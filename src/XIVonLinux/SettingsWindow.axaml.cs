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
        var client = new HttpClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "XIVonLinux/0.1 (FFXIV launcher; https://github.com/rankynbass/XIVonLinux)");
        return client;
    }

    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <summary>Opens prefilled from the shared SettingsService; Save writes back and persists.</summary>
    public SettingsWindow(SettingsService service) : this()
    {
        this.settingsService = service;
        var settings = service.Settings;
        GamePathBox.Text = settings.GamePath;
        ConfigPathBox.Text = settings.ConfigPath;
        PatchPathBox.Text = settings.PatchPath;
        ProtonPathBox.Text = settings.ProtonPath;
        PrefixPathBox.Text = settings.PrefixPath;
        DxvkCheck.IsChecked = settings.EnableDxvk;
        ExtraWineDllOverridesBox.Text = settings.ExtraWineDllOverrides;
        WineDebugCheck.IsChecked = settings.WineDebugEnabled;
        WineDebugBox.Text = string.IsNullOrWhiteSpace(settings.WineDebugValue) ? LaunchSettings.DefaultWineDebugValue : settings.WineDebugValue;
        WineDebugBox.IsEnabled = WineDebugCheck.IsChecked == true;
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
        DpiCombo.Items.Add("Aware");
        DpiCombo.Items.Add("Unaware");
        DpiCombo.SelectedItem = settings.DpiAwareness;
        if (DpiCombo.SelectedItem is null)
            DpiCombo.SelectedIndex = 1;   // Unaware default
        IgnoreSteamCheck.IsChecked = settings.IgnoreSteam;
        XlAuthCheck.IsChecked = settings.UseXlAuthMacros;
        UidCacheCheck.IsChecked = settings.UseUidCache;
        UidCachePanel.IsVisible = false;   // UID cache UI hidden until implemented
        DalamudCheck.IsChecked = settings.DalamudEnabled;

        // Dalamud tab (XIVLauncher.Core parity): load method, branch, injection delay,
        // manual injector override. Combo Tags carry the DalamudLoadMethod enum names.
        LoadMethodCombo.Items.Add(new ComboBoxItem { Content = "Entrypoint", Tag = "EntryPoint" });
        LoadMethodCombo.Items.Add(new ComboBoxItem { Content = "DLL Injection", Tag = "DllInject" });
        LoadMethodCombo.Items.Add(new ComboBoxItem { Content = "ACL-only fix", Tag = "ACLonly" });
        foreach (var item in LoadMethodCombo.Items)
            if ((item as ComboBoxItem)?.Tag as string == settings.LoadMethod)
                LoadMethodCombo.SelectedItem = item;
        if (LoadMethodCombo.SelectedItem is null)
            LoadMethodCombo.SelectedIndex = 0;   // Entrypoint default
        InjectionDelayBox.Text = settings.LoadDelay.ToString();
        ManualInjectCheck.IsChecked = settings.ManualInjectEnabled;
        ManualInjectPathBox.Text = settings.ManualInjectPath;
        ApplyManualInjectMode(settings.ManualInjectEnabled);

        // Branch combo fills once Kamori meta arrives, like XIVLauncher.Core's
        // DalamudBranchMetaSettingsEntry (created disabled, populated on fetch).
        // Item Tags carry the branch Track; item 0 is the synthetic Release entry
        // (Track "", i.e. the updater's default release path).
        BranchCombo.Items.Add(new ComboBoxItem { Content = "Release", Tag = "" });
        BranchCombo.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(settings.BranchTrack))
            BranchCombo.Items[^1] = new ComboBoxItem { Content = settings.BranchTrack, Tag = settings.BranchTrack };
        _ = LoadBranchesAsync();

        // Proton enumeration (Alex): managed mode picks from the Proton builds found in
        // Steam compatibilitytools.d, steamapps/common, and the app's tools/proton dir.
        foreach (var dir in EnumerateProtonDirs())
            ProtonCombo.Items.Add(dir);
        if (ProtonCombo.Items.Contains(settings.ProtonPath))
            ProtonCombo.SelectedItem = settings.ProtonPath;
        ManagedProtonCheck.IsChecked = settings.ManagedProton;
        ApplyProtonMode(settings.ManagedProton);

        // Umu enumeration (Alex): managed mode picks the latest/previous managed zipapp
        // installs under ~/.xivonlinux/tools/umu; custom mode is a folder picker for a
        // disk-side folder containing umu-run.
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

    private void OnManagedUmuToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        => ApplyUmuMode(ManagedUmuCheck.IsChecked == true);

    /// <summary>The WINEDEBUG value box edits only while logging is enabled; when
    /// disabled, launches force WINEDEBUG=-all.</summary>
    private void OnWineDebugToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        => WineDebugBox.IsEnabled = WineDebugCheck.IsChecked == true;

    private void OnUmuCombo(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        // Keep the custom textbox in sync with the selection (tag without the display suffix).
        if (UmuCombo.SelectedItem is string sel)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            UmuPathBox.Text = Path.Combine(home, ".xivonlinux", "tools", "umu", sel.Split(" (")[0]);
        }
    }

    private async void OnBrowseUmu(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
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
            foreach (var dir in Directory.GetDirectories(root))
                if (File.Exists(Path.Combine(dir, "proton")))
                    found.Add(dir);
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

    private void OnManagedProtonToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        => ApplyProtonMode(ManagedProtonCheck.IsChecked == true);

    private void OnProtonCombo(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (ProtonCombo.SelectedItem is string path)
            ProtonPathBox.Text = path;   // keep the text row in sync for custom mode
    }

    private async void OnBrowseGame(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        => await PickFolderAsync(GamePathBox);

    private async void OnBrowseConfig(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        => await PickFolderAsync(ConfigPathBox);

    private async void OnBrowsePatch(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        => await PickFolderAsync(PatchPathBox);

    private async void OnBrowseProton(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        => await PickFolderAsync(ProtonPathBox);

    private async void OnBrowsePrefix(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
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
            foreach (var verB in fetched)
                if (!verB.Hidden)
                    this.branches.Add(verB);

            BranchCombo.Items.Clear();
            BranchCombo.Items.Add(new ComboBoxItem { Content = "Release", Tag = "" });
            foreach (var verB in this.branches)
                BranchCombo.Items.Add(new ComboBoxItem { Content = verB.DisplayName, Tag = verB.Track });

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

    private void OnManualInjectToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        => ApplyManualInjectMode(ManualInjectCheck.IsChecked == true);

    private async void OnBrowseManualInject(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        => await PickFolderAsync(ManualInjectPathBox);

    /// <summary>UID cache panel shows only while the cache toggle is on (XIVLauncher.Core footer).</summary>
    private void OnUidCacheToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        UidCachePanel.IsVisible = UidCacheCheck.IsChecked == true;
    }

    /// <summary>Clears the cached uid and persists; the launcher must restart to take effect
    /// (XIVLauncher.Core: "you must then restart the launcher").</summary>
    private void OnResetUidCache(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
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
        var okButton = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Avalonia.Thickness(0, 12, 0, 0) };
        okButton.Click += (_, _) => popup.Close();
        popup.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16, 12, 16, 12),
            Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, okButton },
        };
        popup.ShowDialog(this);
    }

    /// <summary>Persist all settings, close the modal; the main window refocuses afterwards.</summary>
    private void OnSave(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (this.settingsService is null)
            return;
        var settings = this.settingsService.Settings;
        settings.GamePath = GamePathBox.Text;
        settings.ConfigPath = ConfigPathBox.Text;
        settings.PatchPath = PatchPathBox.Text;
        settings.Language = LanguageCombo.SelectedItem as string ?? "English";

        // XIVLauncher.Core CheckValidity: game folder must be set, and must not be the
        // game/ or boot/ subfolder of an FFXIV install (the common mispick).
        var gameName = Path.GetFileName(settings.GamePath.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(settings.GamePath) || gameName == "game" || gameName == "boot")
        {
            ShowAlert("GamePath is invalid — pick the FFXIV install folder (the one containing\n" +
                      "the game/ and boot/ subfolders), not game/ or boot/ itself.");
            return;
        }

        settings.AdditionalArgs = AdditionalArgsBox.Text;
        settings.DpiAwareness = DpiCombo.SelectedItem as string ?? "Unaware";
        settings.IgnoreSteam = IgnoreSteamCheck.IsChecked == true;
        settings.UseXlAuthMacros = XlAuthCheck.IsChecked == true;
        settings.UseUidCache = UidCacheCheck.IsChecked == true;
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
        settings.ExtraWineDllOverrides = ExtraWineDllOverridesBox.Text;
        settings.WineDebugEnabled = WineDebugCheck.IsChecked == true;
        settings.WineDebugValue = string.IsNullOrWhiteSpace(WineDebugBox.Text)
            ? LaunchSettings.DefaultWineDebugValue : WineDebugBox.Text;
        settings.DalamudEnabled = DalamudCheck.IsChecked == true;

        // Dalamud tab: load method name, delay, branch, manual injector path.
        settings.LoadMethod = (LoadMethodCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "DllInject";
        settings.LoadDelay = int.TryParse(InjectionDelayBox.Text, out var delay) && delay >= 0 ? delay : 1000;
        settings.ManualInjectEnabled = ManualInjectCheck.IsChecked == true;
        settings.ManualInjectPath = ManualInjectPathBox.Text;
        if (settings.ManualInjectEnabled &&
            (string.IsNullOrEmpty(settings.ManualInjectPath) || !Directory.Exists(settings.ManualInjectPath) ||
             !File.Exists(Path.Combine(settings.ManualInjectPath, "Dalamud.Injector.exe"))))
        {
            ShowAlert("Manual injection needs a folder containing Dalamud.Injector.exe.\nFix the path, or untick manual injection.");
            return;   // do not persist/close with an invalid override
        }
        var selBranch = (BranchCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        settings.BranchTrack = selBranch;
        settings.BranchKey = this.branches.Find(verB => verB.Track == selBranch)?.Key ?? "";

        this.settingsService.Save();
        Close();
    }
}
