using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// Unquoted injector arg builders. The reference DalamudInjectorArgs wraps paths in literal
/// quotes ($"--game=\"{path}\"") for string-joined command lines; our arguments go through
/// ArgumentList (argv) unquoted, where those quote characters arrive literal inside the
/// value and corrupt the injector's path parsing.
/// </summary>
internal static class LinuxInjectorArgs
{
    public static string Game(string path) => $"--game={path}";
    public static string LoggingPath(string path) => $"--logpath={path}";
    public static string WorkingDirectory(string path) => $"--dalamud-working-directory={path}";
    public static string ConfigurationPath(string path) => $"--dalamud-configuration-path={path}";
    public static string PluginDirectory(string path) => $"--dalamud-plugin-directory={path}";
    public static string AssetDirectory(string path) => $"--dalamud-asset-directory={path}";
}

/// <summary>
/// Runs the Windows .NET Dalamud injector inside the Proton prefix via umu
/// (replaces legacy UnixDalamudRunner). Verified command shape in Milestone 1;
/// returns the game Process with its unix PID, mapped from the Wine PID in the
/// injector's JSON output via `umu-run winedbg 'info proc'`.
/// </summary>
public sealed class LinuxDalamudRunner : IDalamudRunner
{
    private readonly UmuPrefix prefix;
    private readonly UmuCommandRunner runner;
    private string capturedUmuOutput = "";
    private string capturedUmuError = "";
    private string gameExeName = "";

    public LinuxDalamudRunner(UmuPrefix prefix)
    {
        this.prefix = prefix;
        runner = new UmuCommandRunner(prefix);
    }

    /// <summary>Raw stdout captured from the injector's umu-run invocation (the injector
    /// prints its JSON result on stdout; kept for spike/UI diagnostics).</summary>
    public string CapturedUmuOutput() => capturedUmuOutput;

    /// <summary>Raw stderr captured from the injector's umu-run invocation (proton/umu
    /// diagnostics, injector console output).</summary>
    public string CapturedUmuError() => capturedUmuError;

    public Process? Run(FileInfo runnerExe, bool fakeLogin, bool noPlugins, bool noThirdPlugins, FileInfo gameExe,
        string gameArgs, string additionalInjectorArgs, IDictionary<string, string> environment,
        DalamudLoadMethod loadMethod, DalamudStartInfo dalamudStartInfo)
    {
        gameExeName = gameExe.Name;
        var args = BuildInjectorArgs(runnerExe, gameExe, gameArgs, environment, dalamudStartInfo, loadMethod,
            fakeLogin, noPlugins, noThirdPlugins, additionalInjectorArgs);

        var proc = runner.RunInPrefix(args, Path.GetDirectoryName(runnerExe.FullName), environment, true, true);

        // The injector prints its JSON result as soon as the game is launched, then keeps
        // the stream open while it monitors the game. Drain both pipes on background tasks
        // (so the injector never stalls on unread pipes), capture stdout line-by-line, and
        // map the Wine PID to the unix game process WHILE THE GAME IS ALIVE. ReadToEnd-then-map
        // was the old shape: EOF arrives exactly at game exit, so mapping always found nothing.
        var jsonFound = new ManualResetEventSlim(false);
        DalamudInjectorResult? result = null;
        var outCapture = new StringBuilder();
        _ = Task.Run(() =>
        {
            string? line;
            while ((line = proc.StandardOutput.ReadLine()) is not null)
            {
                outCapture.AppendLine(line);
                Log.Information("Injector stdout: {Line}", line.TrimEnd('\r'));
                if (result is null)
                {
                    var json = ExtractLastJsonObject(line);
                    if (json is not null)
                    {
                        result = JsonConvert.DeserializeObject<DalamudInjectorResult>(json);
                        jsonFound.Set();
                    }
                }
            }
            capturedUmuOutput = outCapture.ToString();
        });
        _ = Task.Run(() => capturedUmuError = proc.StandardError.ReadToEnd());

        jsonFound.Wait(TimeSpan.FromSeconds(180));
        var winePid = result?.WinProcessId ?? result?.Pid;
        if (winePid is null)
        {
            Log.Error("Injector produced no result JSON within 180s; captured: {Out}", capturedUmuOutput);
            return null;
        }

        Log.Information("Injector result: wine pid {P}", winePid.Value);
        return MapWinePidToGameProcess(winePid.Value, proc);
    }

