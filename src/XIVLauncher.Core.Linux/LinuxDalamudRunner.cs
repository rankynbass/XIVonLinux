using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Core.Linux;

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

    public LinuxDalamudRunner(UmuPrefix prefix)
    {
        this.prefix = prefix;
        runner = new UmuCommandRunner(prefix);
    }

    public Process? Run(FileInfo runnerExe, bool fakeLogin, bool noPlugins, bool noThirdPlugins, FileInfo gameExe,
        string gameArgs, string additionalInjectorArgs, IDictionary<string, string> environment,
        DalamudLoadMethod loadMethod, DalamudStartInfo dalamudStartInfo)
    {
        var args = BuildInjectorArgs(runnerExe, gameExe, gameArgs, environment, dalamudStartInfo, loadMethod,
            fakeLogin, noPlugins, noThirdPlugins, additionalInjectorArgs);

        var proc = runner.RunInPrefix(args, Path.GetDirectoryName(runnerExe.FullName), environment, true, true);
        proc.WaitForExit();

        var stdout = proc.StandardOutput.ReadToEnd();
        var result = JsonConvert.DeserializeObject<DalamudInjectorResult>(stdout);
        if (result?.WinProcessId is null)
            return null;

        return MapWinePidToGameProcess(result.WinProcessId.Value, proc);
    }

    /// <summary>Injection mode string passed to the injector (--mode=).</summary>
    private static string LoadMethodString(DalamudLoadMethod loadMethod) => "inject";

    private List<string> BuildInjectorArgs(FileInfo runnerExe, FileInfo gameExe, string gameArgs,
        IDictionary<string, string> environment, DalamudStartInfo startInfo, DalamudLoadMethod loadMethod,
        bool fakeLogin, bool noPlugins, bool noThirdPlugins, string additionalInjectorArgs)
    {
        var args = new List<string>
        {
            DalamudInjectorArgs.LAUNCH,
            DalamudInjectorArgs.Mode(LoadMethodString(loadMethod)),
            DalamudInjectorArgs.Game(prefix.UnixToWinePath(gameExe.FullName)),
            DalamudInjectorArgs.WorkingDirectory(prefix.UnixToWinePath(Path.GetDirectoryName(startInfo.WorkingDirectory)!)),
            DalamudInjectorArgs.ConfigurationPath(prefix.UnixToWinePath(startInfo.ConfigurationPath)),
            DalamudInjectorArgs.LoggingPath(prefix.UnixToWinePath(startInfo.LoggingPath)),
            DalamudInjectorArgs.PluginDirectory(prefix.UnixToWinePath(startInfo.PluginDirectory)),
            DalamudInjectorArgs.AssetDirectory(prefix.UnixToWinePath(startInfo.AssetDirectory)),
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
        var winedbg = runner.RunInPrefix(new List<string> { "winedbg", "info", "proc" }, "", null, true, false);
        winedbg.WaitForExit();
        var outp = winedbg.StandardOutput.ReadToEnd();
        var lines = outp.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var l in lines)
        {
            var sp = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length == 2 && int.TryParse(sp[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var w) &&
                int.TryParse(sp[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u) && w == winePid)
            {
                return Process.GetProcessById(u);
            }
        }

        return null;
    }

    private sealed class DalamudInjectorResult
    {
        [JsonProperty("startup_info")]
        public DalamudStartInfo? StartupInfo { get; set; }

        [JsonProperty("process_id")]
        public int? WinProcessId { get; set; }
    }
}
