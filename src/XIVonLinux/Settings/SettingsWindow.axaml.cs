using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace XIVonLinux.Settings;

/// <summary>Settings window core: constructor, tab dispatch (each tab lives in its own
/// partial file — GameTab, ProtonTab, DalamudTab, PatchingTab, TroubleshootingTab),
/// and the modal helpers shared by all tab files.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService settingsService;
    private readonly AccountManager accountManager;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Opens prefilled from the shared SettingsService; Save writes back and persists.
    /// AccountManager rides along for Reset Settings (accounts.db/deck rebuild + UI reload).</summary>
    public SettingsWindow(SettingsService service, AccountManager accounts) : this()
    {
        this.settingsService = service;
        this.accountManager = accounts;
        ReloadFromSettings();
    }

    /// <summary>Reloads every field from the shared SettingsService — at open and after Reset
    /// Settings. Each tab loads from its own partial file; combos are cleared first so
    /// re-loading never duplicates items.</summary>
    private void ReloadFromSettings()
    {
        GameTabLoad();
        ProtonTabLoad();
        DalamudTabLoad();
        PatchingTabLoad();
        TroubleshootingTabLoad();
    }

    /// <summary>Persist all settings, close the modal; the main window refocuses afterwards.
    /// Each tab validates + commits its own fields; any tab failure aborts the save.</summary>
    private void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (this.settingsService is null)
            return;
        var settings = this.settingsService.Settings;
        if (!GameTabSave(settings)) return;
        if (!ProtonTabSave(settings)) return;
        if (!PatchingTabSave(settings)) return;
        if (!DalamudTabSave(settings)) return;
        if (!TroubleshootingTabSave(settings)) return;
        this.settingsService.Save();
        Close();
    }

    /// <summary>Modal alert on this window; OK closes it.</summary>
    private void ShowAlert(string message)
    {
        var popup = new Window
        {
            Title = "XIVonLinux",
            Width = 420,
            Height = 140,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var okButton = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
        okButton.Click += (_, _) => popup.Close();
        popup.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16, 12, 16, 12),
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, okButton },
        };
        popup.ShowDialog(this);
    }

    /// <summary>Modal Yes/No confirmation on this window. Avalonia ShowDialog is
    /// non-blocking (returns a Task immediately), so it must be awaited — result is
    /// only meaningful after the await completes.</summary>
    private async Task<bool> ShowConfirm(string message)
    {
        var popup = new Window
        {
            Title = "XIVonLinux",
            Width = 460,
            Height = 190,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var result = false;
        var yesButton = new Button { Content = "Yes", Margin = new Avalonia.Thickness(0, 0, 12, 0) };
        var noButton = new Button { Content = "No" };
        yesButton.Click += (_, _) => { result = true; popup.Close(); };
        noButton.Click += (_, _) => { result = false; popup.Close(); };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(16, 12, 16, 12) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var row = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 12, 0, 0),
        };
        row.Children.Add(yesButton);
        row.Children.Add(noButton);
        panel.Children.Add(row);

        popup.Content = panel;
        await popup.ShowDialog(this);
        return result;
    }

    /// <summary>Shared folder picker: the picked folder lands in the given textbox.</summary>
    private async Task PickFolderAsync(TextBox box)
    {
        var folders = await this.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
        if (folders.Count > 0)
            box.Text = folders[0].Path.LocalPath;
    }
}
