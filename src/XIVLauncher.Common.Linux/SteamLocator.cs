using System;
using System.Collections.Generic;
using System.IO;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// Locates the Steam install of FFXIV (appid 39210): Steam library root, appmanifest_39210.acf
/// (VDF), then steamapps/common/&lt;installdir&gt; with game/ffxiv_dx11.exe. This is the layout
/// verified on this machine; XIVLauncher.Core's Eto path had the user pick GamePath manually.
/// </summary>
public sealed class SteamInstall
{
    public const uint XivAppId = 39210;

    /// <summary>steamapps/common/&lt;installdir&gt; — XIVLauncher.Common's gamePath shape (game/ + boot/ inside).</summary>
    public DirectoryInfo GamePath { get; }

    /// <summary>Steam library root (the dir that contains steamapps/).</summary>
    public string SteamRoot { get; }

    public string CompatDataPath => Path.Combine(SteamRoot, "steamapps", "compatdata", XivAppId.ToString());

    public static SteamInstall? Detect()
    {
        var root = FindSteamRoot();
        if (root is null)
            return null;

        var acf = Path.Combine(root, "steamapps", $"appmanifest_{XivAppId}.acf");
        if (!File.Exists(acf))
            return null;

        var fields = ParseTopLevelVdf(File.ReadAllText(acf));
        if (!fields.TryGetValue("StateFlags", out var flags) || flags != "4") // 4 = fully installed
            return null;
        if (!fields.TryGetValue("installdir", out var installDir) || string.IsNullOrEmpty(installDir))
            return null;

        var gameDir = new DirectoryInfo(Path.Combine(root, "steamapps", "common", installDir));
        // Steam's common dir is commonly a symlink to the real game dir (e.g.
        // /games/final-fantasy-xiv/ffxiv). Resolve it: the symlinked name carries spaces
        // ("FINAL FANTASY XIV Online"), and umu's shell-joining quotes args with spaces —
        // the literal quote chars land inside the injector's --game path ("Invalid name").
        var resolved = gameDir.ResolveLinkTarget(true) as DirectoryInfo ?? gameDir;
        if (!File.Exists(Path.Combine(resolved.FullName, "game", "ffxiv_dx11.exe")))
            return null;

        return new SteamInstall(resolved, root);
    }

    private static string? FindSteamRoot()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[]
        {
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam"),
        })
        {
            if (Directory.Exists(Path.Combine(candidate, "steamapps")))
                return candidate;
        }

        return null;
    }

    /// <summary>
    /// Flat top-level key/value pairs of an appmanifest acf. Nested blocks (InstalledDepots,
    /// SharedDepots) are skipped — depth tracking keeps only depth-1 pairs.
    /// </summary>
    private static Dictionary<string, string> ParseTopLevelVdf(string content)
    {
        var result = new Dictionary<string, string>();
        var depth = 0;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "{") { depth++; continue; }
            if (line == "}") { depth--; continue; }
            if (depth != 1) continue;

            var parts = line.Split('"');
            if (parts.Length >= 4)
                result[parts[1]] = parts[3];
        }

        return result;
    }

    private SteamInstall(DirectoryInfo gamePath, string steamRoot)
    {
        GamePath = gamePath;
        SteamRoot = steamRoot;
    }
}
