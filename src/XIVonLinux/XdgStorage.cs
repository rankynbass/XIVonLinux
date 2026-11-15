using System;
using System.IO;
using System.Linq;
using Serilog;

namespace XIVonLinux;

/// <summary>XDG storage roots, one-time app layout (Alex's XDG rule):
/// settings files (.toml, accounts.db, accounts.deck) in ~/.config/xivonlinux,
/// patches and temporary downloads in ~/.cache/xivonlinux, main storage in
/// ~/.local/share/xivonlinux (XDG_DATA_HOME/XDG_CONFIG_HOME/XDG_CACHE_HOME
/// honored when set). The pre-XDG ~/.xivonlinux tree is migrated once on startup:
/// config files move to config, patches/ and dalamudVfs.db to cache, everything
/// else (prefixes, tools, logs, dalamud) to data; the emptied ~/.xivonlinux is
/// replaced with a symlink to the data root so stored path strings keep working.
/// Pure static: no settings object, safe to call before any storage exists.</summary>
public static class XdgStorage
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // .NET maps these to the XDG dirs on Linux: ApplicationData -> ~/.config
    // (XDG_CONFIG_HOME honored when set), LocalApplicationData -> ~/.local/share,
    // UserProfile -> ~ (cache = UserProfile/.cache). ApplicationData can come back
    // empty on exotic HOME/passwd setups (sandboxed probes) — fall back to HOME/.config.
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xivonlinux");
    public static string ConfigRoot => ConfigBase() + "/xivonlinux";
    public static string CacheRoot => CacheBase() + "/xivonlinux";

    // No SpecialFolder for cache: check XDG_CACHE_HOME, default to UserProfile/.cache.
    private static string CacheBase()
    {
        var env = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return string.IsNullOrEmpty(env) ? Path.Combine(Home, ".cache") : env;
    }

    private static string ConfigBase()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? Path.Combine(Home, ".config") : appData;
    }

    /// <summary>One-time migration from ~/.xivonlinux; no-op when the legacy dir
    /// is absent (fresh install) or already a symlink.</summary>
    public static void MigrateFromLegacy()
    {
        var legacy = new DirectoryInfo(Path.Combine(Home, ".xivonlinux"));
        if (!legacy.Exists)
            return;
        if (legacy.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return;

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(CacheRoot);

        foreach (var entry in legacy.EnumerateFileSystemInfos().ToList())
        {
            var target = IsConfigFile(entry.Name) ? ConfigRoot
                : (entry.Name == "dalamudVfs.db" || (entry is DirectoryInfo && entry.Name == "patches"))
                    ? CacheRoot
                    : DataRoot;
            var dest = Path.Combine(target, entry.Name);
            MoveEntry(entry.FullName, dest);
            Log.Information("XDG migration: {Old} -> {New}", entry.FullName, dest);
        }

        if (!legacy.EnumerateFileSystemInfos().Any())
        {
            legacy.Delete();
            File.CreateSymbolicLink(legacy.FullName, DataRoot);
            Log.Information("XDG migration: ~/.xivonlinux is now a symlink to {Data}", DataRoot);
        }
    }

    /// <summary>Settings-era files: settings.toml (+ .old/.bad quarantines),
    /// accounts.db (+ quarantines), accounts.deck, legacy accounts.toml.</summary>
    private static bool IsConfigFile(string name) =>
        name.StartsWith("settings.toml") || name.StartsWith("accounts.db") ||
        name == "accounts.deck" || name.StartsWith("accounts.toml");

    /// <summary>Rename the entry into the target dir; never clobbers an existing
    /// target, and the entry stays put when the target is taken (user can clean up).</summary>
    private static void MoveEntry(string src, string dst)
    {
        if (File.Exists(dst) || Directory.Exists(dst))
            return;
        if (Directory.Exists(src))
            Directory.Move(src, dst);
        else
            File.Move(src, dst);
    }
}
