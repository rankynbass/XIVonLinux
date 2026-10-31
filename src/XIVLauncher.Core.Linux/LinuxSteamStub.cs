using System;
using System.Threading.Tasks;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Core.Linux;

/// <summary>
/// Stub Steam integration. Full Steamworks bindings will come later; for now the
/// launcher gates (overlay, gamepad input, auth ticket) report "not supported",
/// which matches the fake/credential-free launch path used in development spikes.
/// </summary>
public sealed class LinuxSteamStub : ISteam
{
    public bool IsValid => false;
    public bool BLoggedOn => false;
    public bool BOverlayNeedsPresent => false;

    public void Initialize(uint appId) { }
    public void Shutdown() { }

    public Task<byte[]?> GetAuthSessionTicketAsync() => Task.FromResult<byte[]?>(null);

    public bool IsAppInstalled(uint appId) => appId == SteamInstall.XivAppId && SteamInstall.Detect() is not null;

    public string GetAppInstallDir(uint appId) =>
        appId == SteamInstall.XivAppId ? SteamInstall.Detect()?.GamePath.FullName ?? string.Empty : string.Empty;

    public bool ShowGamepadTextInput(bool password, bool multiline, string description, int maxChars, string existingText = "") => false;
    public string GetEnteredGamepadText() => string.Empty;
    public bool ShowFloatingGamepadTextInput(ISteam.EFloatingGamepadTextInputMode mode, int x, int y, int width, int height) => false;

    public bool IsRunningOnSteamDeck() => Environment.GetEnvironmentVariable("SteamTaskId") is not null;

    public uint GetServerRealTime() => 0;

    public void ActivateGameOverlayToWebPage(string url, bool modal = false) { }

    public event Action<bool>? OnGamepadTextInputDismissed;
}
