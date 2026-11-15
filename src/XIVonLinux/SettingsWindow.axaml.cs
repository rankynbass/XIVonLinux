using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Linux;

namespace XIVonLinux;

public partial class SettingsWindow : Window
{
    private readonly SettingsService settingsService;

    /// <summary>Loaded Kamori branch metadata (index-aligned with BranchCombo entries).</summary>
    private readonly List<DalamudBranchMeta.Branch> branches = new();

    /// <summary>Kamori needs a User-Agent on every request (shared client convention).</summary>
    private static readonly HttpClient BranchHttp = CreateBranchClient();

    private static HttpClient CreateBranchClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "XIVonLinux/0.1 (FFXIV launcher; https://github.com/rankynbass/XIVonLinux)");
        return c;
    }

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

        // XIVLauncher.Core Game tab parity: extra args, DPI awareness, Steam-ignore,
        // XL auth macros, uid cache toggle + reset panel.
        AdditionalArgsBox.Text = s.AdditionalArgs;
        DpiCombo.Items.Add("Aware");
        DpiCombo.Items.Add("Unaware");
        DpiCombo.SelectedItem = s.DpiAwareness;
        if (DpiCombo.SelectedItem is null)
            DpiCombo.SelectedIndex = 1;   // Unaware default
        IgnoreSteamCheck.IsChecked = s.IgnoreSteam;
        XlAuthCheck.IsChecked = s.UseXlAuthMacros;
        UidCacheCheck.IsChecked = s.UseUidCache;
        UidCachePanel.IsVisible = false;   // UID cache UI hidden until implemented
        DalamudCheck.IsChecked = s.DalamudEnabled;

        // Dalamud tab (XIVLauncher.Core parity): load method, branch, injection delay,
        // manual injector override. Combo Tags carry the DalamudLoadMethod enum names.
        LoadMethodCombo.Items.Add(new ComboBoxItem { Content = "Entrypoint", Tag = "EntryPoint" });
        LoadMethodCombo.Items.Add(new ComboBoxItem { Content = "DLL Injection", Tag = "DllInject" });
        LoadMethodCombo.Items.Add(new ComboBoxItem { Content = "ACL-only fix", Tag = "ACLonly" });
        foreach (var item in LoadMethodCombo.Items)
            if ((item as ComboBoxItem)?.Tag as string == s.LoadMethod)
                LoadMethodCombo.SelectedItem = item;
        if (LoadMethodCombo.SelectedItem is null)
            LoadMethodCombo.SelectedIndex = 0;   // Entrypoint default
        InjectionDelayBox.Text = s.LoadDelay.ToString();
        ManualInjectCheck.IsChecked = s.ManualInjectEnabled;
        ManualInjectPathBox.Text = s.ManualInjectPath;
        ApplyManualInjectMode(s.ManualInjectEnabled);

        // Branch combo fills once Kamori meta arrives, like XIVLauncher.Core's
        // DalamudBranchMetaSettingsEntry (created disabled, populated on fetch).
        // Item Tags carry the branch Track; item 0 is the synthetic Release entry
        // (Track "", i.e. the updater's default release path).
        BranchCombo.Items.Add(new ComboBoxItem { Content = "Release", Tag = "" });
        BranchCombo.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(s.BranchTrack))
            BranchCombo.Items[^1] = new ComboBoxItem { Content = s.BranchTrack, Tag = s.BranchTrack };
        _ = LoadBranchesAsync();

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

    /// <summary>Fetches Kamori branch metadata once, then fills BranchCombo with the
    /// non-hidden branches (item Tags carry Track; the saved Track pre-selects).</summary>
    private async Task LoadBranchesAsync()
    {
        IEnumerable<DalamudBranchMeta.Branch> fetched;
        try
        {
            fetched = await DalamudBranchMeta.FetchBranchesAsync(BranchHttp);
        }
        catch
        {
            return;   // keep the Release-only combo; saving the current selection is fine
        }

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var b in fetched)
                if (!b.Hidden)
                    this.branches.Add(b);

            BranchCombo.Items.Clear();
            BranchCombo.Items.Add(new ComboBoxItem { Content = "Release", Tag = "" });
            foreach (var b in this.branches)
                BranchCombo.Items.Add(new ComboBoxItem { Content = b.DisplayName, Tag = b.Track });

            var saved = this.settingsService.Settings.BranchTrack;
            for (var i = 0; i < BranchCombo.Items.Count; i++)
                if ((BranchCombo.Items[i] as ComboBoxItem)?.Tag as string == saved)
                {
                    BranchCombo.SelectedIndex = i;
                    return;
                }
            BranchCombo.SelectedIndex = 0;
        });
    }

    /// <summary>Manual-injection rows show only when the override is checked.</summary>
    private void ApplyManualInjectMode(bool enabled)
    {
        ManualInjectLabel.IsVisible = enabled;
        ManualInjectPathBox.IsVisible = enabled;
        ManualInjectBrowseButton.IsVisible = enabled;
    }

    private void OnManualInjectToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ApplyManualInjectMode(ManualInjectCheck.IsChecked == true);

    private async void OnBrowseManualInject(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(ManualInjectPathBox);

    /// <summary>UID cache panel shows only while the cache toggle is on (XIVLauncher.Core footer).</summary>
    private void OnUidCacheToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        UidCachePanel.IsVisible = UidCacheCheck.IsChecked == true;
    }

    /// <summary>Clears the cached uid and persists; the launcher must restart to take effect
    /// (XIVLauncher.Core: "you must then restart the launcher").</summary>
    private void OnResetUidCache(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (this.settingsService is null)
            return;
        this.settingsService.Settings.UidCache = "";
        this.settingsService.Save();
        ShowAlert("UID cache cleared. Restart the launcher to take effect.");
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
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
        ok.Click += (_, _) => popup.Close();
        popup.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16, 12, 16, 12),
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok },
        };
        popup.ShowDialog(this);
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

        // XIVLauncher.Core CheckValidity: game folder must be set, and must not be the
        // game/ or boot/ subfolder of an FFXIV install (the common mispick).
        var gameName = Path.GetFileName(s.GamePath.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(s.GamePath) || gameName == "game" || gameName == "boot")
        {
            ShowAlert("GamePath is invalid — pick the FFXIV install folder (the one containing\n" +
                      "the game/ and boot/ subfolders), not game/ or boot/ itself.");
            return;
        }

        s.AdditionalArgs = AdditionalArgsBox.Text;
        s.DpiAwareness = DpiCombo.SelectedItem as string ?? "Unaware";
        s.IgnoreSteam = IgnoreSteamCheck.IsChecked == true;
        s.UseXlAuthMacros = XlAuthCheck.IsChecked == true;
        s.UseUidCache = UidCacheCheck.IsChecked == true;
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

        // Dalamud tab: load method name, delay, branch, manual injector path.
        s.LoadMethod = (LoadMethodCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "DllInject";
        s.LoadDelay = int.TryParse(InjectionDelayBox.Text, out var delay) && delay >= 0 ? delay : 1000;
        s.ManualInjectEnabled = ManualInjectCheck.IsChecked == true;
        s.ManualInjectPath = ManualInjectPathBox.Text;
        if (s.ManualInjectEnabled &&
            (string.IsNullOrEmpty(s.ManualInjectPath) || !Directory.Exists(s.ManualInjectPath) ||
             !File.Exists(Path.Combine(s.ManualInjectPath, "Dalamud.Injector.exe"))))
        {
            ShowAlert("Manual injection needs a folder containing Dalamud.Injector.exe.\nFix the path, or untick manual injection.");
            return;   // do not persist/close with an invalid override
        }
        var selBranch = (BranchCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        s.BranchTrack = selBranch;
        s.BranchKey = this.branches.Find(b => b.Track == selBranch)?.Key ?? "";

        this.settingsService.Save();
        Close();
    }
}
