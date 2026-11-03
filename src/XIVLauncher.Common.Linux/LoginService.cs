using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Encryption;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Game.Patch.Acquisition.Aria;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// Milestone 4: real Square Enix login + patching for XIVonLinux.
/// Wraps XIVLauncher.Common Launcher (OauthLogin -> RegisterSession -> LoginResult)
/// and the shared patch pipeline (AriaPatchAcquisition + PatchInstaller + PatchManager).
/// Steam-account login is not supported yet (LinuxSteamStub is not a real Steamworks
/// binding — Launcher throws SteamException when isSteam is true), so login runs with
/// SE credentials only. Credentials are passed in-memory and never persisted.
/// </summary>
public sealed class LoginService : IDisposable
{
    private const string FrontierUrlTemplate = "https://sqex-fragment.legacy.net/v650/index.html?rc_lang=en&time={1}";

    private readonly Launcher launcher;
    private readonly Storage storage;
    private readonly string configPath;
    private readonly UmuPrefix prefix;
    private readonly ClientLanguage language;
    private AriaPatchAcquisition? acquisition;

    /// <summary>Library-side service: takes plain values (XIVLauncher.Common.Storage,
    /// the character-config folder path), never the app's settings object.</summary>
    public LoginService(Storage storage, string configPath, UmuPrefix prefix, ClientLanguage language = ClientLanguage.English)
    {
        this.storage = storage;
        this.configPath = configPath;
        this.prefix = prefix;
        this.language = language;
        this.launcher = new Launcher(new SteamworksSteam(), new NullUniqueIdCache(), FrontierUrlTemplate, "en");
    }

    /// <summary>Launch gate/login-server availability (upstream GetGateStatus shape).</summary>
    public async Task<bool> GateAvailableAsync()
    {
        try
        {
            var gate = await launcher.GetGateStatus(language);
            return gate.Status;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not obtain gate status");
            return false;
        }
    }

    /// <summary>News headlines + rotating banners from the frontier API, in the UI language.</summary>
    public async Task<(Headlines, IReadOnlyList<Banner>)> GetNewsAsync()
    {
        var banners = await Headlines.GetBanners(launcher, language);
        var headlines = await Headlines.GetNews(launcher, language);
        return (headlines, banners);
    }

    /// <summary>
    /// Login: Steam-account path (isSteam=true — Steamworks auth ticket via
    /// SteamClient.Init(39210/312060 for free trial) + Ticket.Get) or SE credentials
    /// (OauthLogin + RegisterSession). Launcher.Login drives Steam init, BLoggedOn
    /// check and ticket retries; SteamException is thrown upstream on init/logon
    /// failure. Username is the SE/Steam account name; Steam login ignores
    /// password/OTP. Credentials are passed in-memory and never persisted.
    /// </summary>
    /// <summary>
    /// Fresh-install game folder rule: a missing game directory is created, but only
    /// ONE level deep — its immediate parent must already exist. If the parent is
    /// missing too, this returns the error naming it (no recursive tree creation).
    /// Returns null when the directory exists (or was created).
    /// </summary>
    public static string? EnsureGameDir(string gamePathFull)
    {
        var dir = new DirectoryInfo(gamePathFull);
        if (dir.Exists)
            return null;

        var parent = dir.Parent;
        if (parent is null || !parent.Exists)
            return $"{(parent?.FullName ?? dir.Root.FullName)} does not exist — cannot create the game directory {dir.FullName}";

        Directory.CreateDirectory(dir.FullName);
        return null;
    }

    public async Task<Launcher.LoginResult> LoginAsync(LoginRequest request, DirectoryInfo gamePath, bool forceBaseVersion = false)
    {
        return await launcher.Login(
            request.Username, request.Password, request.OneTimePassword,
            request.SteamAccount, useCache: false, gamePath, forceBaseVersion,
            request.FreeTrial, language);
    }

    /// <summary>Boot patch list for an (empty) install: CheckBootVersion with
    /// forceBaseVersion=true — missing boot ver file reports base ver, so the server
    /// answers with every boot patch from base to latest. No boot hashes involved.</summary>
    public async Task<PatchListEntry[]> CheckBootPatchesAsync(DirectoryInfo gamePath)
    {
        return await launcher.CheckBootVersion(gamePath, forceBaseVersion: true);
    }

    /// <summary>
    /// Download and install a patch list into the game folder: aria2 RPC acquisition
    /// into the patch store (~/.xivonlinux/patches), in-process RemotePatchInstaller
    /// writing repo files (boot/ or game/) into gamePath, per-block SHA1 verification
    /// by PatchManager. status receives progress lines. Repository.Boot drives the
    /// boot dir, Repository.Ffxiv the game dir; uid is the patch unique id ("" for boot).
    /// </summary>
    /// <summary>
    /// The PatchManager currently running an install (null between installs). The UI
    /// polls its public slots/Progresses/Speeds arrays via ProgressSnapshot to drive
    /// the download progress popup.
    /// </summary>
    public static PatchManager? CurrentPatcher { get; private set; }

