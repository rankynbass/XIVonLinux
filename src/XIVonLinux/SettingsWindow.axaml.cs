using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using XIVLauncher.Common.Linux;

namespace XIVonLinux;

public partial class SettingsWindow : Window
{
    private readonly SettingsService settingsService;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Opens prefilled from the shared SettingsService; Save writes back and persists.</summary>
    public SettingsWindow(SettingsService service) : this()
    {
        this.settingsService = service;
        var s = service.Settings;
        GamePathBox.Text = s.GamePath;
        ConfigPathBox.Text = s.ConfigPath;
        PatchPathBox.Text = s.PatchPath;
        ProtonPathBox.Text = s.ProtonPath;
        PrefixPathBox.Text = s.PrefixPath;
        DxvkCheck.IsChecked = s.EnableDxvk;
        ExtraWineDllOverridesBox.Text = s.ExtraWineDllOverrides;
        LanguageCombo.Items.Add("English");
        LanguageCombo.Items.Add("Japanese");
        LanguageCombo.Items.Add("German");
        LanguageCombo.Items.Add("French");
        LanguageCombo.SelectedItem = s.Language;
        if (LanguageCombo.SelectedItem is null)
            LanguageCombo.SelectedIndex = 0;
        DalamudCheck.IsChecked = s.DalamudEnabled;
    }

    private async void OnBrowseGame(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(GamePathBox);

    private async void OnBrowseConfig(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(ConfigPathBox);

    private async void OnBrowsePatch(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(PatchPathBox);

    private async void OnBrowseProton(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(ProtonPathBox);

    private async void OnBrowsePrefix(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(PrefixPathBox);

    private async Task PickFolderAsync(TextBox box)
    {
        var folders = await this.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
        if (folders.Count > 0)
            box.Text = folders[0].Path.LocalPath;
    }

    /// <summary>Persist all settings, close the modal; the main window refocuses afterwards.</summary>
    private void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.settingsService is null)
            return;
        var s = this.settingsService.Settings;
        s.GamePath = GamePathBox.Text;
        s.ConfigPath = ConfigPathBox.Text;
        s.PatchPath = PatchPathBox.Text;
        s.Language = LanguageCombo.SelectedItem as string ?? "English";
        s.ProtonPath = ProtonPathBox.Text;
        s.PrefixPath = PrefixPathBox.Text;
        s.EnableDxvk = DxvkCheck.IsChecked == true;
        s.ExtraWineDllOverrides = ExtraWineDllOverridesBox.Text;
        s.DalamudEnabled = DalamudCheck.IsChecked == true;
        this.settingsService.Save();
        Close();
    }
}
