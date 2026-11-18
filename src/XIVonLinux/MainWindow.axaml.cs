using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Linux;
using XivGame = XIVLauncher.Common.Game.Launcher;

using XIVonLinux.Download;
using XIVonLinux.Settings;

namespace XIVonLinux;

public partial class MainWindow : Window
{
    // Launcher client config (Kamori GetLauncherClientConfig), fetched at startup:
    // frontier URL template (two holes: lang, time) + killswitch boot-version cutoff.
    // Plain values handed to the library at call sites (layering rule).
    private string frontierUrl = XIVonLinux.Net.LauncherClientConfig.FallbackFrontierUrl;
    private string? cutOffBootver;
    private uint flags;

    private int activeLaunches;
    private readonly SettingsService settingsService;
    /// <summary>Saved login accounts (~/.xivonlinux/accounts.toml); passwords are held
    /// in the manager's in-memory cache only (credentials are never persisted).</summary>
    private readonly AccountManager accounts;
    /// <summary>Background Dalamud installer/updater (XIVLauncher.Common.Dalamud.DalamudUpdater).
    /// Started at launcher startup: resolves the release version, downloads the injector +
    /// .NET runtime into ~/.xivonlinux (dalamud/, runtime/, dalamudAssets/) as needed.
    /// The login pipeline waits for State == Done before launching with the injector.</summary>
    private readonly DalamudUpdater dalamudUpdater;
    /// <summary>Managed umu-launcher zipapp versions (~/.xivonlinux/tools/umu; latest +
    /// previous kept in case a bad update is pushed). The pipeline uses the newest
    /// extracted umu-run, falling back to the system /usr/sbin/umu-run.</summary>
    private readonly UmuManager umuManager;
    private string umuRunPath = "/usr/sbin/umu-run";

    /// <summary>File all in-prefix umu-run stderr drains into (STORAGE/logs/wine.log).</summary>
    private string WineLogPath => Path.Combine(this.settingsService.Root.FullName, "logs", "wine.log");

    /// <summary>Branch track the updater last ran with; a settings change to a
    /// different track re-runs the updater in the background on close.</summary>
    private string lastBranchTrack = "";

    /// <summary>Fire-and-forget the updater for the saved branch (empty track = release).</summary>
    private void RunDalamudUpdaterForBranch()
    {
        var settings = this.settingsService.Settings;
        this.dalamudUpdater.Run(
            string.IsNullOrEmpty(settings.BranchTrack) ? null : settings.BranchTrack,
            string.IsNullOrEmpty(settings.BranchKey) ? null : settings.BranchKey);
        this.lastBranchTrack = settings.BranchTrack;
    }

    /// <summary>Public entry for re-firing the background updater (Clear Dalamud deletes
    /// the downloaded files after Run has completed; the updater must re-download them).</summary>
    public void RestartDalamudUpdater() => RunDalamudUpdaterForBranch();

    /// <summary>umu-run to invoke, resolved from settings: the managed latest/previous
    /// tag picked in the Proton tab, the custom picked folder's umu-run, falling back
    /// to the newest managed install, then the system binary.</summary>
    private string ResolveUmuRunPath()
    {
        var settings = this.settingsService.Settings;
        if (settings.ManagedUmu)
        {
            var path = this.umuManager.PathForTag(settings.UmuTag);
            if (path is not null)
                return path;
        }
        else if (!string.IsNullOrEmpty(settings.UmuPath))
        {
            var path = Path.Combine(settings.UmuPath, "umu-run");
            if (File.Exists(path))
                return path;
        }
        return this.umuManager.UmuRunPath();
    }

    public MainWindow()
    {
        InitializeComponent();

        // Title bar carries the assembly version injected by VersionSourceGenerator.
        Title = $"XIVonLinux {AppUtil.VersionInfo.Instance().Version.FileVersion}";

        // Fetch launcher client config in the background (frontier template + killswitch
        // cutoff); enforced at login time, XIVLauncher.Core LauncherApp shape.
        _ = Task.Run(async () =>
        {
            var config = await XIVonLinux.Net.LauncherClientConfig.GetAsync(Program.HttpClient);
            this.frontierUrl = config.frontierUrl;
            this.cutOffBootver = config.cutOffBootver;
            this.flags = config.flags;
            Log.Information("LauncherClientConfig: frontier={Frontier} cutOffBootver={Cutoff} flags={Flags}",
                config.frontierUrl, config.cutOffBootver, config.flags);
        });

        this.settingsService = new SettingsService();
        this.accounts = new AccountManager(this.settingsService);
        this.umuManager = new UmuManager(this.settingsService);
        _ = Task.Run(this.umuManager.EnsureLatestTwoAsync);   // background umu version sync
        // Login fields are not persisted in settings.toml: identity is prefilled from the
        // current account in accounts.db below; defaults are username "", Steam on, trial off.
        var settings = this.settingsService.Settings;
        UsernameBox.Text = settings.Username;
        SteamAccountCheck.IsChecked = settings.SteamAccount;
        FreeTrialCheck.IsChecked = settings.FreeTrial;

        // Pre-select the saved current account (XIVLauncher.Core startup shape).
        var saved = this.accounts.CurrentAccount;
        if (saved is not null) SwitchAccountFields(saved, saveAsCurrent: false);

        // Accounts menu: ContextMenu shown at click time. MenuFlyout in Avalonia 12.1.4
        // builds its popup before Opening fires, so items added in the Opening handler
        // never render — the documented dynamic-item pattern is ContextMenu.Opening.
        AccountsButton.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            BuildAccountsMenu(menu);   // Open() does not raise Opening in Avalonia 12.1.4, so build first
            menu.Open(AccountsButton);
        };

        this.dalamudUpdater = new DalamudUpdater(Program.HttpClient,
            this.settingsService.GetFolder("dalamud"),
            this.settingsService.GetFolder("runtime"),
            this.settingsService.GetFolder("dalamudAssets"),
            null, null)
        {
            Overlay = new SilentDalamudOverlay(),
        };
        RunDalamudUpdaterForBranch();   // fire-and-forget background check/download for the saved branch

        StartNewsLoad();

