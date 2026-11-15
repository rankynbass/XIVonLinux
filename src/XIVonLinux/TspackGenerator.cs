using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

using XIVLauncher.Common;
using XIVLauncher.Common.Game;

using ZipArchive = System.IO.Compression.ZipArchive;

using XIVonLinux.Settings;

namespace XIVonLinux;

/// <summary>Diagnostic trouble pack, XIVLauncher.Core PackGenerator parity.
/// Zips every *.log in the logs folder (skipping .old. rotations), plus a
/// trouble.json settings/version snapshot. Output: logs/trouble-&lt;timestamp&gt;.tspack.</summary>
public static class TspackGenerator
{
    public static string SavePack(LaunchSettings settings)
    {
        var logsDir = Path.Combine(XdgStorage.DataRoot, "logs");
        Directory.CreateDirectory(logsDir);
        var outFile = new FileInfo(Path.Combine(logsDir, $"trouble-{DateTime.Now:yyyyMMddHHmmss}.tspack"));

        using var archive = ZipFile.Open(outFile.FullName, ZipArchiveMode.Create);

        // trouble.json first: the settings/version snapshot
        var json = JsonSerializer.Serialize(BuildTroubleSnapshot(settings), new JsonSerializerOptions { WriteIndented = true });
        var troubleEntry = archive.CreateEntry("trouble.json");
        using (var entryStream = troubleEntry.Open())
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            entryStream.Write(bytes, 0, bytes.Length);
        }

        // Every log file in logs/, skipping .old. rotations and non-.log extras (crash dumps)
        foreach (var file in Directory.GetFiles(logsDir, "*.log").Order())
        {
            if (file.Contains(".old."))
                continue;
            var info = new FileInfo(file);
            using var stream = info.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = archive.CreateEntry(info.Name);
            using var entryStream = entry.Open();
            stream.CopyTo(entryStream);
        }

        return outFile.FullName;
    }

    private static object BuildTroubleSnapshot(LaunchSettings settings)
    {
        var gameDir = new DirectoryInfo(settings.GamePath);
        string ffxivVer = "unavailable", ffxivVerBackup = "unavailable", bootVer = "unavailable";
        try
        {
            if (gameDir.Exists && gameDir.GetDirectories().Any(d => d.Name == "game"))
            {
                ffxivVer = Repository.Ffxiv.GetVer(gameDir);
                ffxivVerBackup = Repository.Ffxiv.GetVer(gameDir, true);
                bootVer = Repository.Boot.GetVer(gameDir);
            }
        }
        catch (Exception)
        {
            // A broken install should not stop the pack from being written
        }

        return new
        {
            when = DateTime.Now.ToString("s"),
            launcherVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            launcherGitHash = GetGitShortHash(),
            gamePath = settings.GamePath,
            ffxivVersion = ffxivVer,
            ffxivVersionBackup = ffxivVerBackup,
            bootVersion = bootVer,
            language = settings.Language,
            protonPath = settings.ProtonPath,
            managedProton = settings.ManagedProton,
            managedUmu = settings.ManagedUmu,
            umuTag = settings.UmuTag,
            umuPath = settings.UmuPath,
            prefixPath = settings.PrefixPath,
            enableDxvk = settings.EnableDxvk,
            extraWineDllOverrides = settings.ExtraWineDllOverrides,
            wineDebugEnv = settings.WineDebugEnv,
            dalamudEnabled = settings.DalamudEnabled,
            loadMethod = settings.LoadMethod,
            loadDelay = settings.LoadDelay,
            branchTrack = settings.BranchTrack,
            maxDownloadSpeedKibps = settings.MaxDownloadSpeedKibps,
            maxConnections = settings.MaxConnections,
            additionalArgs = settings.AdditionalArgs,
            hacks = new
            {
                settings.FixLdp,
                settings.FixXModifiers,
                settings.FixLibIcuuc,
                settings.FixLocale,
                settings.DontUseSystemTz,
            },
        };
    }

    /// <summary>Short git hash of the checkout the running binary lives in; "unknown"
    /// when the app is not inside a git working tree.</summary>
    private static string GetGitShortHash()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
                dir = dir.Parent;
            if (dir is null)
                return "unknown";
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{dir.FullName}\" rev-parse --short HEAD",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (proc is null)
                return "unknown";
            var hash = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return hash.Length == 0 ? "unknown" : hash;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }
}
