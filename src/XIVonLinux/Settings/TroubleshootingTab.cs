using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Microsoft.Data.Sqlite;

namespace XIVonLinux.Settings;

/// <summary>Troubleshooting tab: XIVLauncher.Core hacks (locale detection) plus the
/// Clear/Reset buttons and TSPack generation. Partial of SettingsWindow; the core
/// dispatches load through TroubleshootingTabLoad and save through TroubleshootingTabSave.</summary>
public partial class SettingsWindow
{
    private void TroubleshootingTabLoad()
    {
        var settings = this.settingsService.Settings;
        FixLdpCheck.IsChecked = settings.FixLdp;
        FixXModifiersCheck.IsChecked = settings.FixXModifiers;
        FixLibIcuucCheck.IsChecked = settings.FixLibIcuuc;
        DontUseSystemTzCheck.IsChecked = settings.DontUseSystemTz;

        // Locale hack shows the detected C.* UTF-8 locale, warns when LANG is already
        // UTF-8, and is disabled when none exists.
        var ctype = LaunchSettings.DetectCType();
        var lang = Environment.GetEnvironmentVariable("LANG") ?? "";
        FixLocaleCheck.IsEnabled = ctype.Length > 0;
        FixLocaleCheck.IsChecked = settings.FixLocale && ctype.Length > 0;
        if (ctype.Length == 0)
        {
            FixLocaleCheck.Content = "Hack: Force locale";
            LocaleHackLabel.Text = "Hack Disabled. Could not find a UTF-8 C locale. " +
                "You may have to set LC_ALL manually if LANG is not a UTF-8 type.";
        }
        else
        {
            FixLocaleCheck.Content = $"Hack: Force locale to {ctype}";
            LocaleHackLabel.Text = $"Sets LC_ALL and LC_CTYPE to \"{ctype}\". This can fix some issues " +
                "with non-Latin unicode characters in file paths if LANG is not a UTF-8 type.";
            if (lang.ToUpper().Contains("UTF"))
                LocaleHackLabel.Text += $" Your locale is \"{lang}\". You probably don't need this hack.";
        }
    }

    /// <summary>Commits the hack toggles into settings (no validation needed).</summary>
    private bool TroubleshootingTabSave(LaunchSettings settings)
    {
        settings.FixLdp = FixLdpCheck.IsChecked == true;
        settings.FixXModifiers = FixXModifiersCheck.IsChecked == true;
        settings.FixLibIcuuc = FixLibIcuucCheck.IsChecked == true;
        settings.FixLocale = FixLocaleCheck.IsChecked == true;
        settings.DontUseSystemTz = DontUseSystemTzCheck.IsChecked == true;
        return true;
    }

    private static void DeleteDir(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, true);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Clear Prefix: delete the WINEPREFIX root; umu regenerates it on next launch.
    /// (XIVLauncher.Core's ClearPrefix does the same; the user rule: prefixes break after umu
    /// major bumps, so this doubles as the documented first move for a launch hang.)</summary>
    private async void OnClearPrefix(object? sender, RoutedEventArgs eventArgs)
    {
        var root = this.settingsService.Settings.PrefixPath;
        if (!await ShowConfirm($"Delete the prefix at\n{root}\n\nIt will be recreated on the next game launch."))
        {
            return;
        }
        try
        {
            DeleteDir(root);
            ShowAlert("Prefix cleared. It will be recreated on the next game launch.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[BUTTON] Clear Prefix failed");
            ShowAlert($"Failed to clear prefix: {ex.Message}");
        }
    }

    /// <summary>Clear Logs: delete the whole logs folder. Serilog's live file resumes
    /// on next launch; in-prefix logs (dalamud.boot.log) are recreated by the game launch.</summary>
    private async void OnClearLogs(object? sender, RoutedEventArgs eventArgs)
    {
        var logs = Path.Combine(this.settingsService.Root.FullName, "logs");
        if (!await ShowConfirm($"Delete all log files in\n{logs}"))
        {
            return;
        }
        try
        {
            // Delete the log files, not the folder itself: Serilog's rolling-file sink is
            // attached to the folder, and deleting the folder ghosts the running app's
            // logging (writes keep landing on the deleted inode).
            if (Directory.Exists(logs))
                foreach (var f in Directory.EnumerateFiles(logs, "*", SearchOption.AllDirectories))
                    File.Delete(f);
            ShowAlert("Logs deleted. New log files will be created on next launch.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[BUTTON] Clear Logs failed");
            ShowAlert($"Failed to clear logs: {ex.Message}");
        }
    }

