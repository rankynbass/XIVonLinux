using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Serilog;

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

    private string ToolsRoot => Path.Combine(settings.Root.FullName, "tools", "umu");

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
            .Where(d => File.Exists(Path.Combine(d, "umu-run")))
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version(0, 0))
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
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var tag = r.GetProperty("tag_name").GetString() ?? "";
                if (tag.Length == 0 || tag.Contains("rc", StringComparison.OrdinalIgnoreCase)
                    || tag.Contains("beta", StringComparison.OrdinalIgnoreCase)
                    || tag.Contains("alpha", StringComparison.OrdinalIgnoreCase))
                    continue;
                string? url = null;
                foreach (var a in r.GetProperty("assets").EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (name.EndsWith("-zipapp.tar"))
                        url = a.GetProperty("browser_download_url").GetString();
                }
                if (url is not null)
                    releases.Add((tag, url));
                if (releases.Count >= 2)
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "umu releases fetch failed; keeping current tools/umu state");
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
            catch (Exception ex)
            {
                Log.Warning(ex, "umu {Tag} download/extract failed", tag);
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, true); } catch { }
            }
        }

        // Prune versions outside the kept pair (bad-update rollback keeps latest+previous).
        var keep = new HashSet<string>(releases.Select(r => r.tag));
        foreach (var d in Directory.GetDirectories(ToolsRoot))
        {
            if (keep.Contains(Path.GetFileName(d)))
                continue;
            try { Directory.Delete(d, true); }
            catch (Exception ex) { Log.Warning(ex, "umu prune {Dir} failed", d); }
        }
    }

    private async Task DownloadAndExtractAsync(string tag, string url, string target)
    {
        Directory.CreateDirectory(target);
        var tarPath = Path.Combine(ToolsRoot, $"{tag}-zipapp.tar");

        using (var fs = File.Create(tarPath))
        {
            await using var stream = await http.GetStreamAsync(url);
            await stream.CopyToAsync(fs);
        }

        // The tar packs a top-level umu/ directory (umu-run + umu_run.py); extract into a
        // staging dir, then move its contents up so tools/umu/<tag>/umu-run sits flat.
        var staging = Path.Combine(ToolsRoot, $"{tag}-staging");
        Directory.CreateDirectory(staging);
        TarFile.ExtractToDirectory(tarPath, staging, true);
        var packed = Path.Combine(staging, "umu");
        var src = Directory.Exists(packed) ? packed : staging;
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var outPath = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.Move(f, outPath, true);
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
