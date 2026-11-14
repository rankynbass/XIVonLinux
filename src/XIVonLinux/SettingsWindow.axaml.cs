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

        // Umu enumeration (Alex): managed mode picks the latest/previous managed zipapp
        // installs under ~/.xivonlinux/tools/umu; custom mode is a folder picker for a
        // disk-side folder containing umu-run.
        var umuTags = EnumerateUmuTags();
        for (var i = 0; i < umuTags.Count; i++)
            UmuCombo.Items.Add(i == 0 ? $"{umuTags[i]} (latest)" : $"{umuTags[i]} (previous)");
        var savedTag = s.UmuTag;
        for (var i = 0; i < umuTags.Count; i++)
            if (umuTags[i] == savedTag)
                UmuCombo.SelectedIndex = i;
        if (UmuCombo.SelectedIndex < 0 && UmuCombo.Items.Count > 0)
            UmuCombo.SelectedIndex = 0;
        UmuPathBox.Text = s.UmuPath;
        ManagedUmuCheck.IsChecked = s.ManagedUmu;
        ApplyUmuMode(s.ManagedUmu);
    }

    /// <summary>Managed zipapp tags installed under ~/.xivonlinux/tools/umu (dirs that
    /// contain umu-run), newest first.</summary>
    private static List<string> EnumerateUmuTags()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".xivonlinux", "tools", "umu");
        if (!Directory.Exists(root))
            return new List<string>();
        var tags = Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(t => t is not null && File.Exists(Path.Combine(root, t!, "umu-run")))
            .Select(t => t!)
            .ToList();
        tags.Sort((a, b) =>
        {
            var va = Version.TryParse(a, out var x) ? x : new Version(0, 0);
            var vb = Version.TryParse(b, out var y) ? y : new Version(0, 0);
            return vb.CompareTo(va);
        });
        return tags;
    }

    /// <summary>Custom mode: Umu textbox + Browse. Managed mode: latest/previous combo.</summary>
    private void ApplyUmuMode(bool managed)
    {
        UmuPathBox.IsVisible = !managed;
        UmuBrowseButton.IsVisible = !managed;
        UmuCombo.IsVisible = managed;
        UmuLabel.Text = managed ? "Umu (Managed)" : "Umu (Custom)";
        if (managed && UmuCombo.SelectedIndex < 0 && UmuCombo.Items.Count > 0)
            UmuCombo.SelectedIndex = 0;
    }

    private void OnManagedUmuToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ApplyUmuMode(ManagedUmuCheck.IsChecked == true);

    private void OnUmuCombo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Keep the custom textbox in sync with the selection (tag without the display suffix).
        if (UmuCombo.SelectedItem is string sel)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            UmuPathBox.Text = Path.Combine(home, ".xivonlinux", "tools", "umu", sel.Split(" (")[0]);
        }
    }

    private async void OnBrowseUmu(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(UmuPathBox);

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
        s.ManagedUmu = ManagedUmuCheck.IsChecked == true;
        if (s.ManagedUmu && UmuCombo.SelectedItem is string umuSel)
            s.UmuTag = umuSel.Split(" (")[0];   // managed mode: strip the display suffix
        s.UmuPath = UmuPathBox.Text;            // custom mode: picked folder containing umu-run
        s.PrefixPath = PrefixPathBox.Text;
        s.EnableDxvk = DxvkCheck.IsChecked == true;
        s.ExtraWineDllOverrides = ExtraWineDllOverridesBox.Text;
        s.DalamudEnabled = DalamudCheck.IsChecked == true;
        this.settingsService.Save();
        Close();
    }
}