    /// <summary>One uncompleted download as shown in the progress popup.</summary>
    public sealed class DownloadSlotInfo
    {
        public string VersionId = "";
        public long Total;
        public long Downloaded;
        public double SpeedBps;
        public string State = "queued"; // downloading | verifying | queued
    }

    /// <summary>
    /// Snapshot of uncompleted downloads for the progress popup: in-progress slots
    /// (live bytes/speed; "Done" slots are excluded so finished bars disappear),
    /// then queued patches not yet started. Sorted by downloaded bytes descending,
    /// capped to 5 rows.
    /// </summary>
    public static List<DownloadSlotInfo> ProgressSnapshot(PatchManager? patcher)
    {
        var rows = new List<DownloadSlotInfo>();
        if (patcher is null)
            return rows;

        for (var i = 0; i < PatchManager.MAX_DOWNLOADS_AT_ONCE; i++)
        {
            var act = patcher.Actives[i];
            if (act is null)
                continue;
            var slot = patcher.Slots[i];
            if (slot == PatchManager.SlotState.Done)
                continue;
            rows.Add(new DownloadSlotInfo
            {
                VersionId = act.Patch.VersionId,
                Total = act.Patch.Length,
                Downloaded = patcher.Progresses[i],
                SpeedBps = patcher.Speeds[i],
                State = slot == PatchManager.SlotState.InProgress ? "downloading" : "verifying",
            });
        }

        foreach (var d in patcher.Downloads)
        {
            if (d.State == PatchState.Nothing)
                rows.Add(new DownloadSlotInfo { VersionId = d.Patch.VersionId, Total = d.Patch.Length, Downloaded = 0, SpeedBps = 0, State = "queued" });
        }

        return rows.OrderByDescending(r => r.State == "queued" ? -1 : r.Downloaded).Take(5).ToList();
    }

    /// <summary>
    /// Pause/abort the running install: PatchManager.StartCancellation cancels all
    /// aria2 downloads (the running InstallPatchesAsync then returns false). Called by
    /// the progress popup's Cancel button and by its window close (X).
    /// </summary>
    public static void CancelCurrentDownloads()
    {
        CurrentPatcher?.StartCancellation();
    }

    public async Task<bool> InstallPatchesAsync(Repository repo, PatchListEntry[] patches, string uid, string gameLocation, string patchFolder, Action<string> status)
    {
        var gamePath = new DirectoryInfo(gameLocation);
        var patchStore = new DirectoryInfo(patchFolder);
        Directory.CreateDirectory(patchStore.FullName);

        var logFile = storage.GetFile("aria2.log");

        if (acquisition is null)
        {
            acquisition = new AriaPatchAcquisition(logFile);
            await acquisition.StartIfNeededAsync(0); // no speed limit
        }

        var installer = new PatchInstaller(gamePath, keepPatches: false);
        installer.StartIfNeeded(false); // in-process RemotePatchInstaller, no external patcher exe
        installer.OnFail += () => status("patch installer failed");

        var patcher = new PatchManager(acquisition, 0, repo, patches,
            gamePath, patchStore, installer, launcher, uid);
        CurrentPatcher = patcher;

        var total = patcher.Downloads.Count;
        status($"{repo}: {total} patches to download/install");

        var done = await patcher.PatchAsync(false);
        CurrentPatcher = null;

        var speed = 0d;
        foreach (var s in patcher.Speeds) speed += s;
        status($"{repo} done: {(done ? "ok" : "FAILED")}, {total} patches, last speed {speed / 1024 / 1024:0.0} MB/s");

        return done;
    }

    /// <summary>
    /// Download and install the pending game patches for a NeedsPatchGame LoginResult
    /// (Aria2 RPC acquisition in ~/.xivonlinux/patches, in-process installer writing
    /// into the game folder). status receives human-readable progress lines.
    /// </summary>
    public async Task<bool> PatchAsync(Launcher.LoginResult loginResult, string gameLocation, Action<string> status)
    {
        return await InstallPatchesAsync(Repository.Ffxiv, loginResult.PendingPatches,
            loginResult.UniqueId ?? string.Empty, gameLocation, storage.GetFolder("patches").FullName, status);
    }

