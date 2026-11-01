using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// Prefix-scoped environment variables for processes running inside the prefix
/// (e.g. DOTNET_ROOT / DALAMUD_RUNTIME as Wine paths for the injector), as opposed
/// to umu's game-drive env (UmuPrefix.UmuEnvironment).
/// </summary>
public sealed class UmuInPrefixEnvironment
{
    private readonly UmuPrefix prefix;

    public UmuInPrefixEnvironment(UmuPrefix prefix)
    {
        this.prefix = prefix;
    }

    public string WinePath(string unixPath) => prefix.UnixToWinePath(unixPath);

    public Dictionary<string, string> Build(params (string Key, string WinePath)[] vars)
    {
        var env = new Dictionary<string, string>();
        foreach (var (key, winePath) in vars)
            env[key] = winePath;

        // Tell GE-Proton/umu to run the command inside the prefix (not as a game launch).
        env["PROTON_VERB"] = "runinprefix";
        return env;
    }
}
