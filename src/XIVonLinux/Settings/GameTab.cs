using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XIVonLinux.Settings;

/// <summary>Game tab: paths, language, extra args, DPI, Steam-ignore, XL auth macros,
/// UID cache (XIVLauncher.Core Game tab parity). Partial of SettingsWindow; the core
/// dispatches load through GameTabLoad and save through GameTabSave.</summary>
public partial class SettingsWindow
{
    private void GameTabLoad()
    {
        var settings = this.settingsService.Settings;
        GamePathBox.Text = settings.GamePath;
        ConfigPathBox.Text = settings.ConfigPath;
        PatchPathBox.Text = settings.PatchPath;

        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add("English");
        LanguageCombo.Items.Add("Japanese");
        LanguageCombo.Items.Add("German");
        LanguageCombo.Items.Add("French");
        LanguageCombo.SelectedItem = settings.Language;
        if (LanguageCombo.SelectedItem is null)
            LanguageCombo.SelectedIndex = 0;

        // XIVLauncher.Core Game tab parity: extra args, DPI awareness, Steam-ignore,
        // XL auth macros, uid cache toggle + reset panel.
        AdditionalArgsBox.Text = settings.AdditionalArgs;
        DpiCombo.Items.Clear();
        DpiCombo.Items.Add("Aware");
        DpiCombo.Items.Add("Unaware");
        DpiCombo.SelectedItem = settings.DpiAwareness;
        if (DpiCombo.SelectedItem is null)
            DpiCombo.SelectedIndex = 1;   // Unaware default
        IgnoreSteamCheck.IsChecked = settings.IgnoreSteam;
        XlAuthCheck.IsChecked = settings.UseXlAuthMacros;
        UidCacheCheck.IsChecked = settings.UseUidCache;
        UidCachePanel.IsVisible = settings.UseUidCache;   // reset section shown when enabled (Core parity)
    }

    /// <summary>Commits Game tab fields into settings; returns false (alert shown) when
    /// the save must abort. XIVLauncher.Core CheckValidity: game folder must be set, and
    /// must not be the game/ or boot/ subfolder of an FFXIV install (the common mispick).</summary>
    private bool GameTabSave(LaunchSettings settings)
    {
        settings.GamePath = GamePathBox.Text;
        settings.ConfigPath = ConfigPathBox.Text;
        settings.PatchPath = PatchPathBox.Text;
        settings.Language = LanguageCombo.SelectedItem as string ?? "English";

        var gameName = Path.GetFileName(settings.GamePath.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(settings.GamePath) || gameName == "game" || gameName == "boot")
        {
            ShowAlert("GamePath is invalid — pick the FFXIV install folder (the one containing\n" +
                      "the game/ and boot/ subfolders), not game/ or boot/ itself.");
            return false;
        }

        // Args: space-separated KEY=VALUE pairs, passed as environment variables. A bare
        // KEY (no '=') is invalid — the error names it so the user knows what to fix.
        try
        {
            LaunchSettings.ParseArgs(AdditionalArgsBox.Text);
        }
        catch (FormatException ex)
        {
            ShowAlert($"Args: {ex.Message}\n\nFormat: space-separated KEY=VALUE pairs.\n" +
                      "Use KEY=\"VALUE\" for values with spaces, KEY= for an empty value.");
            return false;   // do not persist/close with invalid args
        }
        settings.AdditionalArgs = AdditionalArgsBox.Text;
        settings.DpiAwareness = DpiCombo.SelectedItem as string ?? "Unaware";
        settings.IgnoreSteam = IgnoreSteamCheck.IsChecked == true;
        settings.UseXlAuthMacros = XlAuthCheck.IsChecked == true;
        settings.UseUidCache = UidCacheCheck.IsChecked == true;
        return true;
    }

    private async void OnBrowseGame(object? sender, RoutedEventArgs eventArgs)
        => await PickFolderAsync(GamePathBox);

    private async void OnBrowseConfig(object? sender, RoutedEventArgs eventArgs)
        => await PickFolderAsync(ConfigPathBox);

    private async void OnBrowsePatch(object? sender, RoutedEventArgs eventArgs)
        => await PickFolderAsync(PatchPathBox);

    /// <summary>UID cache panel shows only while the cache toggle is on (XIVLauncher.Core footer).</summary>
    private void OnUidCacheToggled(object? sender, RoutedEventArgs eventArgs)
    {
        UidCachePanel.IsVisible = UidCacheCheck.IsChecked == true;
    }

    /// <summary>Clears the cached uid and persists; the launcher must restart to take effect
    /// (XIVLauncher.Core: "you must then restart the launcher").</summary>
    private void OnResetUidCache(object? sender, RoutedEventArgs eventArgs)
    {
        if (this.settingsService is null)
            return;
        this.settingsService.UidCache.Reset();
        ShowAlert("UID cache cleared. Restart the launcher to take effect.");
    }
}