        // Quick test of the repair results popup: XIVON_FAKE_REPAIR shows it at startup
        // with the given outcome — no login, no verification. "1" = nothing changed;
        // "2,3" = 2 repaired, 3 removed.
        this.Opened += (_, __) =>
        {
            var fake = Environment.GetEnvironmentVariable("XIVON_FAKE_REPAIR");
            if (fake is null) return;
            var parts = fake.Split(',');
            var rep = parts.Length > 0 && int.TryParse(parts[0], out var r) ? r : 0;
            var mov = parts.Length > 1 && int.TryParse(parts[1], out var m) ? m : 0;
            ShowRepairResults(rep, mov);
        };
    }

    // ---------- settings modal ----------

    /// <summary>XIVLauncher.Core parity: button-bar glyphs open the My Account page and
    /// the FFXXIV service status page in the browser (Steam Deck: Steam overlay).</summary>
    private void OnOpenAccount(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        AppUtil.OpenBrowser("https://sqex.to/Msp");
    }

    private void OnOpenStatus(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        AppUtil.OpenBrowser("https://is.xivup.com/");
    }

    /// <summary>Modal settings window over the main page; Save persists and closes,
    /// then the main page is refocused.</summary>
    private async void OnSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        var dlg = new SettingsWindow(this.settingsService, this.accounts);
        await dlg.ShowDialog(this);
        var settings = this.settingsService.Settings;
        if (settings.BranchTrack != this.lastBranchTrack)
            RunDalamudUpdaterForBranch();   // branch changed: re-fetch in the background
        this.Focus();
    }

    /// <summary>Reset Settings re-prefill (called by SettingsWindow after Reinit): login
    /// fields fall back to the reset defaults — username/password cleared, Steam back on,
    /// trial off, no current account.</summary>
    public void PrefillFromCurrentAccount()
    {
        var settings = this.settingsService.Settings;
        UsernameBox.Text = settings.Username;
        PasswordBox.Text = settings.Password;
        SteamAccountCheck.IsChecked = settings.SteamAccount;
        FreeTrialCheck.IsChecked = settings.FreeTrial;
        var saved = this.accounts.CurrentAccount;
        if (saved is not null) SwitchAccountFields(saved, saveAsCurrent: false);
    }

    // ---------- log box visibility ----------

    /// <summary>Show/hide the log box (it replaces the news panel; the login side is
    /// never hidden). ShowLog(true) is called on Login press so pipeline errors are
    /// visible in the log box as they happen.</summary>
    private void ShowLog(bool show)
    {
        StatusLog.IsVisible = show;
        NewsPanel.IsVisible = !show;
        // Glyph-only log button: rectangle-list glyph is fixed (f022).
    }

    private void OnToggleLog(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        ShowLog(!StatusLog.IsVisible);
    }

    // ---------- accounts ----------

    /// <summary>Rebuilds the Accounts menu on open: saved accounts (name + Steam/OTP/Trial
    /// tags, XIVLauncher.Core AccountSwitcher shape), then Add/Update, Remove, Remove All.</summary>
    private int BuildAccountsMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        if (this.accounts.Accounts.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No saved accounts", IsEnabled = false });

        foreach (var account in this.accounts.Accounts)
        {
            var name = account.UserName;
            if (account.UseSteamServiceAccount) name += " (Steam)";
            if (account.UseOtp) name += " (OTP)";
            if (account.IsFreeTrial) name += " (Trial)";

            var item = new MenuItem { Header = name };
            var captured = account;
            item.Click += (_, _) => SelectAccount(captured);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var add = new MenuItem { Header = "Add/Update Account" };
        add.Click += (_, _) =>
        {
            PersistAccount(UsernameBox.Text, PasswordBox.Text, OtpCheck.IsChecked == true,
                SteamAccountCheck.IsChecked == true, FreeTrialCheck.IsChecked == true,
                SavePasswordCheck.IsChecked == true, saveAsCurrent: true);
            Report(SavePasswordCheck.IsChecked == true
                ? $"Account '{UsernameBox.Text}' saved (password stored encrypted with the account's AES key)"
                : $"Account '{UsernameBox.Text}' saved (password kept in memory only)");
        };

        var remove = new MenuItem { Header = "Remove Current Account" };
        remove.Click += (_, _) =>
        {
            var target = this.accounts.ByUserName(UsernameBox.Text);
            if (target is not null)
            {
                this.accounts.RemoveAccount(target);
                Report($"Account '{target.UserName}' removed");
            }
            else
                Report("Current account is not saved");
        };

        var removeAll = new MenuItem { Header = "Remove All Accounts" };
        removeAll.Click += (_, _) =>
        {
            this.accounts.RemoveAllAccounts();
            Report("All accounts removed");
        };

        menu.Items.Add(add);
        menu.Items.Add(remove);
        menu.Items.Add(removeAll);
        return menu.Items.Count;
    }

    /// <summary>Menu pick: fill the login fields from the account (password comes from the
    /// in-memory cache, populated by past logins in this session; OTP prefills the last
    /// successful code when the account uses OTP) and mark it current.</summary>
    private void SelectAccount(XivAccount account)
    {
        SwitchAccountFields(account, saveAsCurrent: true);
        Report($"Switched to account '{account.UserName}'");
    }

    private void SwitchAccountFields(XivAccount account, bool saveAsCurrent)
    {
        UsernameBox.Text = account.UserName;
        PasswordBox.Text = account.Password;
        SteamAccountCheck.IsChecked = account.UseSteamServiceAccount;
        FreeTrialCheck.IsChecked = account.IsFreeTrial;
        OtpCheck.IsChecked = account.UseOtp;
        SavePasswordCheck.IsChecked = account.SavePassword;

        var settings = this.settingsService.Settings;
        settings.Username = account.UserName;
        settings.SteamAccount = account.UseSteamServiceAccount;
        settings.FreeTrial = account.IsFreeTrial;
        this.settingsService.Save();

        if (saveAsCurrent)
            this.accounts.CurrentAccount = account;
    }

    /// <summary>Unique-username persist (accounts.db, user_name PRIMARY KEY): a login
    /// registers the username once; re-logins with the same username update the stored
    /// flags (OTP/Steam/trial/save-password) instead of adding a second account. The
    /// password is encrypted with the account's unique AES key/vector and persisted
    /// only when savePassword is true; the OTP is not persisted (rotates every minute).</summary>
    private void PersistAccount(string username, string password, bool isOtp, bool isSteam, bool isFreeTrial,
        bool savePassword, bool saveAsCurrent = false)
    {
        if (string.IsNullOrEmpty(username))
            return;

        username = username.ToLower();

        var existing = this.accounts.ByUserName(username);
        if (existing is null)
        {
            existing = new XivAccount(username)
            {
                Password = password,
                SavePassword = savePassword,
                UseOtp = isOtp,
                UseSteamServiceAccount = isSteam,
                IsFreeTrial = isFreeTrial,
            };
            this.accounts.AddAccount(existing);
        }
        else
        {
            existing.Password = password;
            existing.UseOtp = isOtp;
            existing.UseSteamServiceAccount = isSteam;
            existing.IsFreeTrial = isFreeTrial;
            existing.SavePassword = savePassword;
            this.accounts.UpdateAccountFlags(existing);
        }

        if (saveAsCurrent)
            this.accounts.CurrentAccount = existing;
    }

    // ---------- login ----------

    private async void OnLogin(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        if (!await PrepareLoginFieldsAsync()) return;
        StartPipeline(this.settingsService.Settings.DalamudEnabled);
    }

    /// <summary>Shared login preparation for the Login button AND the Troubleshoot menu
    /// items: reads the fields, prompts the OTP popup when OTP is enabled (cancel
    /// aborts), persists the account. Returns false when the OTP popup was cancelled.
    /// XIVLauncher.Core persists the account at login time (MainPage.Login calls
    /// PersistAccount before the OAuth request), so pressing Login once registers
    /// the account in the account store; the Ok-path refreshes the password. The OTP
    /// is never persisted (it changes every minute).</summary>
    private async Task<bool> PrepareLoginFieldsAsync()
    {
        var settings = this.settingsService.Settings;
        settings.Username = UsernameBox.Text;
        settings.Password = PasswordBox.Text;
        settings.SteamAccount = SteamAccountCheck.IsChecked == true;
        settings.FreeTrial = FreeTrialCheck.IsChecked == true;

        // OTP is a true/false setting; when enabled, a popup prompts for the 6-digit code
        // and the login proceeds only when its Login button is pressed (Cancel aborts).
        // XIVLauncher.Core MainPage parity: a valid uid cache with the setting enabled skips
        // the OTP prompt; a valid cache with the setting disabled is reset before prompting.
        var isOtp = OtpCheck.IsChecked == true;
        var cacheValid = this.settingsService.UidCache.HasValidCache(settings.Username);

        if (isOtp && cacheValid && !settings.UseUidCache)
            this.settingsService.UidCache.Reset();

        if (isOtp && settings.UseUidCache && cacheValid)
            settings.OneTimePassword = "";
        else if (isOtp)
        {
            var code = await ShowOtpPopupAsync();
            if (code is null)
            {
                Report("Login cancelled (OTP popup)");
                return false;
            }
            settings.OneTimePassword = code;
        }
        else
            settings.OneTimePassword = "";

        this.settingsService.Save();

        PersistAccount(settings.Username, settings.Password, isOtp, settings.SteamAccount, settings.FreeTrial,
            SavePasswordCheck.IsChecked == true, saveAsCurrent: true);
        return true;
    }

    /// <summary>Fires the login pipeline. While a game is up, launches are refused
    /// (shared prefix is single-instance) rather than stacked. The modifiers mirror
    /// XIVLauncher.Core's LoginAction menu: noPlugins/noThirdPlugins go to the
    /// injector, noLaunch stops after patching.</summary>
    private void StartPipeline(bool withDalamud, bool noPlugins = false, bool noThirdPlugins = false, bool noLaunch = false)
    {
        if (activeLaunches > 0)
        {
            Report("A game is already running — close it before launching another");
            return;
        }
        // Captured on the UI thread here: the pipeline runs on a background task and
        // must not touch the checkbox directly.
        var savePassword = SavePasswordCheck.IsChecked == true;
        LoginButton.IsEnabled = false;
        // The login pipeline writes every step and every error into the log box;
        // show it now so failures are visible as they happen (menu items too).
        ShowLog(true);
        _ = Task.Run(() => RunLoginPipelineAsync(withDalamud, savePassword, noPlugins, noThirdPlugins, noLaunch));
    }

    /// <summary>Troubleshoot dropdown in XIVLauncher.Core's LoginAction menu format:
    /// the button shows the special-launch menu at itself (Core's caret does the same).
    /// Each item fires the login pipeline with the matching modifier, using the login
    /// fields as they are — the same as pressing Login with that modifier.</summary>
    private void OnTroubleshoot(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
    {
        // Same pattern as the Accounts menu: build items, then Open (Open() does not
        // raise Opening in Avalonia 12.1.4, and Click is an event, not settable).
        // Each item runs the FULL login preparation (OTP popup when enabled) before
        // firing the pipeline with its modifier — the same as pressing Login.
        var menu = new ContextMenu();
        var noD = new MenuItem { Header = "Launch without Dalamud" };
        noD.Click += async (_, __) => { if (await PrepareLoginFieldsAsync()) StartPipeline(false); };
        var noP = new MenuItem { Header = "Launch without plugins" };
        noP.Click += async (_, __) => { if (await PrepareLoginFieldsAsync()) StartPipeline(true, true); };
        var noT = new MenuItem { Header = "Launch without third-party plugins" };
        noT.Click += async (_, __) => { if (await PrepareLoginFieldsAsync()) StartPipeline(true, false, true); };
        var noL = new MenuItem { Header = "Patch without launching" };
        noL.Click += async (_, __) => { if (await PrepareLoginFieldsAsync()) StartPipeline(true, false, false, true); };
        var rep = new MenuItem { Header = "Repair game files" };
        rep.Click += async (_, __) => { if (await PrepareLoginFieldsAsync()) await RepairPipelineAsync(); };
        menu.Items.Add(noD);
        menu.Items.Add(noP);
        menu.Items.Add(noT);
        menu.Items.Add(noL);
        menu.Items.Add(rep);
        menu.Open((Control)sender!);
    }

    /// <summary>Log to Serilog (console/file) and mirror the line into the UI status box.</summary>
    // ---------- news frame ----------

    private const int BANNER_TIME_MS = 8000;

    private List<Banner> bannerList = new();
    private Bitmap[] bannerBitmaps = [];
    private int currentBanner;
    private LoginService? newsService;
    private DispatcherTimer? bannerTimer;

    /// Fetches headlines + banners via the launcher frontier API, loads banner images,
    /// then shows them on the UI thread (NewsFrame shape from XIVLauncher.Core).
    private void StartNewsLoad()
    {
        newsService = new LoginService(settingsService.Storage, settingsService.Settings.ConfigPath,
            settingsService.GetCacheFolder("patches").FullName, null!, ClientLanguage.English, this.frontierUrl,
            settingsService.Settings.MaxDownloadSpeedKibps, settingsService.UidCache);
        var svc = newsService;

        _ = Task.Run(async () =>
        {
            try
            {
                var (headlines, banners) = await svc.GetNewsAsync();

                var bitmaps = new List<Bitmap>();
                foreach (var banner in banners)
                {
                    var bytes = await Program.HttpClient.GetByteArrayAsync(banner.LsbBanner);
                    bitmaps.Add(await Dispatcher.UIThread.InvokeAsync(() => new Bitmap(new MemoryStream(bytes))));
                }

                Dispatcher.UIThread.Post(() => ShowNews(headlines, banners, bitmaps));
            }
            catch (Exception exception)
            {
                Log.Error(exception, "News load failed");
                Dispatcher.UIThread.Post(() => NewsLoadingText.Text = "News unavailable");
            }
        });
    }

    private void ShowNews(Headlines headlines, IReadOnlyList<Banner> banners, List<Bitmap> bitmaps)
    {
        NewsLoadingText.IsVisible = false;
        NewsList.Children.Clear();

        if (bitmaps.Count > 0)
        {
            bannerList = banners.ToList();
            bannerBitmaps = bitmaps.ToArray();
            BannerImage.IsVisible = true;
            BannerImage.Source = bannerBitmaps[0];
            BannerImage.Cursor = new Cursor(StandardCursorType.Hand);
            BannerImage.PointerPressed += (_, _) =>
            {
                var link = bannerList[currentBanner].Link;
                if (link != null) OpenUrl(link.ToString());
            };

            bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BANNER_TIME_MS) };
            bannerTimer.Tick += (_, _) =>
            {
                currentBanner = (currentBanner + 1) % bannerBitmaps.Length;
                BannerImage.Source = bannerBitmaps[currentBanner];
            };
            bannerTimer.Start();
        }

        AddNewsCaption("News");
        foreach (var newsItem in headlines.News) AddNewsEntry(newsItem.Title, newsItem.Url);

        if (headlines.Topics.Length > 0)
        {
            AddNewsCaption("Topics");
            foreach (var topic in headlines.Topics) AddNewsEntry(topic.Title, topic.Url);
        }
    }

    private void AddNewsCaption(string caption)
    {
        NewsList.Children.Add(new TextBlock { Text = caption, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 2) });
    }

    private void AddNewsEntry(string title, string url)
    {
        // News/Topics entries draw 1pt below the inherited default (12 -> 11), and
        // highlight with the theme accent on mouseover (cleared on exit, falling
        // back to the inherited Foreground).
        var textBlock = new TextBlock { Text = title, FontSize = 11, TextWrapping = TextWrapping.Wrap, Cursor = new Cursor(StandardCursorType.Hand) };
        var accent = new SolidColorBrush((Color)Avalonia.Application.Current.FindResource("SystemAccentColor"));
        textBlock.PointerEntered += (_, _) => textBlock.Foreground = accent;
        textBlock.PointerExited += (_, _) => textBlock.ClearValue(TextBlock.ForegroundProperty);
        textBlock.PointerPressed += (_, _) => OpenUrl(url);
        NewsList.Children.Add(textBlock);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Could not open {Url}", url);
        }
    }

    private void Report(string line)
    {
        Log.Information(line);
        Dispatcher.UIThread.Post(() => StatusLog.Text += line + "\n");
    }

    /// <summary>
    /// Popup warning: the game is not installed at the Game location and a full
    /// download will run first, with Continue and Cancel buttons. Shown on the UI
    /// thread (the pipeline runs on a background task), result delivered via TCS.
    /// </summary>
    private async Task<bool> ShowInstallConfirmAsync(string gamePathFull)
    {
        var tcs = new TaskCompletionSource<bool>();

        var dialog = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var msg = new TextBlock
            {
                Text = $"The game is not installed at:\n{gamePathFull}\n\nThe game will be downloaded first (boot + game patches from base, tens of GB). This can take a long time. Do you want to continue?",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 16, 16, 12),
            };

            var continueButton = new Button { Content = "Continue", Padding = new Thickness(16, 6), Margin = new Thickness(0, 0, 8, 0) };
            var cancelButton = new Button { Content = "Cancel", Padding = new Thickness(16, 6) };

            var row = new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(16, 0, 16, 16),
            };
            DockPanel.SetDock(continueButton, Dock.Right);
            DockPanel.SetDock(cancelButton, Dock.Right);
            row.Children.Add(cancelButton);
            row.Children.Add(continueButton);

            var panel = new StackPanel();
            panel.Children.Add(msg);
            panel.Children.Add(row);

            var window = new Window
            {
                Title = "Game not installed",
                Content = panel,
                Width = 460,
                Height = 230,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };

            continueButton.Click += (_, __) => { tcs.TrySetResult(true); Dispatcher.UIThread.Post(() => window.Close()); };
            cancelButton.Click += (_, __) => { tcs.TrySetResult(false); Dispatcher.UIThread.Post(() => window.Close()); };

            window.Show(this);
            return window;
        });

        var cont = await tcs.Task;
        Dispatcher.UIThread.Post(() => dialog.Close());
        return cont;
    }

    /// <summary>Environment flag reader tolerant of 1/0 (bool.Parse, used by the submodule's
    /// CheckEnvBool, accepts only true/false and throws on 1).</summary>
    private static bool EnvBool(string var)
    {
        var envValue = Environment.GetEnvironmentVariable(var);
        return envValue is "1" || string.Equals(envValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Killswitch gate (XIVLauncher.Core LauncherApp shape): when the server-published
    /// cutoff is below the local boot version, the login protocol changed during a recent patch
    /// and the launcher refuses to log in. XL_NO_KILLSWITCH=1 bypasses (EnvironmentSettings).</summary>
    private string? KillswitchCheck(string gamePathFull)
    {
        if (EnvBool("XL_NO_KILLSWITCH"))
            return null;
        if (string.IsNullOrEmpty(cutOffBootver))
            return null;

        var bootVer = Repository.Boot.GetVer(new DirectoryInfo(gamePathFull));
        var boot = SeVersion.Parse(bootVer);
        var cutoff = SeVersion.Parse(cutOffBootver);

        if (boot.CompareTo(cutoff) > 0)
            return "XIVonLinux is unavailable at this time as there were changes to the login process during a recent patch." +
                   "\n\nWe need to adjust to these changes and verify that our adjustments are safe before we can re-enable the launcher." +
                   "\n\nYou can use the Official Launcher instead until XIVonLinux has been updated." +
                   $"\n\n(local boot: {bootVer}, cutoff: {cutOffBootver})";
        return null;
    }

    /// <summary>Single-button killswitch message popup (same window shape as ShowInstallConfirmAsync).</summary>
    private async Task ShowKillswitchPopupAsync(string message)
        => await ShowPopupAsync("Killswitch", message);

    /// <summary>Modal-style warning popup: wrapped message + Close button, centered on
    /// the main window (killswitch/OTP popup shape reused for all popup alerts).</summary>
    private async Task ShowPopupAsync(string title, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var msg = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 16, 16, 12),
            };

            var closeButton = new Button { Content = "Close", Padding = new Thickness(16, 6) };
            var row = new DockPanel { LastChildFill = false, Margin = new Thickness(16, 0, 16, 16) };
            DockPanel.SetDock(closeButton, Dock.Right);
            row.Children.Add(closeButton);

            var panel = new StackPanel();
            panel.Children.Add(msg);
            panel.Children.Add(row);

            var window = new Window
            {
                Title = title,
                Content = panel,
                Width = 520,
                Height = 280,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };

            closeButton.Click += (_, __) => Dispatcher.UIThread.Post(() => window.Close());
            window.Show(this);
        });
    }

    /// <summary>OTP popup (spec: 6-digit prompt with Login/Cancel buttons). Returns the code
    /// when its Login button presses with a valid 6-digit code, null when Cancel is pressed.
    /// Enter in the code box triggers the same path as the Login button. Modal on the main window.</summary>
    private async Task<string?> ShowOtpPopupAsync()
    {
        var tcs = new TaskCompletionSource<string?>();

        var dialog = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var msg = new TextBlock
            {
                Text = "Enter the 6-digit code from your authenticator app:",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 16, 16, 8),
            };
            var codeBox = new TextBox { Margin = new Thickness(16, 0, 16, 4) };
            var hint = new TextBlock { Text = "", Foreground = Brushes.Red, Margin = new Thickness(16, 0, 16, 4) };

            var loginButton = new Button { Content = "Login", Padding = new Thickness(16, 6), Margin = new Thickness(0, 0, 8, 0) };
            var cancelButton = new Button { Content = "Cancel", Padding = new Thickness(16, 6) };

            var row = new DockPanel { LastChildFill = false, Margin = new Thickness(16, 8, 16, 16) };
            DockPanel.SetDock(loginButton, Dock.Right);
            DockPanel.SetDock(cancelButton, Dock.Right);
            row.Children.Add(cancelButton);
            row.Children.Add(loginButton);

            var panel = new StackPanel();
            panel.Children.Add(msg);
            panel.Children.Add(codeBox);
            panel.Children.Add(hint);
            panel.Children.Add(row);

            var window = new Window
            {
                Title = "One-Time Password",
                Content = panel,
                Width = 420,
                Height = 190,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };

            void TryLogin()
            {
                var code = codeBox.Text;
                if (code.Length != 6 || !code.All(char.IsDigit))
                {
                    hint.Text = "The code must be exactly 6 digits";
                    return;
                }
                tcs.TrySetResult(code);
                Dispatcher.UIThread.Post(() => window.Close());
            }

            loginButton.Click += (_, __) => TryLogin();
            cancelButton.Click += (_, __) => { tcs.TrySetResult(null); Dispatcher.UIThread.Post(() => window.Close()); };
            codeBox.KeyDown += (_, eventArgs) => { if (eventArgs.Key == Key.Enter) TryLogin(); };

            // Focus the code box on open so the user can type immediately.
            window.Opened += (_, __) => codeBox.Focus();

            window.Show(this);
            return window;
        });

        var codeResult = await tcs.Task;
        Dispatcher.UIThread.Post(() => dialog.Close());
        return codeResult;
    }

    // ---- download progress popup ----------------------------------------------------
    // The popup lives in DownloadPopup.axaml(.cs): overall bar + 0-4 file bars,
    // rebuilt every second, auto-close when all downloads finish. These wrappers
    // show it against the main window as owner and close it on install completion.
    // Fake test mode lives inside DownloadPopup (construct with fakeMode: true);
    // no auto-launch hook — the popup is exercised by real installs.

    private DownloadPopup? progressPopup;

    private void OpenProgressPopup(string title, bool fakeMode = false, bool repairMode = false)
    {
        progressPopup = new DownloadPopup(title, fakeMode, repairMode);
        progressPopup.Show(this);
    }

    private void CloseProgressPopup()
    {
        progressPopup?.ClosePopup();
        progressPopup = null;
    }

    /// <summary>
    /// Milestone 4 pipeline: settings -> prefix ensure (create + bootstrap) -> gate check
    /// -> real SE login (OauthLogin + RegisterSession) -> game patching when the server
    /// returns a patch list -> launch with the encrypted login session args, via the
    /// in-prefix Windows injector (Dalamud) or plain LinuxGameRunner.
    /// Runs off the UI thread; first-run prefix creation and patching can take minutes.
    /// The WINEPREFIX root comes from settings.PrefixPath (default ~/.xivonlinux),
    /// so prefix, settings and dalamud data all live under ~/.xivonlinux.
    /// </summary>
    private async Task RunLoginPipelineAsync(bool withDalamud, bool savePassword, bool noPlugins, bool noThirdPlugins, bool noLaunch)
    {
        LoginService? service = null;
        try
        {
            var settings = this.settingsService.Settings;
            // Killswitch flags bitmask (XIVLauncher LeaseFeatureFlags): bit 0 = GlobalDisableDalamud,
            // launched without the in-game addon on patch days, regardless of the user setting.
            if ((flags & 1) != 0 && withDalamud)
            {
                Report("GlobalDisableDalamud flag set — launching without Dalamud");
                withDalamud = false;
            }

            Report($"Pipeline start (login, dalamud: {withDalamud})");

            var prefixRoot = string.IsNullOrEmpty(settings.PrefixPath)
                ? this.settingsService.Root.FullName
                : settings.PrefixPath;
            Directory.CreateDirectory(prefixRoot);

            var gamePath = new DirectoryInfo(settings.GamePath);
            var gameExe = new FileInfo(Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe"));
            var gameInstalled = File.Exists(gameExe.FullName);
            Report($"Game location: {gamePath.FullName}");

            // Killswitch gate (XIVLauncher.Core LauncherApp shape): server-published
            // cutoff vs local boot version — login protocol changed on patch days.
            var killswitch = KillswitchCheck(gamePath.FullName);
            if (killswitch is not null)
            {
                Report($"KILL SWITCH: boot {Repository.Boot.GetVer(gamePath)} > cutoff {cutOffBootver} — login refused.");
                await ShowKillswitchPopupAsync(killswitch);
                return;
            }

            var proton = settings.ProtonPath;
            if (string.IsNullOrEmpty(proton))
            {
                Report("Proton path empty — launch refused.");
                await ShowPopupAsync("Proton Runner", "No proton runner selected. You must go into settings and choose one. " +
                    "Use ProtonPlus or protonup-qt to manage proton runners.");
                return;
            }
            if (!Directory.Exists(proton))
            {
                Report($"Proton path not found: {proton} — pick a Proton (default GE-Proton 11-6) under compatibilitytools.d; umu auto-select would upgrade a GE prefix, so launching is refused.");
                return;
            }
            Report($"Proton: {Path.GetFileName(proton.TrimEnd('/', '\\'))}");

            // WINEDLLOVERRIDES: exact DXVK preset per the Enable DXVK setting, with the
            // user's extra overrides appended (joined with ';' when non-empty).
            var dxvkPreset = settings.EnableDxvk
                ? "msquic=,mscoree=n,b;d3d9,d3d11,d3d10core,dxgi=n,b"
                : "msquic=,mscoree=n,b;d3d9,d3d11,d3d10core,dxgi=b";
            var winedllOverrides = string.IsNullOrEmpty(settings.ExtraWineDllOverrides)
                ? dxvkPreset
                : dxvkPreset + ";" + settings.ExtraWineDllOverrides;

            // GE/Proton convention: WINEPREFIX is the compatdata ROOT, proton appends pfx/.
            // Steam runtime container mounts: only bind-mounted dirs are visible inside
            // the prefix, so the config folder (UserPath) must be mounted explicitly.
            // When the game lived on the /games drive, the whole drive was mounted and
            // config came along for free; with the game elsewhere, config needs its own.
            var configMount = this.settingsService.Settings.ConfigPath.TrimEnd(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            Directory.CreateDirectory(configMount);
            var compatMounts = string.IsNullOrEmpty(configMount) ? gamePath.FullName : gamePath.FullName + ":" + configMount;
            var prefix = new UmuPrefix(
                new DirectoryInfo(Path.Combine(prefixRoot, "pfx")),
                prefixRoot,
                gamePath.FullName,
                "umu-steam",
                proton,
                compatMounts,
                winedllOverrides);
            Report($"Prefix root: {prefixRoot}");
            Report($"STEAM_COMPAT_MOUNTS: {compatMounts}");
            Report($"WINEDLLOVERRIDES: {winedllOverrides}");

            // Managed umu-launcher: keep latest + previous zipapp in tools/umu, use the
            // newest; system umu-run remains the fallback when nothing is installed.
            await this.umuManager.EnsureLatestTwoAsync();
            this.umuRunPath = ResolveUmuRunPath();
            Report($"Using umu-run: {this.umuRunPath}");

            // Prefixes are umu-version-sensitive (steamruntime major changes break them:
            // a 1.1.0-era prefix hangs under 1.4.4). Track the umu identity in a marker
            // under the prefix root; on any change, clear the prefix so the bootstrap
            // below rebuilds it clean. Custom picks key on their folder name, the
            // system binary on "system".
            var rootFull = this.settingsService.Root.FullName;
            var umuKey = this.umuRunPath.StartsWith(rootFull)
                ? Path.GetFileName(Path.GetDirectoryName(this.umuRunPath))
                : this.umuRunPath == "/usr/sbin/umu-run"
                    ? "system"
                    : "custom:" + Path.GetFileName(Path.GetDirectoryName(this.umuRunPath));
            var umuMarker = Path.Combine(prefixRoot, "umu-version");
            if (!File.Exists(umuMarker) || File.ReadAllText(umuMarker).Trim() != umuKey)
            {
                var prev = File.Exists(umuMarker) ? File.ReadAllText(umuMarker).Trim() : "none";
                Report($"umu identity changed ({prev} → {umuKey}) — clearing prefix, first launch rebuilds it (a few minutes)");
                if (Directory.Exists(prefixRoot))
                    Directory.Delete(prefixRoot, true);
                Directory.CreateDirectory(prefixRoot);
            }

            Report("Ensuring prefix (create + bootstrap) — first run can take a few minutes");
            new UmuCommandRunner(prefix, this.umuRunPath, WineLogPath, this.settingsService.Settings.WineDebugEnv).EnsurePrefix();
            File.WriteAllText(umuMarker, umuKey);
            Report("Prefix ready");

            var lang = Enum.TryParse<ClientLanguage>(settings.Language, out var parsedLang) ? parsedLang : ClientLanguage.English;
            service = new LoginService(this.settingsService.Storage, this.settingsService.Settings.ConfigPath,
                this.settingsService.GetCacheFolder("patches").FullName, prefix, lang, this.frontierUrl,
                this.settingsService.Settings.MaxDownloadSpeedKibps, this.settingsService.UidCache);
            Report($"Client language: {lang}");

            Report("Checking login gate ...");
            if (!await service.GateAvailableAsync())
            {
                Report("Login servers unreachable or maintenance in progress.");
                return;
            }

            if (!gameInstalled)
            {
                // Missing game folder: create it, but only one level — the immediate
                // parent must exist (e.g. ~/Games/final-fantasy-xiv is creatable when
                // ~/Games exists; a missing ~/Games fails with a clear message).
                var dirErr = LoginService.EnsureGameDir(gamePath.FullName);
                if (dirErr is not null)
                {
                    Report(dirErr);
                    return;
                }

                // Full fresh install from an empty game dir. Upstream fresh-install shape
                // (MainPage.cs CheckBootVersion → TryHandlePatchAsync(Repository.Boot)):
                // 1) boot patches from base (missing boot ver file reports base ver, no
                //    boot hashes), installed into boot/;
                // 2) login with forceBaseVersion=true — the base version report makes the
                //    server return the FULL game patch list (base → latest);
                // 3) install every game patch into the game dir; then launch with the
                //    login session, game exe exists now.
                Report($"Game is not installed at {gamePath.FullName} — the full game (boot + game patches, tens of GB) will be downloaded and installed first.");
                var cont = await ShowInstallConfirmAsync(gamePath.FullName);
                if (!cont)
                {
                    Report("Install cancelled — nothing downloaded.");
                    return;
                }

                Report("Install step 1: boot patch list from base ...");
                var bootPatches = await service.CheckBootPatchesAsync(gamePath);
                Report($"Boot patch list: {bootPatches.Length} entries");
                if (bootPatches.Length > 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => OpenProgressPopup("Installing boot patches"));
                    var okBoot = await service.InstallPatchesAsync(Repository.Boot, bootPatches, string.Empty, settings.GamePath, settings.PatchPath, Report);
                    await Dispatcher.UIThread.InvokeAsync(() => CloseProgressPopup());
                    if (!okBoot)
                    {
                        Report("Boot install stopped (cancelled from the progress popup, or failed).");
                        return;
                    }
                }

                Report($"Install step 2: login with base game version report (steam: {settings.SteamAccount}, free trial: {settings.FreeTrial})");
                var installResult = await service.LoginAsync(new LoginRequest { Username = settings.Username, Password = settings.Password, OneTimePassword = settings.OneTimePassword, SteamAccount = settings.SteamAccount, FreeTrial = settings.FreeTrial }, gamePath, forceBaseVersion: true);
                Report($"Install login state: {installResult.State}");
                if (installResult.State != XivGame.LoginState.NeedsPatchGame)
                {
                    Report($"Unexpected login state {installResult.State} during install (expected NeedsPatchGame).");
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() => OpenProgressPopup("Installing game patches"));
                var okAll = await service.InstallPatchesAsync(Repository.Ffxiv, installResult.PendingPatches, installResult.UniqueId ?? string.Empty, settings.GamePath, settings.PatchPath, Report);
                await Dispatcher.UIThread.InvokeAsync(() => CloseProgressPopup());
                if (!okAll)
                {
                    Report("Game install stopped (cancelled from the progress popup, or failed) — game not installed.");
                    return;
                }

                Report($"Install complete: game ver {Repository.Ffxiv.GetVer(gamePath)}");
                if (noLaunch)
                {
                    Report("Install complete — launch skipped (Patch without launching)");
                    return;
                }
                var launchSteam1 = settings.SteamAccount && !settings.IgnoreSteam;   // XIVLauncher.Core IsIgnoringSteam
                if (withDalamud)
                {
                    if (!await WaitForDalamudAsync()) return;
                    LaunchWithDansharing(prefix, service, gameExe, gamePath, installResult, lang, launchSteam1, noPlugins, noThirdPlugins);
                }
                else
                    LaunchPlain(prefix, service, gamePath, installResult, lang, launchSteam1);
                return;
            }

            Report($"Logging in as {settings.Username} (steam: {settings.SteamAccount}, free trial: {settings.FreeTrial})");
            var result = await service.LoginAsync(new LoginRequest { Username = settings.Username, Password = settings.Password, OneTimePassword = settings.OneTimePassword, SteamAccount = settings.SteamAccount, FreeTrial = settings.FreeTrial }, gamePath,
                useCache: settings.UseUidCache);
            Report($"Login state: {result.State}");

            if (result.State == XivGame.LoginState.Ok)
            {
                // XIVLauncher.Core PersistAccount: merge login into the account store after
                // a successful login; the password is encrypted and stored only when the
                // Save-password checkbox was on (flag captured on the UI thread). The OTP
                // is not persisted.
                PersistAccount(settings.Username, settings.Password, !string.IsNullOrEmpty(settings.OneTimePassword),
                    settings.SteamAccount, settings.FreeTrial, savePassword, saveAsCurrent: true);
            }

            switch (result.State)
            {
                case XivGame.LoginState.NoService:
                    Report("No service on this account (no subscription for this region).");
                    return;
                case XivGame.LoginState.NoTerms:
                    Report("Terms not accepted on this account.");
                    return;
                case XivGame.LoginState.NeedsPatchBoot:
                    Report("Boot conflict — boot files were modified outside the launcher; a reinstall is required.");
                    return;
                case XivGame.LoginState.NeedsPatchGame:
                    Report($"Server returned {result.PendingPatches.Length} game patch(es)");
                    if (result.PendingPatches.Length > 0)
                        await Dispatcher.UIThread.InvokeAsync(() => OpenProgressPopup("Installing game patches"));
                    var patchOk = await service.PatchAsync(result, settings.GamePath, Report);
                    await Dispatcher.UIThread.InvokeAsync(() => CloseProgressPopup());
                    if (!patchOk)
                    {
                        Report("Patching stopped (cancelled from the progress popup, or failed) — game not launched.");
                        return;
                    }
                    result.State = XivGame.LoginState.Ok;
                    break;
            }

            if (noLaunch)
            {
                Report("Login + patch complete — launch skipped (Patch without launching)");
                return;
            }

            var launchSteam = settings.SteamAccount && !settings.IgnoreSteam;   // XIVLauncher.Core IsIgnoringSteam
            if (withDalamud)
            {
                if (!await WaitForDalamudAsync()) return;
                LaunchWithDansharing(prefix, service, gameExe, gamePath, result, lang, launchSteam, noPlugins, noThirdPlugins);
            }
            else
                LaunchPlain(prefix, service, gamePath, result, lang, launchSteam);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Login pipeline failed");
            Dispatcher.UIThread.Post(() => StatusLog.Text += $"Launch failed: {exception.Message}\n");
        }
        finally
        {
            service?.Dispose();
            Dispatcher.UIThread.Post(() =>
            {
                LoginButton.IsEnabled = true;
            });
        }
    }

    /// <summary>Plain launch: LaunchGame with the real login session (uid, region,
    /// expansion) and encrypted args, UserPath pointing at ~/.xivonlinux/ffxivConfig
    /// in Wine-style prefix path, no injector.</summary>
    private void LaunchPlain(UmuPrefix prefix, LoginService service, DirectoryInfo gamePath, XivGame.LoginResult result, ClientLanguage lang, bool isSteam)
    {
        var gameSettings = this.settingsService.Settings;
        var userPathWine = prefix.UnixToWinePath(service.GameConfigFolder().FullName);
        var extraArgs = $" UserPath={userPathWine}";
        // AdditionalArgs are KEY=VALUE pairs passed as environment variables to the
        // game process (validated on save, so ParseArgs is safe here).
        // Troubleshooting-hack env pairs first, AdditionalArgs pairs after: the user can
        // override a hack through AdditionalArgs, as XIVLauncher.Core intends.
        var extraEnv = gameSettings.HackEnvPairs()
            .Concat(LaunchSettings.ParseArgs(gameSettings.AdditionalArgs)
                .Select(p => new KeyValuePair<string, string>(p.key, p.value)))
            .ToList();
        Report($"extraEnv pairs: {string.Join(" ", extraEnv.Select(p => $"{p.Key}={p.Value.Replace("\n", "\\n")}"))} (hacks + AdditionalArgs='{gameSettings.AdditionalArgs}')");
        var dpi = Enum.TryParse<DpiAwareness>(gameSettings.DpiAwareness, out var parsedDpi) ? parsedDpi : DpiAwareness.Unaware;
        Report("LaunchGame (login session, encrypted args)");
        var gameRunner = new LinuxGameRunner(prefix, this.umuRunPath, WineLogPath, this.settingsService.Settings.WineDebugEnv, extraEnv);
        var game = new XivGame(new LinuxSteamStub(), new NullUniqueIdCache(), this.frontierUrl, "en");
        var proc = game.LaunchGame(gameRunner, result.UniqueId ?? string.Empty, result.OauthLogin!.Region,
            result.OauthLogin.MaxExpansion, isSteam, extraArgs, "",
            gamePath, lang, true, dpi);

        if (proc is not null)
        {
            Interlocked.Increment(ref activeLaunches);
            Report($"Launched ffxiv_dx11.exe via umu (unix pid {proc.Id})");
            _ = Task.Run(() =>
            {
                proc.WaitForExit();
                Interlocked.Decrement(ref activeLaunches);
                Report("game process exited");
            });
        }
        else
            Report("Launched ffxiv_dx11.exe via umu (pid not reported)");
    }

    /// <summary>Repair game files (XIVLauncher.Core LoginAction.Repair shape): login with
    /// the BASE game version report so the server returns the FULL patch list
    /// (base → latest), then PatchVerifier hashes every game file against every
    /// patch source. Broken files are re-downloaded from the CDN and reinstalled
    /// in place; files in no list go to game/repair_recycler. No prefix bootstrap,
    /// no launch — repair is a file-integrity operation only.</summary>
    private async Task RepairPipelineAsync()
    {
        var settings = this.settingsService.Settings;
        var gamePath = new DirectoryInfo(settings.GamePath);
        var gameExe = new FileInfo(Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe"));
        if (!File.Exists(gameExe.FullName))
        {
            Report($"Repair skipped — game is not installed at {gamePath.FullName}");
            return;
        }

        // UmuPrefix is pure data; LoginAsync needs it for the session object, so repair
        // can log in without bootstrapping the prefix at all.
        var prefixRoot = string.IsNullOrEmpty(settings.PrefixPath)
            ? this.settingsService.Root.FullName
            : settings.PrefixPath;
        var prefix = new UmuPrefix(
            new DirectoryInfo(Path.Combine(prefixRoot, "pfx")),
            prefixRoot, gamePath.FullName, "umu-steam",
            settings.ProtonPath ?? string.Empty, string.Empty, string.Empty);

        var lang = Enum.TryParse<ClientLanguage>(settings.Language, out var parsedLang) ? parsedLang : ClientLanguage.English;
        var service = new LoginService(this.settingsService.Storage, settings.ConfigPath,
            this.settingsService.GetCacheFolder("patches").FullName, prefix, lang, this.frontierUrl,
            settings.MaxDownloadSpeedKibps, this.settingsService.UidCache);

        Report("Repair: checking login gate ...");
        if (!await service.GateAvailableAsync())
        {
            Report("Login servers unreachable or maintenance in progress.");
            return;
        }

        Report($"Repair login as {settings.Username} (base version report, steam: {settings.SteamAccount}, free trial: {settings.FreeTrial})");
        var result = await service.LoginAsync(new LoginRequest { Username = settings.Username, Password = settings.Password, OneTimePassword = settings.OneTimePassword, SteamAccount = settings.SteamAccount, FreeTrial = settings.FreeTrial }, gamePath, forceBaseVersion: true);
        Report($"Repair login state: {result.State}");

        switch (result.State)
        {
            case XivGame.LoginState.NeedsPatchGame:
                break;
            case XivGame.LoginState.NoService:
                Report("No service on this account (no subscription for this region).");
                return;
            case XivGame.LoginState.NoTerms:
                Report("Terms not accepted on this account.");
                return;
            case XivGame.LoginState.NeedsPatchBoot:
                Report("Boot conflict — boot files were modified outside the launcher; a reinstall is required.");
                return;
            default:
                Report($"Repair login state was {result.State}, not NeedsPatchGame — nothing to repair.");
                return;
        }

        Report($"Repair: verifying game files against {result.PendingPatches.Length} patch set(s) ...");
        await Dispatcher.UIThread.InvokeAsync(() => OpenProgressPopup("Repairing game files", false, true));
        var outcome = await service.RepairAsync(result, settings.GamePath, Report);
        await Dispatcher.UIThread.InvokeAsync(() => CloseProgressPopup());

        if (outcome.repaired < 0)
            return; // RepairAsync reported the Error/cancel state already

        Report($"Repair complete: {outcome.repaired} file(s) repaired, {outcome.moved} file(s) moved to repair_recycler");
        ShowRepairResults(outcome.repaired, outcome.moved);
    }

    /// <summary>Results popup shown when a repair run finishes (XIVLauncher.Core reports
    /// repaired/moved counts in its UI). Non-blocking modal on the main window; OK,
    /// Enter, or the X button all close it.</summary>
    private void ShowRepairResults(int repaired, int moved)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var headline = new TextBlock
            {
                Text = repaired == 0 && moved == 0
                    ? "All game files valid — nothing to repair."
                    : $"Repair finished: {repaired} file(s) repaired, {moved} file(s) removed.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 16, 16, 8),
            };
            var detail = new TextBlock
            {
                Text = $"Files repaired: {repaired}\nFiles removed (moved to game/repair_recycler): {moved}\nAll game files were verified against the server patch list.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(16, 0, 16, 8),
            };
            var okButton = new Button { Content = "OK", Padding = new Thickness(16, 6), Margin = new Thickness(0, 8, 0, 16), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, IsDefault = true };

            var panel = new StackPanel();
            panel.Children.Add(headline);
            panel.Children.Add(detail);
            panel.Children.Add(okButton);

            var window = new Window
            {
                Title = "Repair Results",
                Content = panel,
                Width = 420,
                Height = 180,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };

            okButton.Click += (_, __) => window.Close();
            window.Show(this);
        });
    }

    /// <summary>Blocks the login pipeline until the background Dalamud updater reaches
    /// Done (injector + runtime downloaded/verified), reporting progress. On failure
    /// (NoIntegrity after 10 tries, or 5 min timeout) the launch is aborted — the user
    /// can retry, matching upstream's "hold for update" gate.</summary>
    private async Task<bool> WaitForDalamudAsync()
    {
        Report("Checking Dalamud (background updater)...");
        return await WaitForDalamudCoreAsync(DateTime.UtcNow.AddMinutes(5), "", false);
    }

    private async Task<bool> WaitForDalamudCoreAsync(DateTime deadline, string reportedStep, bool restarted)
    {
        while (dalamudUpdater.State != DalamudUpdater.DownloadState.Done)
        {
            if (dalamudUpdater.State == DalamudUpdater.DownloadState.NoIntegrity)
            {
                Report($"Dalamud update failed: {dalamudUpdater.EnsurementException?.Message ?? "no integrity"}");
                return false;
            }
            if (DateTime.UtcNow > deadline)
            {
                Report("Dalamud update timed out after 5 minutes.");
                return false;
            }
            var step = dalamudUpdater.State.ToString();
            if (step != reportedStep)
            {
                reportedStep = step;
                Report($"Dalamud updater state: {step}");
            }
            await Task.Delay(1000);
        }

        if (!dalamudUpdater.Runner.Exists)
        {
            if (!restarted)
            {
                Report($"Dalamud injector missing at {dalamudUpdater.Runner.FullName}; restarting background updater");
                RunDalamudUpdaterForBranch();
                return await WaitForDalamudCoreAsync(deadline, reportedStep, restarted: true);
            }
            Report($"Dalamud injector missing at {dalamudUpdater.Runner.FullName}");
            return false;
        }
        Report($"Dalamud ready: injector {dalamudUpdater.Runner.FullName}");
        return true;
    }

    /// <summary>In-prefix injector launch with the real login: the injector gets the
    /// encrypted game args (session, region, expansion, ver, UserPath); Run returns
    /// as soon as the injector's JSON arrives (game is up); game exit is watched
    /// off the UI thread.</summary>
    private void LaunchWithDansharing(UmuPrefix prefix, LoginService service, FileInfo gameExe, DirectoryInfo gamePath, XivGame.LoginResult result, ClientLanguage lang, bool isSteam, bool noPlugins, bool noThirdPlugins)
    {
        var injectorExe = this.dalamudUpdater.Runner;
        if (this.settingsService.Settings.ManualInjectEnabled)
        {
            // Manual override: a local build folder containing Dalamud.Injector.exe
            // (validated on settings save), used instead of the updater's download.
            var local = Path.Combine(this.settingsService.Settings.ManualInjectPath, "Dalamud.Injector.exe");
            if (!File.Exists(local))
            {
                Report($"FAIL: manual injector missing at {local}");
                return;
            }
            injectorExe = new FileInfo(local);
            Report($"Manual injector: {local}");
        }
        if (!File.Exists(injectorExe.FullName))
        {
            Report($"FAIL: injector exe missing at {injectorExe.FullName}");
            return;
        }
        var storage = this.settingsService.Root.FullName;
        var logs = Path.Combine(storage, "logs");
        Directory.CreateDirectory(logs);

        var startInfo = new DalamudStartInfo
        {
            WorkingDirectory = injectorExe.Directory!.FullName,
            ConfigurationPath = Path.Combine(storage, "dalamudConfig.json"),
            LoggingPath = logs,
            PluginDirectory = Path.Combine(storage, "installedPlugins"),
            AssetDirectory = this.dalamudUpdater.AssetDirectory!.FullName,
            Language = lang,
            DelayInitializeMs = this.settingsService.Settings.LoadDelay,
        };

        // Wine .NET runtime for the in-prefix injector (M1-verified env shape; Wine-style paths).
        var runtimeWine = prefix.UnixToWinePath(this.dalamudUpdater.Runtime.FullName);
        var envPairs = new List<(string, string)>
        {
            ("DOTNET_ROOT", runtimeWine),
            ("DOTNET_ROOT_X64", runtimeWine),
            ("WINEDOTNET_ROOT", runtimeWine),
            ("DALAMUD_RUNTIME", runtimeWine),
        };
        if (isSteam)
            envPairs.Add(("IS_FFXIV_LAUNCH_FROM_STEAM", "1"));
        var env = new UmuInPrefixEnvironment(prefix).Build(envPairs.ToArray());

        var gameArgs = service.BuildEncryptedGameArgs(result, gamePath, service.GameConfigFolder(), isSteam, this.settingsService.Settings.AdditionalArgs);

        Interlocked.Increment(ref activeLaunches);
        var loadMethod = this.settingsService.Settings.LoadMethod switch
        {
            "EntryPoint" => DalamudLoadMethod.EntryPoint,
            "ACLonly" => DalamudLoadMethod.ACLonly,
            _ => DalamudLoadMethod.DllInject,
        };
        Report($"In-prefix injector: SE login session + {loadMethod} + delay {startInfo.DelayInitializeMs}ms");
        // AdditionalArgs KEY=VALUE pairs → env vars for the injector, inherited by the
        // game it spawns (validated on save, so ParseArgs is safe here). Hack pairs first,
        // AdditionalArgs pairs after: the user can override a hack through AdditionalArgs.
        var extraEnv = this.settingsService.Settings.HackEnvPairs()
            .Concat(LaunchSettings.ParseArgs(this.settingsService.Settings.AdditionalArgs)
                .Select(p => new KeyValuePair<string, string>(p.key, p.value)))
            .ToList();
        Report($"injector extraEnv pairs: {string.Join(" ", extraEnv.Select(p => $"{p.Key}={p.Value.Replace("\n", "\\n")}"))} (hacks + AdditionalArgs='{this.settingsService.Settings.AdditionalArgs}')");
        var runner = new LinuxDalamudRunner(prefix, this.umuRunPath, WineLogPath, this.settingsService.Settings.WineDebugEnv, extraEnv);
        var proc = runner.Run(injectorExe, false, noPlugins, noThirdPlugins, gameExe, gameArgs, "", env,
            loadMethod, startInfo);

        if (proc is null)
        {
            Interlocked.Decrement(ref activeLaunches);
            Report("Injector finished; no game process mapped");
            foreach (var line in runner.CapturedUmuOutput().Split('\n').Take(10))
                if (line.Trim().Length > 0) Report($"injector stdout: {line}");
            return;
        }

        Report($"Game unix pid {proc.Id} (name {proc.ProcessName})");
        _ = Task.Run(() =>
        {
            proc.WaitForExit();
            Interlocked.Decrement(ref activeLaunches);
            Report("Game exited");
            foreach (var line in runner.CapturedUmuOutput().Split('\n').Take(10))
                if (line.Trim().Length > 0) Report($"injector stdout: {line}");
        });
    }
}
