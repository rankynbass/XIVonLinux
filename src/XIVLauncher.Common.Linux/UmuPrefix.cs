using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// A Proton (umu-managed) Wine prefix. Carries the umu game-drive environment and
/// translates unix paths to in-prefix (Wine) paths via the prefix's dosdevices drive
/// symlinks (z: -> /, x: -> /home/rankyn, ...). Verified in Milestone 1: all in-prefix
/// arguments must be Windows-style paths, never unix paths.
/// </summary>
public sealed class UmuPrefix
{
    public DirectoryInfo PrefixDir { get; }

    /// <summary>STEAM_COMPAT_DATA_PATH (compatdata/&lt;appid&gt;).</summary>
    public string SteamCompatDataPath { get; }

    /// <summary>STEAM_COMPAT_INSTALL_PATH — required for the game-drive mount; injector
    /// reads of game files fail (DirectoryNotFoundException) without it.</summary>
    public string SteamCompatInstallPath { get; }

    /// <summary>GAMEID (e.g. umu-steam).</summary>
    public string GameId { get; }

    /// <summary>Optional PROTONPATH override; umu auto-selects latest UMU-Proton when null.</summary>
    public string? ProtonPath { get; }

    /// <summary>Optional STEAM_COMPAT_MOUNTS — directories bind-mounted into the prefix by
    /// the Steam/compat launch convention (space-free paths, see SteamLocator).</summary>
    public string? SteamCompatMounts { get; }

    /// <summary>Optional WINEDLLOVERRIDES string (e.g. the DXVK preset + user extras);
    /// null/empty means the variable is not set.</summary>
    public string? WineDllOverrides { get; }

    public UmuPrefix(DirectoryInfo prefixDir, string steamCompatDataPath, string steamCompatInstallPath,
        string gameId = "umu-steam", string? protonPath = null, string? steamCompatMounts = null,
        string? wineDllOverrides = null)
    {
        PrefixDir = prefixDir;
        SteamCompatDataPath = steamCompatDataPath;
        SteamCompatInstallPath = steamCompatInstallPath;
        GameId = gameId;
        ProtonPath = protonPath;
        SteamCompatMounts = steamCompatMounts;
        WineDllOverrides = wineDllOverrides;
    }

    public bool Exists => PrefixDir.Exists;

    /// <summary>
    /// Prefix environment for umu-run invocations (WINEPREFIX, game-drive mount vars).
    /// Prefix-scoped env for in-prefix processes is merged on top of this.
    /// </summary>
    public Dictionary<string, string> UmuEnvironment()
    {
        // GE/Proton convention (Milestone-1 verified command): WINEPREFIX is the compatdata
        // ROOT; the proton script appends pfx/ and the prefix lands at <compatdata>/pfx.
        // PrefixDir is that <compatdata>/pfx dir, used for dosdevices path translation.
        var env = new Dictionary<string, string>
        {
            { "WINEPREFIX", SteamCompatDataPath },
            { "GAMEID", GameId },
            { "STEAM_COMPAT_DATA_PATH", SteamCompatDataPath },
            { "STEAM_COMPAT_INSTALL_PATH", SteamCompatInstallPath },
        };
        if (!string.IsNullOrEmpty(ProtonPath))
            env["PROTONPATH"] = ProtonPath;
        if (!string.IsNullOrEmpty(SteamCompatMounts))
            env["STEAM_COMPAT_MOUNTS"] = SteamCompatMounts;
        if (!string.IsNullOrEmpty(WineDllOverrides))
            env["WINEDLLOVERRIDES"] = WineDllOverrides;
        return env;
    }

    /// <summary>
    /// Translate a unix path to its Wine path by locating the deepest matching drive
    /// symlink in &lt;prefix&gt;/dosdevices (longest prefix wins; z: maps the unix root).
    /// </summary>
    public string UnixToWinePath(string unixPath)
    {
        var drives = GetDriveSymlinks();
        var best = drives.OrderByDescending(d => d.Value.Length)
            .FirstOrDefault(d => unixPath == d.Value || unixPath.StartsWith(d.Value.TrimEnd('/') + "/"));

        if (best.Key is null)
            throw new FileNotFoundException($"No prefix drive maps to unix path '{unixPath}'");

        var root = best.Value.TrimEnd('/');
        var rest = unixPath.Substring(root.Length).TrimStart('/').Replace('/', '\\');
        return rest.Length > 0 ? $"{best.Key}:\\{rest}" : $"{best.Key}:\\";
    }

    /// <summary>
    /// Translate a unix path to a Wine path rooted at the prefix's z: drive (z: -> unix /).
    /// Wine-rooted (z:\home\...) segments are absolute for .NET path APIs, so Path.Join with
    /// them returns the rooted segment; x:-style paths (x:\.local\...) are drive-relative
    /// and concatenate, which breaks the injector's Path.Join(workingDir, ver) reads.
    /// </summary>
    public string UnixToWineRootPath(string unixPath)
    {
        var drives = GetDriveSymlinks();
        if (drives.TryGetValue("z", out var unixRoot))
        {
            var root = unixRoot.TrimEnd('/');
            if (unixPath == root || unixPath.StartsWith(root + "/"))
            {
                var rest = unixPath.Substring(root.Length).TrimStart('/').Replace('/', '\\');
                return rest.Length > 0 ? $"z:\\{rest}" : "z:\\";
            }
        }

        return UnixToWinePath(unixPath);
    }

    private Dictionary<string, string> GetDriveSymlinks()
    {
        var result = new Dictionary<string, string>();
        var dosdevices = Path.Combine(PrefixDir.FullName, "dosdevices");
        if (!Directory.Exists(dosdevices))
            return result;

        foreach (var entry in Directory.GetFileSystemEntries(dosdevices))
        {
            var name = Path.GetFileName(entry).TrimEnd(':');
            if (name.Length == 1 && char.IsAsciiLetter(name[0]))
            {
                var target = SafeSymlinkTarget(entry);
                if (target is not null)
                    result[name.ToLowerInvariant()] = target;
            }
        }

        return result;
    }

    private static string? SafeSymlinkTarget(string path)
    {
        // FileInfo.Exists is false for symlinks that target directories — read the
        // link target directly, no existence gate.
        var fi = new FileInfo(path);
        if (fi.LinkTarget is null)
            return null;
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, fi.LinkTarget));
    }
}
