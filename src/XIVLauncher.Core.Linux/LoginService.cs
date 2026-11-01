using System;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Encryption;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Game.Patch.Acquisition.Aria;
using XIVLauncher.Common.Util;

namespace XIVLauncher.Core.Linux;

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
        this.launcher = new Launcher(new LinuxSteamStub(), new NullUniqueIdCache(), FrontierUrlTemplate, "en");
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
    /// Real SE login: OauthLogin (username/password/OTP) + RegisterSession (version report
    /// -> patch list + X-Patch-Unique-Id). isSteam must be false — Steam-account login needs
    /// a Steamworks binding, which XIVonLinux does not have yet.
    /// </summary>
    public async Task<Launcher.LoginResult> LoginAsync(LaunchSettings settings, DirectoryInfo gamePath)
    {
        if (settings.SteamAccount)
            throw new NotSupportedException("Steam-account login is not implemented on Linux yet (no Steamworks binding); use the Square Enix account option.");

        return await launcher.Login(
            settings.Username, settings.Password, settings.OneTimePassword,
            isSteam: false, useCache: false, gamePath, forceBaseVersion: false,
            settings.FreeTrial, language);
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