    /// <summary>Clear Dalamud Files: delete dalamud, runtime, dalamudAssets folders plus
    /// dalamudConfig.json, dalamudUI.ini and dalamudVfs.db (cache copy and the legacy
    /// data-root copy). The updater redownloads everything on next game launch.</summary>
    private async void OnClearDalamud(object? sender, RoutedEventArgs eventArgs)
    {
        var root = this.settingsService.Root.FullName;
        if (!await ShowConfirm("Delete all Dalamud files (dalamud, runtime, dalamudAssets, dalamudConfig.json, " +
            "dalamudUI.ini, dalamudVfs.db)?\n\nThey will be redownloaded on the next game launch."))
        {
            return;
        }
        try
        {
            DeleteDir(Path.Combine(root, "dalamud"));
            DeleteDir(Path.Combine(root, "runtime"));
            DeleteDir(Path.Combine(root, "dalamudAssets"));
            DeleteFile(Path.Combine(root, "dalamudConfig.json"));
            DeleteFile(Path.Combine(root, "dalamudUI.ini"));
            DeleteFile(Path.Combine(root, "dalamudVfs.db"));
            DeleteFile(Path.Combine(XdgStorage.CacheRoot, "dalamudVfs.db"));
            // Recreate the folders empty: the in-prefix Dalamud updater (submodule) calls
            // Directory.Delete(true) on folders it finds existing; racing against our own
            // half-deleted trees throws DirectoryNotFoundException. An empty folder makes
            // its delete-then-create cycle deterministic.
            Directory.CreateDirectory(Path.Combine(root, "dalamud"));
            Directory.CreateDirectory(Path.Combine(root, "runtime"));
            Directory.CreateDirectory(Path.Combine(root, "dalamudAssets"));
            // The background updater already completed in this session; re-fire it so the
            // files are re-downloaded without needing an app restart.
            if (this.Owner is MainWindow mw) mw.RestartDalamudUpdater();
            ShowAlert("Dalamud files cleared. They will be redownloaded on the next game launch.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[BUTTON] Clear Dalamud failed");
            ShowAlert($"Failed to clear Dalamud files: {ex.Message}");
        }
    }

    /// <summary>Clear Plugins: delete installedPlugins; pluginConfigs is kept (XIVLauncher.Core
    /// parity — plugin config/settings survive a plugin clear).</summary>
    private async void OnClearPlugins(object? sender, RoutedEventArgs eventArgs)
    {
        var plugins = Path.Combine(this.settingsService.Root.FullName, "installedPlugins");
        if (!await ShowConfirm($"Delete the installedPlugins folder?\n\npluginConfigs is kept."))
        {
            return;
        }
        try
        {
            DeleteDir(plugins);
            ShowAlert("Plugins cleared. pluginConfigs kept.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[BUTTON] Clear Plugins failed");
            ShowAlert($"Failed to clear plugins: {ex.Message}");
        }
    }

    /// <summary>Reset Settings: back up settings.toml, accounts.db and accounts.deck to
    /// *.bak, reset settings.toml to factory defaults, rebuild empty accounts.db + accounts.deck,
    /// and reload every field. The Accounts menu and login prefill rebuild on next use.</summary>
    private async void OnResetSettings(object? sender, RoutedEventArgs eventArgs)
    {
        if (!await ShowConfirm("Reset ALL settings to defaults?\n\nsettings.toml, accounts.db and accounts.deck are saved as *.bak\n"
            + XdgStorage.ConfigRoot + " first."))
        {
            return;
        }
        try
        {
            foreach (var name in new[] { "settings.toml", "accounts.db", "accounts.deck" })
            {
                var file = this.settingsService.GetConfigFile(name).FullName;
                if (File.Exists(file))
                    File.Copy(file, file + ".bak", true);
                File.Delete(file);
            }

            // Kill pooled SQLite handles before recreating: any pooled connection opened
            // on accounts.db / accounts.deck is keyed to the deleted path; Reinit's Load()
            // would reuse the ghost on the deleted inode (stale reads, SQLITE_READONLY
            // writes). ClearAllPools closes them, so the next Open creates the new files.
            SqliteConnection.ClearAllPools();

            this.settingsService.ResetToDefaults();
            this.accountManager.Reinit();
            ReloadFromSettings();
            if (this.Owner is MainWindow mw) mw.PrefillFromCurrentAccount();
            ShowAlert("Settings reset. Backups saved as settings.toml.bak, accounts.db.bak, accounts.deck.bak.");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[BUTTON] Reset Settings failed");
            ShowAlert($"Failed to reset settings: {ex.Message}");
        }
    }

    /// <summary>Write logs/trouble-&lt;timestamp&gt;.tspack: all non-rotated log files plus
    /// the trouble.json settings/version snapshot; the path is reported in an alert.</summary>
    private async void OnGenerateTspack(object? sender, RoutedEventArgs eventArgs)
    {
        if (this.settingsService is null)
            return;
        try
        {
            var settings = this.settingsService.Settings;
            var path = await Task.Run(() => TspackGenerator.SavePack(settings));
            ShowAlert($"TSPack saved to {path}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "[BUTTON] Generate TSPack failed");
            ShowAlert($"Failed to generate TSPack: {ex.Message}");
        }
    }
}
