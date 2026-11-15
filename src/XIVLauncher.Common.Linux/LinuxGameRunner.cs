using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using XIVLauncher.Common;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// Launches the game through umu/Proton (replaces legacy UnixGameRunner's wine64 path).
/// Arguments are in-prefix Windows paths translated via the prefix drive map.
/// </summary>
public sealed class LinuxGameRunner : IGameRunner
{
    private const string AdditionalArgsSeparator = "--";

    private readonly UmuPrefix prefix;
    private readonly UmuCommandRunner runner;
    /// <summary>Extra environment variables merged into every launch, overriding the
    /// prefix environment. Plain types only (layering rule: the library never references
    /// app types); the app parses user args into these pairs.</summary>
    private readonly Dictionary<string, string>? extraEnv;

    public LinuxGameRunner(UmuPrefix prefix, string umuRunPath = "/usr/sbin/umu-run",
        string? wineLogPath = null, string? wineDebug = null,
        IEnumerable<KeyValuePair<string, string>>? extraEnv = null)
    {
        this.prefix = prefix;
        runner = new UmuCommandRunner(prefix, umuRunPath, wineLogPath, wineDebug);
        this.extraEnv = extraEnv is null ? null : new Dictionary<string, string>(extraEnv);
    }

    public Process? Start(string path, string workingDirectory, string arguments, IDictionary<string, string> environment,
        DpiAwareness dpiAwareness, string additionalInjectorArgs)
    {
        var gameExeWine = prefix.UnixToWinePath(path);

        var args = new List<string>();

        // umu-run's first positional arg is the in-prefix executable; it must be the
        // Wine-style path (a unix path makes the launch silent). Then the game args.
        args.Add(gameExeWine);

        foreach (var a in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            args.Add(a);

        // -- separates user game args from in-prefix injector args (umu/Proton convention,
        // mirrors --fake-arguments ... -- in the verified Milestone 1 command).
        if (!string.IsNullOrWhiteSpace(additionalInjectorArgs))
        {
            args.Add(AdditionalArgsSeparator);
            foreach (var a in additionalInjectorArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                args.Add(a);
        }

        // Merge user extra env vars on top of the launcher-built environment so the
        // game process sees them as real environment variables.
        var mergedEnv = environment is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(environment);
        if (extraEnv is not null)
            foreach (var kv in extraEnv)
                mergedEnv[kv.Key] = kv.Value;

        return runner.RunInPrefix(args, PrefixWorkingDirectory(workingDirectory), mergedEnv, false, true);
    }

    private string PrefixWorkingDirectory(string workingDirectory)
    {
        // umu commands are invoked from unix working dirs; the in-prefix cwd is the
        // game's prefix drive_c, matching umu's own launch behaviour.
        return string.IsNullOrEmpty(workingDirectory)
            ? Path.Combine(prefix.PrefixDir.FullName, "drive_c")
            : workingDirectory;
    }
}
