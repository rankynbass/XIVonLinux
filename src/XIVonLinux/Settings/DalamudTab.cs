using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using XIVLauncher.Common.Dalamud;

namespace XIVonLinux.Settings;

/// <summary>Dalamud tab: enabled toggle, load method, injection delay, manual injector
/// override, Kamori branch combo. Partial of SettingsWindow; the core dispatches load
/// through DalamudTabLoad and save through DalamudTabSave.</summary>
public partial class SettingsWindow
{
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

    private void DalamudTabLoad()
    {
        var settings = this.settingsService.Settings;
        DalamudCheck.IsChecked = settings.DalamudEnabled;

        // Dalamud tab (XIVLauncher.Core parity): load method, branch, injection delay,
        // manual injector override. Combo Tags carry the DalamudLoadMethod enum names.
        LoadMethodCombo.Items.Clear();
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
        BranchCombo.Items.Clear();
        BranchCombo.Items.Add(new ComboBoxItem { Content = "Release", Tag = "" });
        BranchCombo.SelectedIndex = 0;
        if (!string.IsNullOrEmpty(settings.BranchTrack))
            BranchCombo.Items[^1] = new ComboBoxItem { Content = settings.BranchTrack, Tag = settings.BranchTrack };
        _ = LoadBranchesAsync();
    }

    /// <summary>Commits Dalamud tab fields into settings; returns false (alert shown) when
    /// manual injection is enabled with a path lacking Dalamud.Injector.exe.</summary>
    private bool DalamudTabSave(LaunchSettings settings)
    {
        settings.DalamudEnabled = DalamudCheck.IsChecked == true;
        settings.LoadMethod = (LoadMethodCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "DllInject";
        settings.LoadDelay = int.TryParse(InjectionDelayBox.Text, out var delay) && delay >= 0 ? delay : 1000;
        settings.ManualInjectEnabled = ManualInjectCheck.IsChecked == true;
        settings.ManualInjectPath = ManualInjectPathBox.Text;
        if (settings.ManualInjectEnabled &&
            (string.IsNullOrEmpty(settings.ManualInjectPath) || !Directory.Exists(settings.ManualInjectPath) ||
             !File.Exists(Path.Combine(settings.ManualInjectPath, "Dalamud.Injector.exe"))))
        {
            ShowAlert("Manual injection needs a folder containing Dalamud.Injector.exe.\nFix the path, or untick manual injection.");
            return false;   // do not persist/close with an invalid override
        }
        var selBranch = (BranchCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        settings.BranchTrack = selBranch;
        settings.BranchKey = this.branches.Find(verB => verB.Track == selBranch)?.Key ?? "";
        return true;
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

    private void OnManualInjectToggled(object? sender, RoutedEventArgs eventArgs)
        => ApplyManualInjectMode(ManualInjectCheck.IsChecked == true);

    private async void OnBrowseManualInject(object? sender, RoutedEventArgs eventArgs)
        => await PickFolderAsync(ManualInjectPathBox);
}
