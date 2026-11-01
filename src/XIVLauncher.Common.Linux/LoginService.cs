using System;
using System.IO;
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
    private readonly SettingsService settingsService;
    private readonly UmuPrefix prefix;
    private readonly ClientLanguage language;
    private AriaPatchAcquisition? acquisition;

    public LoginService(SettingsService settings, UmuPrefix prefix, ClientLanguage language = ClientLanguage.English)
    {
        this.settingsService = settings;
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

    /// <summary>
    /// Login: Steam-account path (isSteam=true — Steamworks auth ticket via
    /// SteamClient.Init(39210/312060 for free trial) + Ticket.Get) or SE credentials
    /// (OauthLogin + RegisterSession). Launcher.Login drives Steam init, BLoggedOn
    /// check and ticket retries; SteamException is thrown upstream on init/logon
    /// failure. Username is the SE/Steam account name; Steam login ignores
    /// password/OTP. Credentials are passed in-memory and never persisted.
    /// </summary>
    public async Task<Launcher.LoginResult> LoginAsync(LaunchSettings settings, DirectoryInfo gamePath)
    {
        return await launcher.Login(
            settings.Username, settings.Password, settings.OneTimePassword,
            settings.SteamAccount, useCache: false, gamePath, forceBaseVersion: false,
            settings.FreeTrial, language);
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
        var logFile = settingsService.GetFile("aria2.log");
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
    /// Download and install the pending game patches for a NeedsPatchGame LoginResult
    /// (Aria2 RPC acquisition in ~/.xivonlinux/patches, in-process installer writing
    /// into the game folder). status receives human-readable progress lines.
    /// </summary>
    public async Task<bool> PatchAsync(Launcher.LoginResult loginResult, LaunchSettings settings, Action<string> status)
    {
        var gamePath = new DirectoryInfo(settings.GameLocation);
        var patchStore = new DirectoryInfo(settings.PatchFolder);
        Directory.CreateDirectory(patchStore.FullName);

        var logFile = settingsService.GetFile("aria2.log");

        acquisition = new AriaPatchAcquisition(logFile);
        await acquisition.StartIfNeededAsync(0); // no speed limit

        var installer = new PatchInstaller(gamePath, keepPatches: false);
        installer.StartIfNeeded(false); // in-process RemotePatchInstaller, no external patcher exe
        installer.OnFail += () => status("patch installer failed");

        var patcher = new PatchManager(acquisition, 0, Repository.Ffxiv, loginResult.PendingPatches,
            gamePath, patchStore, installer, launcher, loginResult.UniqueId ?? string.Empty);

        var total = patcher.Downloads.Count;
        status($"Patching: {total} patches to download");

        var done = await patcher.PatchAsync(false);

        var speed = 0d;
        foreach (var s in patcher.Speeds) speed += s;
        status($"Patching done: {done}, {total} patches, last speed {speed / 1024 / 1024:0.0} MB/s");

        return done;
    }

    /// <summary>
    /// Encrypted game argument string for a successful login: the LaunchGame argument set
    /// (DEV.TestSID=login uid, region, expansion, ver) plus the UserPath the game writes
    /// its config to, translated to a Wine-style prefix path. Same shape as
    /// Launcher.LaunchGame's internal ArgumentBuilder + upstream UserPath append.
    /// </summary>
    public string BuildEncryptedGameArgs(Launcher.LoginResult loginResult, DirectoryInfo gamePath, DirectoryInfo gameConfigPath)
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

        // Steam service account flags (upstream LaunchGame sets these when isSteamServiceAccount).
        builder.Append("IsSteam", "1");

        var userPath = prefix.UnixToWinePath(gameConfigPath.FullName);
        builder.Append("UserPath", userPath);

        return builder.BuildEncrypted();
    }

    /// <summary>Game config folder under ~/.xivonlinux (upstream storage.GetFolder("ffxivConfig")).</summary>
    public DirectoryInfo GameConfigFolder() => settingsService.GetFolder("ffxivConfig");

    public void Dispose()
    {
        acquisition?.Dispose();
    }
}
