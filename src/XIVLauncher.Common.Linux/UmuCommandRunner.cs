using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using XIVLauncher.Common.PlatformAbstractions;
using Serilog;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// Runs commands through umu-run inside a UmuPrefix (replaces the legacy wine64
/// RunInPrefix). umu-run auto-selects UMU-Proton (or PROTONPATH); commands are
/// launched with the prefix's game-drive environment.
/// </summary>
public sealed class UmuCommandRunner
{
    private const string DefaultUmuRunPath = "/usr/sbin/umu-run";
    private const string WinetricksVerb = "winetricks";

    private readonly UmuPrefix prefix;
    /// <summary>Executable to invoke: the system umu-run by default, or a managed
    /// zipapp umu-run extracted under the app's tools dir. The app passes the path
    /// as a plain value (layering rule: the library never resolves app storage).</summary>
    private readonly string umuRunPath;
    /// <summary>When set, every launched command's stderr is drained into this file
    /// (append). Plain path string from the app layer.</summary>
    private readonly string? wineLogPath;
    /// <summary>WINEDEBUG value added to the launch environment when set; null leaves
    /// the Proton/GE default (-all) untouched.</summary>
    private readonly string? wineDebug;

    /// <summary>Published for every drained stderr line (file drain runs once; extra
    /// consumers subscribe here instead of competing for the stderr pipe).</summary>
    public event Action<string>? StderrLine;

    public UmuCommandRunner(UmuPrefix prefix, string umuRunPath = DefaultUmuRunPath,
        string? wineLogPath = null, string? wineDebug = null)
    {
        this.prefix = prefix;
        this.umuRunPath = string.IsNullOrEmpty(umuRunPath) ? DefaultUmuRunPath : umuRunPath;
        this.wineLogPath = wineLogPath;
        this.wineDebug = wineDebug;
    }

    /// <summary>Run a command inside the prefix (args are in-prefix, i.e. Windows paths).</summary>
    public Process RunInPrefix(string command, string workingDirectory = "", IDictionary<string, string>? environment = null,
        bool redirectOutput = false, bool writeLog = false)
    {
        var psi = new ProcessStartInfo(umuRunPath);
        psi.Arguments = command;
        return Run(psi, workingDirectory, environment, redirectOutput, writeLog);
    }

    public Process RunInPrefix(IEnumerable<string> args, string workingDirectory = "", IDictionary<string, string>? environment = null,
        bool redirectOutput = false, bool writeLog = false)
    {
        var psi = new ProcessStartInfo(umuRunPath);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return Run(psi, workingDirectory, environment, redirectOutput, writeLog);
    }

    /// <summary>
    /// Create a prefix with the current (auto-selected or PROTONPATH) Proton build:
    /// `umu-run ""` with the prefix env — verified in Milestone 1. Runs from the compatdata
    /// root (drive_c does not exist yet, so PrefixWorkDir() would fail Process.Start).
    /// umu prints "ShellExecuteEx failed: File not found" and exits 0 — harmless.
    /// </summary>
    public Process CreatePrefix()
    {
        var psi = new ProcessStartInfo(umuRunPath) { Arguments = "\"\"" };
        return Run(psi, prefix.SteamCompatDataPath, null, false, true);
    }

    /// <summary>
    /// Ensure the prefix exists and is bootstrapped. Prefix creation is `umu-run ""`;
    /// the bootstrap `umu-run cmd /c dir %userprofile%/Documents > nul` initializes the
    /// user profile/Steam bits (upstream CompatibilityTools.EnsurePrefix shape) — plain
    /// launches without it destabilize game boot (error dialogs at boot).
    /// GE init markers (config_info, version, tracked_files) live at the compatdata root.
    /// </summary>
    public void EnsurePrefix()
    {
        Directory.CreateDirectory(prefix.SteamCompatDataPath);

        var created = File.Exists(Path.Combine(prefix.SteamCompatDataPath, "config_info"))
                      && Directory.Exists(Path.Combine(prefix.PrefixDir.FullName, "dosdevices"));

        Log.Information("Prefix {Prefix} at {Root}: {State}", prefix.PrefixDir.FullName,
            prefix.SteamCompatDataPath, created ? "created" : "missing");

        if (!created)
            CreatePrefix().WaitForExit();

        var bootstrap = RunInPrefix(new[] { "cmd", "/c", "dir %userprofile%/Documents > nul" },
            "", null, false, true);
        bootstrap.WaitForExit();
    }

    /// <summary>Run winetricks verbs inside the prefix (GE/UMU-Proton support).</summary>
    public Process Winetricks(params string[] verbs)
    {
        var psi = new ProcessStartInfo(umuRunPath);
        psi.ArgumentList.Add(WinetricksVerb);
        foreach (var v in verbs)
            psi.ArgumentList.Add(v);
        return Run(psi, "", null, false, true);
    }

    private Process Run(ProcessStartInfo psi, string workingDirectory, IDictionary<string, string>? environment,
        bool redirectOutput, bool writeLog)
    {
        psi.RedirectStandardOutput = redirectOutput;
        psi.RedirectStandardError = writeLog;
        psi.UseShellExecute = false;
        psi.WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? PrefixWorkDir() : workingDirectory;

        var merged = prefix.UmuEnvironment();
        if (wineDebug is not null)
            merged["WINEDEBUG"] = wineDebug;
        if (environment is not null)
            foreach (var kv in environment)
                merged[kv.Key] = kv.Value;

        foreach (var kv in merged)
            psi.EnvironmentVariables[kv.Key] = kv.Value;

        Log.Verbose("umu-run: {FileName} {Arguments}", psi.FileName, BuildArgLine(psi));
        var p = new Process { StartInfo = psi };
        p.Start();
        if (writeLog)
            DrainStderr(p);
        return p;
    }

    /// <summary>Drains the redirected stderr pipe into the log file (append) and
    /// publishes lines to StderrLine. The drain task owns the pipe, so consumers
    /// never compete with the file writer; an undrained pipe stalls the child once
    /// the 64KB buffer fills.</summary>
    private void DrainStderr(Process p)
    {
        _ = Task.Run(() =>
        {
            StreamWriter? writer = string.IsNullOrEmpty(wineLogPath)
                ? null
                : new StreamWriter(wineLogPath, append: true) { AutoFlush = true };
            string? line;
            while ((line = p.StandardError.ReadLine()) is not null)
            {
                writer?.WriteLine(line);
                StderrLine?.Invoke(line);
            }
            writer?.Dispose();
        });
        }

    private string PrefixWorkDir() => Path.Combine(prefix.PrefixDir.FullName, "drive_c");

    private static string BuildArgLine(ProcessStartInfo psi)
    {
        var sb = new StringBuilder(psi.Arguments);
        foreach (var a in psi.ArgumentList)
            sb.Append(' ').Append(a);
        return sb.ToString();
    }
}
