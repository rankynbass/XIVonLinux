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

    public LinuxGameRunner(UmuPrefix prefix)
    {
        this.prefix = prefix;
        runner = new UmuCommandRunner(prefix);
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

        return runner.RunInPrefix(args, PrefixWorkingDirectory(workingDirectory), environment, false, true);
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