    /// <summary>
    /// Fetch the game patch list for an overridden reported version, reusing the login
    /// session from LoginAsync. This is the RegisterSession request shape (boot hash from
    /// the real boot files, exN ver lines, POST body = version report) sent with
    /// reportVer in the URL path, so a fully-patched install can be tested against the
    /// full patch list: reportVer = Constants.BASE_GAME_VERSION returns every patch from
    /// base to latest. Returns the parsed PatchListEntry[] (Url, VersionId, Length,
    /// HashType sha1, HashBlockSize, per-block Hashes).
    /// </summary>
    public async Task<PatchListEntry[]> FetchPatchListAsync(Launcher.LoginResult loginResult, DirectoryInfo gamePath, string reportVer)
    {
        var report = BuildVersionReport(gamePath, loginResult.OauthLogin!.MaxExpansion, reportVer);

        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://patch-gamever.ffxiv.com/http/win32/ffxivneo_release_game/{reportVer}/{loginResult.OauthLogin.SessionId}");
        request.Headers.AddWithoutValidation("Connection", "Keep-Alive");
        request.Headers.AddWithoutValidation("User-Agent", Constants.PatcherUserAgent);
        request.Headers.AddWithoutValidation("X-Hash-Check", "enabled");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(report));

        var resp = await client.SendAsync(request);
        var text = await resp.Content.ReadAsStringAsync();

        if (!resp.Headers.TryGetValues("X-Patch-Unique-Id", out var uidVals))
            throw new InvalidResponseException($"Could not get X-Patch-Unique-Id. ({resp.StatusCode})", text);

        if (string.IsNullOrEmpty(text))
            return Array.Empty<PatchListEntry>();

