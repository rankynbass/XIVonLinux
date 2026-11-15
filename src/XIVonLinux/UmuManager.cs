using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Serilog;

using XIVonLinux.Settings;

namespace XIVonLinux;

/// <summary>
/// Manages umu-launcher versions under ~/.xivonlinux/tools/umu (Alex: always keep two —
/// the latest release and the previous one — in case a bad update is pushed). Each
/// release of Open-Wine-Components/umu-launcher ships a zipapp asset
/// umu-launcher-&lt;tag&gt;-zipapp.tar that unpacks to umu-run + umu_run.py; umu-run is a
/// zipapp that runs on the system Python. The launcher invokes the newest extracted
/// umu-run, falling back to the system /usr/sbin/umu-run when nothing is installed.
/// </summary>
public sealed class UmuManager
{
    private const string ReleasesApi =
        "https://api.github.com/repos/Open-Wine-Components/umu-launcher/releases?per_page=10";
    private const string SystemUmuRunPath = "/usr/sbin/umu-run";
    private const string UserAgent = "XIVonLinux/0.1 (FFXIV launcher; https://github.com/rankynbass/XIVonLinux)";

    private readonly SettingsService settings;
    private readonly HttpClient http;

    public UmuManager(SettingsService settings)
    {
        this.settings = settings;
        // GitHub's REST API forbids requests without a User-Agent (403 administrative
        // rules) — a dedicated client, not the shared app HttpClient.
        this.http = new HttpClient();
        this.http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>~/.xivonlinux/tools/umu — the managed zipapp install root.</summary>
    public string ToolsRoot => Path.Combine(settings.Root.FullName, "tools", "umu");

    /// <summary>Installed managed zipapp tags, newest first (Version-parse order).</summary>
    public List<string> InstalledTags()
    {
        if (!Directory.Exists(ToolsRoot))
            return new List<string>();
        var tags = Directory.GetDirectories(ToolsRoot)
            .Select(Path.GetFileName)
            .Where(tag => tag is not null && File.Exists(Path.Combine(ToolsRoot, tag!, "umu-run")))
            .Select(tag => tag!)
            .ToList();
        tags.Sort((verA, verB) =>
        {
            var versionA = Version.TryParse(verA, out var verX) ? verX : new Version(0, 0);
            var versionB = Version.TryParse(verB, out var verY) ? verY : new Version(0, 0);
            return versionB.CompareTo(versionA);
        });
        return tags;
    }

    /// <summary>The umu-run path for a managed tag; null when not installed.</summary>
    public string? PathForTag(string tag)
    {
        var path = Path.Combine(ToolsRoot, tag, "umu-run");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The executable to invoke for prefix/game/injector runs: the newest managed
    /// zipapp umu-run when installed, otherwise the system umu-run. Plain path value handed
    /// to the library (layering rule).</summary>
    public string UmuRunPath() => LatestUmuRunPath() ?? SystemUmuRunPath;

    /// <summary>Path to the newest extracted managed umu-run, or null when none.</summary>
    public string? LatestUmuRunPath()
    {
        if (!Directory.Exists(ToolsRoot))
            return null;
        var installed = Directory.GetDirectories(ToolsRoot)
            .Where(dir => File.Exists(Path.Combine(dir, "umu-run")))
            .OrderByDescending(dir => Version.TryParse(Path.GetFileName(dir), out var parsedVer) ? parsedVer : new Version(0, 0))
            .ToList();
        return installed.Count > 0 ? Path.Combine(installed[0], "umu-run") : null;
    }

    /// <summary>
    /// Ensure the latest two stable releases are extracted at tools/umu/&lt;tag&gt;/ (with
    /// umu-run present); prune anything else. Releases come newest-first from the GitHub
    /// API; rc/beta/alpha tags are skipped. Fetch failures keep the current versions in
    /// place — the launcher degrades to the system umu-run, never blocks the pipeline.
    /// </summary>
    public async Task EnsureLatestTwoAsync()
    {
        var releases = new List<(string tag, string url)>();
        try
        {
            using var resp = await http.GetAsync(ReleasesApi);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString() ?? "";
                if (tag.Length == 0 || tag.Contains("rc", StringComparison.OrdinalIgnoreCase)
                    || tag.Contains("beta", StringComparison.OrdinalIgnoreCase)
                    || tag.Contains("alpha", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? url = null;
                foreach (var verA in release.GetProperty("assets").EnumerateArray())
                {
                    var name = verA.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith("-zipapp.tar"))
                        url = verA.GetProperty("browser_download_url").GetString();
                }
                if (url is not null)
                    releases.Add((tag, url));
                if (releases.Count >= 2)
                    break;
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "umu releases fetch failed; keeping current tools/umu state");
            return;
        }

        Directory.CreateDirectory(ToolsRoot);

        foreach (var (tag, url) in releases)
        {
            var dir = Path.Combine(ToolsRoot, tag);
            if (File.Exists(Path.Combine(dir, "umu-run")))
                continue;
            try
            {
                await DownloadAndExtractAsync(tag, url, dir);
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "umu {Tag} download/extract failed", tag);
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, true); } catch { }
            }
        }

        // Prune versions outside the kept pair (bad-update rollback keeps latest+previous).
        var keep = new HashSet<string>(releases.Select(release => release.tag));
        foreach (var dir in Directory.GetDirectories(ToolsRoot))
        {
            if (keep.Contains(Path.GetFileName(dir)))
                continue;
            try { Directory.Delete(dir, true); }
            catch (Exception exception) { Log.Warning(exception, "umu prune {Dir} failed", dir); }
        }
    }

    private async Task DownloadAndExtractAsync(string tag, string url, string target)
    {
        Directory.CreateDirectory(target);
        var tarPath = Path.Combine(ToolsRoot, $"{tag}-zipapp.tar");

        using (var fileStream = File.Create(tarPath))
        {
            await using var stream = await http.GetStreamAsync(url);
            await stream.CopyToAsync(fileStream);
        }

        // The tar packs a top-level umu/ directory (umu-run + umu_run.py); extract into a
        // staging dir, then move its contents up so tools/umu/<tag>/umu-run sits flat.
        var staging = Path.Combine(ToolsRoot, $"{tag}-staging");
        Directory.CreateDirectory(staging);
        TarFile.ExtractToDirectory(tarPath, staging, true);
        var packed = Path.Combine(staging, "umu");
        var src = Directory.Exists(packed) ? packed : staging;
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var outPath = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.Move(file, outPath, true);
        }
        Directory.Delete(staging, true);
        File.Delete(tarPath);

        var umuRun = Path.Combine(target, "umu-run");
        if (File.Exists(umuRun))
            File.SetUnixFileMode(umuRun,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Log.Information("Managed umu-launcher {Tag} installed at {Dir}", tag, target);
    }
}
