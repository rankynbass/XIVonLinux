using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace XIVonLinux.Settings;

/// <summary>Proton tab: Proton/umu pickers (managed + custom), prefix, DXVK, extra
/// wine dll overrides, WINEDEBUG. Partial of SettingsWindow; the core dispatches
/// load through ProtonTabLoad and save through ProtonTabSave.</summary>
public partial class SettingsWindow
{
    /// <summary>Wine Sync method combo labels; index maps to LaunchSettings.WineSyncMethod.
    /// Env pairs per selection are produced in HackEnvPairs (shared launch-env pipeline).</summary>
    private static readonly string[] WineSyncLabels =
    {
        "NTSync (if available)",
        "FSync",
        "ESync",
        "Wine Sync (slow)",
    };

    private void ProtonTabLoad()
    {
        var settings = this.settingsService.Settings;
        ProtonPathBox.Text = settings.ProtonPath;
        PrefixPathBox.Text = settings.PrefixPath;
        DxvkCheck.IsChecked = settings.EnableDxvk;
        WaylandCheck.IsChecked = settings.EnableWayland;
        WineSyncCombo.Items.Clear();
        foreach (var label in WineSyncLabels)
            WineSyncCombo.Items.Add(label);
        WineSyncCombo.SelectedIndex = settings.WineSyncMethod >= 0 && settings.WineSyncMethod < WineSyncLabels.Length
            ? settings.WineSyncMethod : 0;
        ExtraWineDllOverridesBox.Text = settings.ExtraWineDllOverrides;
        WineDebugCheck.IsChecked = settings.WineDebugEnabled;
        WineDebugBox.Text = string.IsNullOrWhiteSpace(settings.WineDebugValue)
            ? LaunchSettings.DefaultWineDebugValue : settings.WineDebugValue;
        WineDebugBox.IsEnabled = WineDebugCheck.IsChecked == true;

        // Proton enumeration (Alex): managed mode picks from the Proton builds found in
        // Steam compatibilitytools.d, steamapps/common, and the app's tools/proton dir.
        ProtonCombo.Items.Clear();
        foreach (var dir in EnumerateProtonDirs())
            ProtonCombo.Items.Add(dir);
        if (ProtonCombo.Items.Contains(settings.ProtonPath))
            ProtonCombo.SelectedItem = settings.ProtonPath;
        ManagedProtonCheck.IsChecked = settings.ManagedProton;
        ApplyProtonMode(settings.ManagedProton);

        // Umu enumeration (Alex): managed mode picks the latest/previous managed zipapp
        // installs under ~/.xivonlinux/tools/umu; custom mode is a folder picker for a
        // disk-side folder containing umu-run.
        UmuCombo.Items.Clear();
        var umuTags = EnumerateUmuTags();
        for (var i = 0; i < umuTags.Count; i++)
            UmuCombo.Items.Add(i == 0 ? $"{umuTags[i]} (latest)" : $"{umuTags[i]} (previous)");
        var savedTag = settings.UmuTag;
        for (var i = 0; i < umuTags.Count; i++)
            if (umuTags[i] == savedTag)
                UmuCombo.SelectedIndex = i;
        if (UmuCombo.SelectedIndex < 0 && UmuCombo.Items.Count > 0)
            UmuCombo.SelectedIndex = 0;
        UmuPathBox.Text = settings.UmuPath;
        ManagedUmuCheck.IsChecked = settings.ManagedUmu;
        ApplyUmuMode(settings.ManagedUmu);
    }

    /// <summary>Commits Proton tab fields into settings (no validation needed).</summary>
    private bool ProtonTabSave(LaunchSettings settings)
    {
        settings.ManagedProton = ManagedProtonCheck.IsChecked == true;
        if (settings.ManagedProton && ProtonCombo.SelectedItem is string pick)
            settings.ProtonPath = pick;   // managed mode: the enumerated selection wins
        else
            settings.ProtonPath = ProtonPathBox.Text;
        settings.ManagedUmu = ManagedUmuCheck.IsChecked == true;
        if (settings.ManagedUmu && UmuCombo.SelectedItem is string umuSel)
            settings.UmuTag = umuSel.Split(" (")[0];   // managed mode: strip the display suffix
        settings.UmuPath = UmuPathBox.Text;            // custom mode: picked folder containing umu-run
        settings.PrefixPath = PrefixPathBox.Text;
        settings.EnableDxvk = DxvkCheck.IsChecked == true;
        settings.EnableWayland = WaylandCheck.IsChecked == true;
        settings.WineSyncMethod = WineSyncCombo.SelectedIndex >= 0 ? WineSyncCombo.SelectedIndex : 0;
        settings.ExtraWineDllOverrides = ExtraWineDllOverridesBox.Text;
        settings.WineDebugEnabled = WineDebugCheck.IsChecked == true;
        settings.WineDebugValue = string.IsNullOrWhiteSpace(WineDebugBox.Text)
            ? LaunchSettings.DefaultWineDebugValue : WineDebugBox.Text;
        return true;
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
            .Where(tag => tag is not null && File.Exists(Path.Combine(root, tag!, "umu-run")))
            .Select(tag => tag!)
            .ToList();
        tags.Sort((verA, verB) =>
        {
            var versionA = Version.TryParse(verA, out var parsedVerA) ? parsedVerA : new Version(0, 0);
            var versionB = Version.TryParse(verB, out var parsedVerB) ? parsedVerB : new Version(0, 0);
            return versionB.CompareTo(versionA);
        });
        return tags;
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
            foreach (var dir in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(dir, "proton")))
                    found.Add(dir);
        }
        return found;
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

    private void OnManagedProtonToggled(object? sender, RoutedEventArgs eventArgs)
        => ApplyProtonMode(ManagedProtonCheck.IsChecked == true);

    private void OnProtonCombo(object? sender, RoutedEventArgs eventArgs)
    {
        if (ProtonCombo.SelectedItem is string path)
            ProtonPathBox.Text = path;   // keep the text row in sync for custom mode
    }

    private void OnManagedUmuToggled(object? sender, RoutedEventArgs eventArgs)
        => ApplyUmuMode(ManagedUmuCheck.IsChecked == true);

    private void OnUmuCombo(object? sender, RoutedEventArgs eventArgs)
    {
        // Keep the custom textbox in sync with the selection (tag without the display suffix).
        if (UmuCombo.SelectedItem is string sel)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            UmuPathBox.Text = Path.Combine(home, ".xivonlinux", "tools", "umu", sel.Split(" (")[0]);
        }
    }

    private async void OnBrowseUmu(object? sender, RoutedEventArgs eventArgs)
        => await PickFolderAsync(UmuPathBox);

    private async void OnBrowseProton(object? sender, RoutedEventArgs eventArgs)
        => await PickFolderAsync(ProtonPathBox);

    private async void OnBrowsePrefix(object? sender, RoutedEventArgs eventArgs)
        => await PickFolderAsync(PrefixPathBox);

    /// <summary>The WINEDEBUG value box edits only while logging is enabled; when
    /// disabled, launches force WINEDEBUG=-all.</summary>
    private void OnWineDebugToggled(object? sender, RoutedEventArgs eventArgs)
        => WineDebugBox.IsEnabled = WineDebugCheck.IsChecked == true;
}