    private static string? ExtractLastJsonObject(string output)
    {
        var end = output.LastIndexOf('}');
        if (end < 0)
            return null;
        var start = output.LastIndexOf('{', end);
        if (start < 0 || start > end)
            return null;
        return output.Substring(start, end - start + 1);
    }

    /// <summary>Injection mode string passed to the injector (--mode=).</summary>
    private static string LoadMethodString(DalamudLoadMethod loadMethod) => "inject";

    private List<string> BuildInjectorArgs(FileInfo runnerExe, FileInfo gameExe, string gameArgs,
        IDictionary<string, string> environment, DalamudStartInfo startInfo, DalamudLoadMethod loadMethod,
        bool fakeLogin, bool noPlugins, bool noThirdPlugins, string additionalInjectorArgs)
    {
        var args = new List<string>
        {
            // umu-run's first positional is the in-prefix executable (M1-verified command:
            // umu-run x:\...\Dalamud.Injector.exe launch --mode=inject ...). Rooted z:\ paths
            // (z: -> unix /) are absolute for .NET: the injector's Path.Join(workingDir, ver)
            // reads concatenate on drive-relative x:\ paths and fail with "Invalid name".
            prefix.UnixToWineRootPath(runnerExe.FullName),
            DalamudInjectorArgs.LAUNCH,
            DalamudInjectorArgs.Mode(LoadMethodString(loadMethod)),
            LinuxInjectorArgs.Game(prefix.UnixToWineRootPath(gameExe.FullName)),
            // --dalamud-working-directory is the Hooks dir itself (where dalamud.dll lives);
            // the reference runner passes startInfo.WorkingDirectory directly, not its parent.
            LinuxInjectorArgs.WorkingDirectory(prefix.UnixToWineRootPath(startInfo.WorkingDirectory)),
            LinuxInjectorArgs.ConfigurationPath(prefix.UnixToWineRootPath(startInfo.ConfigurationPath)),
            LinuxInjectorArgs.LoggingPath(prefix.UnixToWineRootPath(startInfo.LoggingPath)),
            LinuxInjectorArgs.PluginDirectory(prefix.UnixToWineRootPath(startInfo.PluginDirectory)),
            LinuxInjectorArgs.AssetDirectory(prefix.UnixToWineRootPath(startInfo.AssetDirectory)),
            DalamudInjectorArgs.ClientLanguage((int)startInfo.Language),
            DalamudInjectorArgs.DelayInitialize(startInfo.DelayInitializeMs),
        };

        if (noThirdPlugins)
            args.Add(DalamudInjectorArgs.NO_THIRD_PARTY);
        if (noPlugins)
            args.Add(DalamudInjectorArgs.NO_PLUGIN);
        if (fakeLogin)
            args.Add(DalamudInjectorArgs.FAKE_ARGUMENTS);

        if (!string.IsNullOrWhiteSpace(additionalInjectorArgs))
            foreach (var a in additionalInjectorArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                args.Add(a);

        args.Add("--");
        foreach (var a in gameArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            args.Add(a);

        return args;
    }

    private Process? MapWinePidToGameProcess(int winePid, Process injectorProc)
    {
        // winedbg is dropped: GE/UMU winedbg hangs on `info proc` (winedbg.WaitForExit()
        // never completes), so the comm-match below is the sole mapping path.
        var candidates = Process.GetProcessesByName(gameExeName);
        Log.Information("comm-match '{Name}': {N} candidates", gameExeName, candidates.Length);

        Process? best = null;
        DateTime bestStart = default;
        foreach (var p in candidates)
        {
            try
            {
                var st = p.StartTime;
                Log.Information("  pid {P} start {S}", p.Id, st);
                if (best is null || st > bestStart)
                {
                    bestStart = st;
                    best = p;
                }
            }
            catch (Exception e)
            {
                Log.Information("  pid {P} start time unavailable: {E}", p.Id, e.Message);
            }
        }

        return best;
    }

    private sealed class DalamudInjectorResult
    {
        [JsonProperty("startup_info")]
        public DalamudStartInfo? StartupInfo { get; set; }

        // The in-prefix injector (LINUX platform path) prints {"pid": N, "handle": 0} —
        // the Windows launcher shape uses process_id. Both spellings accepted; pid is
        // the Wine process id, which MapWinePidToGameProcess resolves to the unix game pid.
        [JsonProperty("process_id")]
        public int? WinProcessId { get; set; }

        [JsonProperty("pid")]
        public int? Pid { get; set; }
    }
}