        return ParsePatchList(text);
    }

    /// <summary>
    /// Download a single patch into targetDir (no install), verify its per-block SHA1
    /// hashes against the patch list entry, report progress lines. targetDir is an
    /// empty scratch directory (e.g. ~/.xivonlinux/patches-test) — nothing is written
    /// into the game folder.
    /// </summary>
    public async Task<bool> DownloadPatchAsync(PatchListEntry patch, DirectoryInfo targetDir, Action<string> status)
    {
        var logFile = storage.GetFile("aria2.log");
        acquisition = new AriaPatchAcquisition(logFile);
        await acquisition.StartIfNeededAsync(0); // no speed limit

        var outFile = new FileInfo(Path.Combine(targetDir.FullName, patch.GetFilePath()));
        Directory.CreateDirectory(outFile.Directory!.FullName);

        status($"Downloading {patch.GetRepoName()}/{patch.VersionId} ({patch.Length / 1048576} MB) -> {outFile.FullName}");

        var tcs = new TaskCompletionSource<AcquisitionResult>();
        var task = acquisition.MakeTask(patch.Url, outFile);

        var lastReport = 0L;
        task.ProgressChanged += (_, args) =>
        {
            if (args.Progress - lastReport >= 100_000_000)
            {
                lastReport = args.Progress;
                status($"downloading {patch.VersionId}: {args.Progress / 1048576} MB / {patch.Length / 1048576} MB @ {args.BytesPerSecondSpeed / 1048576:0.0} MB/s");
            }
        };
        task.Complete += (_, result) => tcs.TrySetResult(result);

        await task.StartAsync();
        var result = await tcs.Task;

        if (result != AcquisitionResult.Success)
        {
            status($"Download {patch.VersionId} failed: {result}");
            Log.Error("[DOWNLOAD] acquisition result {Result} for {VersionId}", result, patch.VersionId);
            return false;
        }

        status($"Downloaded {patch.VersionId} ({patch.Length / 1048576} MB), verifying SHA1 ...");
        var ok = VerifyPatchHashes(patch, outFile);
        status(ok ? $"SHA1 verify PASS: {patch.VersionId} ({patch.Hashes.Length} blocks)"
                  : $"SHA1 verify FAIL: {patch.VersionId} at block {failBlock}");
        Log.Information("[DOWNLOAD] {VersionId} sha1 verify {Result} (block {Block})", patch.VersionId, ok ? "PASS" : "FAIL", failBlock);
        return ok;
    }

    private static int failBlock;

    /// <summary>
    /// Patch list parse, same shape as XIVLauncher.Common's internal PatchListParser
    /// (tab-separated fields; 9-field lines carry blocksize + hashes, 6-field lines
    /// are hash-less, e.g. bootver): Length=0, VersionId=4, HashType=5, HashBlockSize=6,
    /// Hashes=7 split ',' , Url=8 when 9 fields, Url=5 when 6. START_OFFSET=5, stop
    /// before the last 2 lines.
    /// </summary>
    private static PatchListEntry[] ParsePatchList(string list)
    {
        var lines = list.Split(new[] { "\r\n", "\r", "\n", Environment.NewLine }, StringSplitOptions.None);
        var output = new System.Collections.Generic.List<PatchListEntry>();
        const int START_OFFSET = 5;

        for (var i = START_OFFSET; i < lines.Length - 2; i++)
        {
            var fields = lines[i].Split('\t');
            output.Add(new PatchListEntry
            {
                Length = long.Parse(fields[0]),
                VersionId = fields[4],
                HashType = fields[5],
                HashBlockSize = fields.Length == 9 ? long.Parse(fields[6]) : 0,
                Hashes = fields.Length == 9 ? fields[7].Split(',') : null,
                Url = fields[fields.Length == 9 ? 8 : 5],
            });
        }

        return output.ToArray();
    }

    /// <summary>Per-block SHA1 verification, same shape as PatchManager.CheckPatchValidity.</summary>
    private static bool VerifyPatchHashes(PatchListEntry patch, FileInfo path)
    {
        if (patch.HashType != "sha1" || path.Length != patch.Length)
            return false;

        var parts = (int)Math.Ceiling((double)patch.Length / patch.HashBlockSize);
        var block = new byte[patch.HashBlockSize];

        using var stream = path.OpenRead();
        for (var i = 0; i < parts; i++)
        {
            var read = stream.Read(block, 0, (int)patch.HashBlockSize);
            if (read < patch.HashBlockSize)
                block = block.AsSpan(0, read).ToArray();

            var hash = SHA1.HashData(block);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));

            if (sb.ToString() != patch.Hashes[i])
            {
                failBlock = i;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Version report body for patch-gamever: boot ver + SHA1 hashes of the four hashed
    /// boot binaries, then exN ver lines (base ver when reporting an old game version).
    /// Same shape as Launcher.GetVersionReport; Launcher's own is private.
    /// </summary>
    private static string BuildVersionReport(DirectoryInfo gamePath, int exLevel, string reportVer)
    {
        var bootFiles = new[] { "ffxivboot.exe", "ffxivboot64.exe", "ffxivlauncher64.exe", "ffxivupdater64.exe" };
        var bootHash = $"{Repository.Boot.GetVer(gamePath)}=";
        for (var i = 0; i < bootFiles.Length; i++)
        {
            bootHash += $"{bootFiles[i]}/{FileHashSha1(Path.Combine(gamePath.FullName, "boot", bootFiles[i]))}";
            if (i != bootFiles.Length - 1) bootHash += ",";
        }

        var report = $"{bootHash}\n";
        for (var ex = 1; ex <= exLevel && ex <= 5; ex++)
            report += $"ex{ex}\t{Constants.BASE_GAME_VERSION}\n";

        return report;
    }

    private static string FileHashSha1(string file)
    {
        var hash = SHA1.HashData(File.ReadAllBytes(file));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        // GetFileHash shape: length + "/" + sha1hex
        return new FileInfo(file).Length + "/" + sb.ToString();
    }

    /// <summary>
    /// Encrypted game argument string for a successful login: the LaunchGame argument set
    /// (DEV.TestSID=login uid, region, expansion, ver) plus the UserPath the game writes
    /// its config to, translated to a Wine-style prefix path. Same shape as
    /// Launcher.LaunchGame's internal ArgumentBuilder + upstream UserPath append.
    /// </summary>
    public string BuildEncryptedGameArgs(Launcher.LoginResult loginResult, DirectoryInfo gamePath, DirectoryInfo gameConfigPath, bool isSteamServiceAccount)
    {
        var builder = new ArgumentBuilder()
            .Append("DEV.DataPathType", "1")
            .Append("DEV.MaxEntitledExpansionID", (loginResult.OauthLogin?.MaxExpansion ?? 5).ToString())
            .Append("DEV.TestSID", loginResult.UniqueId ?? string.Empty)
            .Append("DEV.UseSqPack", "1")
            .Append("SYS.Region", loginResult.OauthLogin.Region.ToString())
            .Append("language", ((int)language).ToString())
            .Append("resetConfig", "0")
            .Append("ver", Repository.Ffxiv.GetVer(gamePath));

        // Steam service account flags (upstream LaunchGame sets these ONLY when isSteamServiceAccount —
        // appending IsSteam=1 for a plain SE account makes the game treat it as a Steam service
        // account, which fails FT accounts with error 3109 "not registered").
        if (isSteamServiceAccount)
            builder.Append("IsSteam", "1");

        var userPath = prefix.UnixToWinePath(gameConfigPath.FullName);
        builder.Append("UserPath", userPath);

        return builder.BuildEncrypted();
    }

    /// <summary>Game config folder (character data): the configPath value passed to the
    /// constructor (app default: ~/.xivonlinux/ffxivConfig); created on first use.</summary>
    public DirectoryInfo GameConfigFolder()
    {
        var dir = new DirectoryInfo(configPath);
        dir.Create();
        return dir;
    }

    public void Dispose()
    {
        acquisition?.Dispose();
    }
}
