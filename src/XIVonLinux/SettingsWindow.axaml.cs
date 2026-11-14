using System;
using System.IO;
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

        // Proton enumeration (Alex): managed mode picks from the Proton builds found in
        // Steam compatibilitytools.d, steamapps/common, and the app's tools/proton dir.
        foreach (var dir in EnumerateProtonDirs())
            ProtonCombo.Items.Add(dir);
        if (ProtonCombo.Items.Contains(s.ProtonPath))
            ProtonCombo.SelectedItem = s.ProtonPath;
        ManagedProtonCheck.IsChecked = s.ManagedProton;
        ApplyProtonMode(s.ManagedProton);
    }

    /// <summary>Proton tool directories: any folder containing a top-level `proton` script
    /// (the Steam compatibility-tool layout) in compatibilitytools.d, steamapps/common,
    /// or ~/.xivonlinux/tools/proton.</summary>
    private static List<string> EnumerateProtonDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"),
            Path.Combine(home, ".local", "share", "Steam", "steamapps", "common"),
            Path.Combine(home, ".xivonlinux", "tools", "proton"),
        };
        var found = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                continue;
            foreach (var d in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(d, "proton")))
                    found.Add(d);
        }
        return found;
    }

    /// <summary>Custom mode: Proton textbox + Browse. Managed mode: the enumerated combo.</summary>
    private void ApplyProtonMode(bool managed)
    {
        ProtonPathBox.IsVisible = !managed;
        ProtonBrowseButton.IsVisible = !managed;
        ProtonCombo.IsVisible = managed;
        ProtonLabel.Text = managed ? "Proton (Managed)" : "Proton (Custom)";
        // Prefix is always a folder picker, in both modes.
        PrefixBrowseButton.IsVisible = true;
        if (managed && ProtonCombo.SelectedItem is null && ProtonCombo.Items.Count > 0)
            ProtonCombo.SelectedIndex = 0;
    }

    private void OnManagedProtonToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ApplyProtonMode(ManagedProtonCheck.IsChecked == true);

    private void OnProtonCombo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ProtonCombo.SelectedItem is string path)
            ProtonPathBox.Text = path;   // keep the text row in sync for custom mode
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
        s.ManagedProton = ManagedProtonCheck.IsChecked == true;
        if (s.ManagedProton && ProtonCombo.SelectedItem is string pick)
            s.ProtonPath = pick;   // managed mode: the enumerated selection wins
        else
            s.ProtonPath = ProtonPathBox.Text;
        s.PrefixPath = PrefixPathBox.Text;
        s.EnableDxvk = DxvkCheck.IsChecked == true;
        s.ExtraWineDllOverrides = ExtraWineDllOverridesBox.Text;
        s.DalamudEnabled = DalamudCheck.IsChecked == true;
        this.settingsService.Save();
        Close();
    }
}
