using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Steamworks;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Linux;

/// <summary>
/// Real Steamworks binding for Steam-account login: Facepunch.Steamworks (goaaats.Steamworks
/// 2.3.4, chippy branch) running on Linux. The Win64-named assembly P/Invokes "steam_api64",
/// which resolves libsteam_api64.so from the app directory. Same shape as XIVLauncher.Core's
/// UnixSteam (reference): setenv SteamAppId/SteamGameId before SteamClient.Init so the
/// steam_api native finds the running Steam client. Requires the Steam client running and
/// logged on (offline mode => BLoggedOn false => SteamException upstream).
/// </summary>
public sealed class SteamworksSteam : ISteam
{
    public event Action<bool>? OnGamepadTextInputDismissed;

    public SteamworksSteam()
    {
        SteamUtils.OnGamepadTextInputDismissed += b => OnGamepadTextInputDismissed?.Invoke(b);
    }

    public void Initialize(uint appId)
    {
        // SetEnvironmentVariable does not touch the real process env on unix; setenv does.
        [DllImport("c")]
        static extern int setenv(string name, string value, int overwrite);

        setenv("SteamAppId", appId.ToString(), 1);
        setenv("SteamGameId", appId.ToString(), 1);

        SteamClient.Init(appId);
    }

    public bool IsValid => SteamClient.IsValid;

    public bool BLoggedOn => SteamClient.IsLoggedOn;

    public bool BOverlayNeedsPresent => SteamUtils.DoesOverlayNeedPresent;

    public void Shutdown()
    {
        SteamClient.Shutdown();
    }

    public async Task<byte[]?> GetAuthSessionTicketAsync()
    {
        var ticket = await SteamUser.GetAuthSessionTicketAsync().ConfigureAwait(true);
        return ticket?.Data;
    }

    public bool IsAppInstalled(uint appId) => SteamApps.IsAppInstalled(appId);

    public string GetAppInstallDir(uint appId) => SteamApps.AppInstallDir(appId);

    public bool ShowGamepadTextInput(bool password, bool multiline, string description, int maxChars, string existingText = "")
        => SteamUtils.ShowGamepadTextInput(password ? GamepadTextInputMode.Password : GamepadTextInputMode.Normal,
            multiline ? GamepadTextInputLineMode.MultipleLines : GamepadTextInputLineMode.SingleLine,
            description, maxChars, existingText);

    public string GetEnteredGamepadText() => SteamUtils.GetEnteredGamepadText();

    public bool ShowFloatingGamepadTextInput(ISteam.EFloatingGamepadTextInputMode mode, int x, int y, int width, int height)
    {
        SteamUtils.ShowFloatingGamepadTextInput((TextInputMode)mode, x, y, width, height);
        return true;
    }

    public bool IsRunningOnSteamDeck() => SteamUtils.IsRunningOnSteamDeck;

    public uint GetServerRealTime() => (uint)((DateTimeOffset)SteamUtils.SteamServerTime).ToUnixTimeSeconds();

    public void ActivateGameOverlayToWebPage(string url, bool modal = false)
    {
        SteamFriends.OpenWebOverlay(url, modal);
    }
}
