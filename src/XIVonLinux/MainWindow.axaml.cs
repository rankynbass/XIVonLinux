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
        var s = this.settingsService.Settings;
        this.dalamudUpdater.Run(
            string.IsNullOrEmpty(s.BranchTrack) ? null : s.BranchTrack,
            string.IsNullOrEmpty(s.BranchKey) ? null : s.BranchKey);
        this.lastBranchTrack = s.BranchTrack;
    }

    /// <summary>umu-run to invoke, resolved from settings: the managed latest/previous
    /// tag picked in the Proton tab, the custom picked folder's umu-run, falling back
    /// to the newest managed install, then the system binary.</summary>
    private string ResolveUmuRunPath()
    {
        var s = this.settingsService.Settings;
        if (s.ManagedUmu)
        {
            var p = this.umuManager.PathForTag(s.UmuTag);
            if (p is not null)
                return p;
        }
        else if (!string.IsNullOrEmpty(s.UmuPath))
        {
            var p = Path.Combine(s.UmuPath, "umu-run");
            if (File.Exists(p))
                return p;
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
            var config = await XIVonLinux.Net.LauncherClientConfig.GetAsync(NewsHttp);
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
        var s = this.settingsService.Settings;
        UsernameBox.Text = s.Username;
        SteamAccountCheck.IsChecked = s.SteamAccount;
        FreeTrialCheck.IsChecked = s.FreeTrial;

        // Pre-select the saved current account (XIVLauncher.Core startup shape).
        var saved = this.accounts.CurrentAccount;
        if (saved is not null) SwitchAccountFields(saved, saveAsCurrent: false);

        // Accounts menu: ContextMenu shown at click time. MenuFlyout in Avalonia 12.1.4
        // builds its popup before Opening fires, so items added in the Opening handler
        // never render — the documented dynamic-item pattern is ContextMenu.Opening.
        AccountsButton.Click += (_, _) =>
        {
            var cm = new ContextMenu();
            BuildAccountsMenu(cm);   // Open() does not raise Opening in Avalonia 12.1.4, so build first
            cm.Open(AccountsButton);
        };

        this.dalamudUpdater = new DalamudUpdater(NewsHttp,
            this.settingsService.GetFolder("dalamud"),
            this.settingsService.GetFolder("runtime"),
            this.settingsService.GetFolder("dalamudAssets"),
            null, null)
        {
            Overlay = new SilentDalamudOverlay(),
        };
        RunDalamudUpdaterForBranch();   // fire-and-forget background check/download for the saved branch

        StartNewsLoad();

        // Fake download test mode: opens the popup with 40 simulated patches (no
        // network) so the 5-bar layout can be exercised without a real download.
        // Deferred to Opened: the popup Show()s against this window as owner, which
        // requires the main window to be shown first.
        if (Environment.GetEnvironmentVariable("XIVON_FAKE_DOWNLOADS") == "1")
            this.Opened += (_, __) => OpenFakeProgressPopup();
    }

    // ---------- settings modal ----------

    /// <summary>Modal settings window over the main page; Save persists and closes,
    /// then the main page is refocused.</summary>
    private async void OnSettings(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(this.settingsService);
        await dlg.ShowDialog(this);
        var s = this.settingsService.Settings;
        if (s.BranchTrack != this.lastBranchTrack)
            RunDalamudUpdaterForBranch();   // branch changed: re-fetch in the background
        this.Focus();
    }

    // ---------- log box visibility ----------

    /// <summary>Show/hide the log box (it replaces the news panel; the login side is
    /// never hidden). ShowLog(true) is called on Login press so pipeline errors are
    /// visible in the log box as they happen.</summary>
    private void ShowLog(bool show)
    {
        StatusLog.IsVisible = show;
        NewsPanel.IsVisible = !show;
        LogToggleButton.Content = show ? "Hide log" : "Show log";
    }

    private void OnToggleLog(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowLog(!StatusLog.IsVisible);
    }

    // ---------- accounts ----------

    /// <summary>Rebuilds the Accounts menu on open: saved accounts (name + Steam/OTP/Trial
    /// tags, XIVLauncher.Core AccountSwitcher shape), then Add/Update, Remove, Remove All.</summary>
    private int BuildAccountsMenu(ContextMenu cm)
    {
        cm.Items.Clear();

        if (this.accounts.Accounts.Count == 0)
            cm.Items.Add(new MenuItem { Header = "No saved accounts", IsEnabled = false });

        foreach (var account in this.accounts.Accounts)
        {
            var name = account.UserName;
            if (account.UseSteamServiceAccount) name += " (Steam)";
            if (account.UseOtp) name += " (OTP)";
            if (account.IsFreeTrial) name += " (Trial)";

            var item = new MenuItem { Header = name };
            var captured = account;
            item.Click += (_, _) => SelectAccount(captured);
            cm.Items.Add(item);
        }

        cm.Items.Add(new Separator());

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

        cm.Items.Add(add);
        cm.Items.Add(remove);
        cm.Items.Add(removeAll);
        return cm.Items.Count;
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

        var s = this.settingsService.Settings;
        s.Username = account.UserName;
        s.SteamAccount = account.UseSteamServiceAccount;
        s.FreeTrial = account.IsFreeTrial;
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

    private async void OnLogin(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
        var s = this.settingsService.Settings;
        s.Username = UsernameBox.Text;
        s.Password = PasswordBox.Text;
        s.SteamAccount = SteamAccountCheck.IsChecked == true;
        s.FreeTrial = FreeTrialCheck.IsChecked == true;

        // OTP is a true/false setting; when enabled, a popup prompts for the 6-digit code
        // and the login proceeds only when its Login button is pressed (Cancel aborts).
        var isOtp = OtpCheck.IsChecked == true;
        if (isOtp)
        {
            var code = await ShowOtpPopupAsync();
            if (code is null)
            {
                Report("Login cancelled (OTP popup)");
                return false;
            }
            s.OneTimePassword = code;
        }
        else
            s.OneTimePassword = "";

        this.settingsService.Save();

        PersistAccount(s.Username, s.Password, isOtp, s.SteamAccount, s.FreeTrial,
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
    private void OnTroubleshoot(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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
        menu.Items.Add(noD);
        menu.Items.Add(noP);
        menu.Items.Add(noT);
        menu.Items.Add(noL);
        menu.Open((Control)sender!);
    }

    /// <summary>Log to Serilog (console/file) and mirror the line into the UI status box.</summary>
    // ---------- news frame ----------

    private const int BANNER_TIME_MS = 8000;
    private static readonly HttpClient NewsHttp = new();

    private List<Banner> bannerList = new();
    private Bitmap[] bannerBitmaps = [];
    private int currentBanner;
    private LoginService? newsService;
    private DispatcherTimer? bannerTimer;

    /// Fetches headlines + banners via the launcher frontier API, loads banner images,
    /// then shows them on the UI thread (NewsFrame shape from XIVLauncher.Core).
    private void StartNewsLoad()
    {
        newsService = new LoginService(settingsService.Storage, settingsService.Settings.ConfigPath, null!, ClientLanguage.English, this.frontierUrl);
        var svc = newsService;

        _ = Task.Run(async () =>
        {
            try
            {
                var (headlines, banners) = await svc.GetNewsAsync();

                var bitmaps = new List<Bitmap>();
                foreach (var b in banners)
                {
                    var bytes = await NewsHttp.GetByteArrayAsync(b.LsbBanner);
                    bitmaps.Add(await Dispatcher.UIThread.InvokeAsync(() => new Bitmap(new MemoryStream(bytes))));
                }

                Dispatcher.UIThread.Post(() => ShowNews(headlines, banners, bitmaps));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "News load failed");
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
        foreach (var n in headlines.News) AddNewsEntry(n.Title, n.Url);

        if (headlines.Topics.Length > 0)
        {
            AddNewsCaption("Topics");
            foreach (var t in headlines.Topics) AddNewsEntry(t.Title, t.Url);
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
        var tb = new TextBlock { Text = title, FontSize = 11, TextWrapping = TextWrapping.Wrap, Cursor = new Cursor(StandardCursorType.Hand) };
        var accent = new SolidColorBrush((Color)Avalonia.Application.Current.FindResource("SystemAccentColor"));
        tb.PointerEntered += (_, _) => tb.Foreground = accent;
        tb.PointerExited += (_, _) => tb.ClearValue(TextBlock.ForegroundProperty);
        tb.PointerPressed += (_, _) => OpenUrl(url);
        NewsList.Children.Add(tb);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open {Url}", url);
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
        var v = Environment.GetEnvironmentVariable(var);
        return v is "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
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
                Title = "Killswitch",
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
            codeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) TryLogin(); };

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
    // A non-modal popup showing up to 5 live download bars. Rows are rebuilt each
    // second from LoginService.ProgressSnapshot: in-progress slots show bytes/speed
    // bars, queued patches show as queued; finished downloads are excluded, so bars
    // disappear one by one as fewer pieces remain.

    private Window? progressWindow;
    private DispatcherTimer? progressTimer;
    private StackPanel? progressPanel;
    private TextBlock? progressSummary;

    // Fake download test mode (XIVON_FAKE_DOWNLOADS=1): 40 simulated patches, 4 slots,
    // no network — exercises the 5-bar popup layout. Slot s downloads patches at
    // (2+s) MB/s; each finished patch verifies for 2 ticks.
    private sealed class FakePatch
    {
        public string VersionId = "";
        public long Length;
        public long Downloaded;
        public int VerifyTicks;
        public bool Done;
    }

    private const int FakeCount = 40;
    private List<FakePatch>? fakePatches;
    private int[] fakeSlotIdx = new int[4];

    private void OpenProgressPopup(string title)
    {
        progressWindow = new Window
        {
            Title = title,
            Width = 560,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        progressSummary = new TextBlock { Margin = new Thickness(16, 14, 16, 8), Text = "Starting downloads ..." };
        progressPanel = new StackPanel { Margin = new Thickness(16, 0, 16, 16) };

        var outer = new StackPanel();
        outer.Children.Add(progressSummary);
        outer.Children.Add(progressPanel);
        progressWindow.Content = outer;

        // Cancel pauses (cancels) all downloads, then closes. Closing the window with
        // the X button does the same via the Closing event.
        var cancelRow = new DockPanel { LastChildFill = false, Margin = new Thickness(16, 4, 16, 14) };
        var cancelButton = new Button { Content = "Cancel", Padding = new Thickness(16, 6) };
        DockPanel.SetDock(cancelButton, Dock.Right);
        cancelRow.Children.Add(cancelButton);
        outer.Children.Add(cancelRow);

        cancelButton.Click += (_, __) =>
        {
            LoginService.CancelCurrentDownloads();
            Dispatcher.UIThread.Post(() => progressWindow?.Close());
        };

        progressWindow.Closing += (_, __) => LoginService.CancelCurrentDownloads();

        progressWindow.Show(this);

        progressTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        progressTimer.Tick += (_, __) => UpdateProgressPopup();
        progressTimer.Start();
        UpdateProgressPopup();
    }

    private void UpdateProgressPopup()
    {
        if (progressPanel is null || progressSummary is null)
            return;

        List<LoginService.DownloadSlotInfo> rows;
        int done, totalFiles, queued;
        double speed;
        long totalBytes, downloadedBytes;

        var patcher = LoginService.CurrentPatcher;
        if (fakePatches is not null)
        {
            AdvanceFakeProgress();
            rows = FakeRows();
            done = fakePatches.Count(p => p.Done);
            totalFiles = FakeCount;
            queued = FakeCount - done - rows.Count;
            speed = rows.Sum(r => r.SpeedBps) / 1024 / 1024;
            totalBytes = fakePatches.Sum(p => p.Length);
            downloadedBytes = fakePatches.Sum(p => p.Downloaded);
        }
        else
        {
            rows = LoginService.ProgressSnapshot(patcher);
            done = patcher?.Downloads.Count(d => d.State == PatchState.Finished) ?? 0;
            totalFiles = patcher?.Downloads.Count ?? 0;
            queued = rows.Count(r => r.State == "queued");
            speed = rows.Sum(r => r.SpeedBps) / 1024 / 1024;
            totalBytes = patcher is null ? 0 : patcher.Downloads.Sum(d => d.Patch.Length);
            downloadedBytes = (patcher?.Downloads.Where(d => d.State == PatchState.Finished).Sum(d => d.Patch.Length) ?? 0)
                              + rows.Where(r => r.State != "queued").Sum(r => r.Downloaded);
        }

        // All patches done — nothing left to show; close the popup (the login
        // pipeline also closes it on install completion; double close is safe).
        if (totalFiles > 0 && done == totalFiles)
        {
            Log.Information("All {Total} downloads complete — closing progress popup", totalFiles);
            CloseProgressPopup();
            return;
        }

        // File bars: min 0, max 4. 1-3 downloading files show that many bars, 4+ show 4.
        var activeBars = rows.Where(r => r.State != "queued").Take(4).ToList();

        progressSummary.Text = $"{done}/{totalFiles} downloaded · {queued} queued · {speed:0.0} MB/s";

        if (fakePatches is not null)
            Log.Information("Fake tick: bars={Bars} done={Done} queued={Queued} bytes={Bytes}/{Total}",
                activeBars.Count, done, queued, downloadedBytes, totalBytes);

        progressPanel.Children.Clear();

        // Overall bar (longest): spans every patch, finished included.
        progressPanel.Children.Add(new TextBlock
        {
            Text = "Overall",
            Margin = new Thickness(0, 2, 0, 0),
        });
        progressPanel.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = Math.Max(1, totalBytes),
            Value = downloadedBytes,
            Height = 14,
            Width = 528,
            Margin = new Thickness(0, 2, 0, 8),
        });

        foreach (var r in activeBars)
        {
            var label = r.State == "verifying"
                ? $"{r.VersionId} — verifying SHA1 {FmtBytes(r.Downloaded)}/{FmtBytes(r.Total)}"
                : $"{r.VersionId} — {FmtBytes(r.Downloaded)}/{FmtBytes(r.Total)} at {r.SpeedBps / 1024 / 1024:0.0} MB/s";
            progressPanel.Children.Add(new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            progressPanel.Children.Add(new ProgressBar
            {
                Minimum = 0,
                Maximum = Math.Max(1, r.Total),
                Value = r.Downloaded,
                Height = 8,
                Width = 400,
                Margin = new Thickness(0, 2, 0, 2),
            });
        }
    }

    private void CloseProgressPopup()
    {
        progressTimer?.Stop();
        progressTimer = null;
        progressWindow?.Close();
        progressWindow = null;
        progressPanel = null;
        progressSummary = null;
    }

    // ---------- fake download test (XIVON_FAKE_DOWNLOADS=1, no network) ----------

    private static double SlotSpeed(int slot) => (2.0 + slot) * 5 * 1024 * 1024;

    /// <summary>Advance each of the 4 fake slots: the patch at fakeSlotIdx[s] gains
    /// SlotSpeed(s) bytes per tick; a full patch verifies for 2 ticks, then the slot
    /// moves to the next patch in its interleaved queue (s, s+4, s+8, ...).</summary>
    private void AdvanceFakeProgress()
    {
        for (var s = 0; s < 4; s++)
        {
            var idx = fakeSlotIdx[s];
            if (idx >= FakeCount)
                continue;
            var p = fakePatches![idx];
            if (p.VerifyTicks > 0)
            {
                p.VerifyTicks--;
                if (p.VerifyTicks == 0)
                {
                    p.Done = true;
                    fakeSlotIdx[s] += 4;
                }
                continue;
            }
            p.Downloaded = Math.Min(p.Length, p.Downloaded + (long)SlotSpeed(s));
            if (p.Downloaded >= p.Length)
                p.VerifyTicks = 2;
        }
    }

    private List<LoginService.DownloadSlotInfo> FakeRows()
    {
        var rows = new List<LoginService.DownloadSlotInfo>();
        for (var s = 0; s < 4; s++)
        {
            var idx = fakeSlotIdx[s];
            if (idx >= FakeCount)
                continue;
            var p = fakePatches![idx];
            rows.Add(new LoginService.DownloadSlotInfo
            {
                VersionId = p.VersionId,
                Total = p.Length,
                Downloaded = p.Downloaded,
                SpeedBps = p.VerifyTicks > 0 ? 0 : SlotSpeed(s),
                State = p.VerifyTicks > 0 ? "verifying" : "downloading",
            });
        }
        return rows;
    }

    /// <summary>Open the popup with 40 fake patches (20-119 MB each) and start the
    /// 1s tick; progress is simulated entirely in memory, no aria2, no network.</summary>
    private void OpenFakeProgressPopup()
    {
        fakePatches = new List<FakePatch>();
        for (var i = 0; i < FakeCount; i++)
            fakePatches.Add(new FakePatch { VersionId = $"fake-{i:D2}", Length = (20 + i * 37 % 100) * 1024 * 1024 });
        fakeSlotIdx = new int[] { 0, 1, 2, 3 };
        OpenProgressPopup("Installing game patches (fake test)");
        Log.Information("Fake download test mode: {Count} fake patches, 4 slots", FakeCount);
    }

    private static string FmtBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / 1024.0 / 1024 / 1024:0.00} GB";
        if (bytes >= 1L << 20) return $"{bytes / 1024.0 / 1024:0.0} MB";
        if (bytes >= 1L << 10) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
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
            var s = this.settingsService.Settings;
            // Killswitch flags bitmask (XIVLauncher LeaseFeatureFlags): bit 0 = GlobalDisableDalamud,
            // launched without the in-game addon on patch days, regardless of the user setting.
            if ((flags & 1) != 0 && withDalamud)
            {
                Report("GlobalDisableDalamud flag set — launching without Dalamud");
                withDalamud = false;
            }

            Report($"Pipeline start (login, dalamud: {withDalamud})");

            var prefixRoot = string.IsNullOrEmpty(s.PrefixPath)
                ? this.settingsService.Root.FullName
                : s.PrefixPath;
            Directory.CreateDirectory(prefixRoot);

            var gamePath = new DirectoryInfo(s.GamePath);
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

            var proton = s.ProtonPath;
            if (string.IsNullOrEmpty(proton) || !Directory.Exists(proton))
            {
                Report($"Proton path not found: {proton} — pick a Proton (default GE-Proton 11-6) under compatibilitytools.d; umu auto-select would upgrade a GE prefix, so launching is refused.");
                return;
            }
            Report($"Proton: {Path.GetFileName(proton.TrimEnd('/', '\\'))}");

            // WINEDLLOVERRIDES: exact DXVK preset per the Enable DXVK setting, with the
            // user's extra overrides appended (joined with ';' when non-empty).
            var dxvkPreset = s.EnableDxvk
                ? "msquic=,mscoree=n,b;d3d9,d3d11,d3d10core,dxgi=n,b"
                : "msquic=,mscoree=n,b;d3d9,d3d11,d3d10core,dxgi=b";
            var winedllOverrides = string.IsNullOrEmpty(s.ExtraWineDllOverrides)
                ? dxvkPreset
                : dxvkPreset + ";" + s.ExtraWineDllOverrides;

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

            var lang = Enum.TryParse<ClientLanguage>(s.Language, out var l) ? l : ClientLanguage.English;
            service = new LoginService(this.settingsService.Storage, this.settingsService.Settings.ConfigPath, prefix, lang, this.frontierUrl);
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
                    var okBoot = await service.InstallPatchesAsync(Repository.Boot, bootPatches, string.Empty, s.GamePath, s.PatchPath, Report);
                    await Dispatcher.UIThread.InvokeAsync(() => CloseProgressPopup());
                    if (!okBoot)
                    {
                        Report("Boot install stopped (cancelled from the progress popup, or failed).");
                        return;
                    }
                }

                Report($"Install step 2: login with base game version report (steam: {s.SteamAccount}, free trial: {s.FreeTrial})");
                var installResult = await service.LoginAsync(new LoginRequest { Username = s.Username, Password = s.Password, OneTimePassword = s.OneTimePassword, SteamAccount = s.SteamAccount, FreeTrial = s.FreeTrial }, gamePath, forceBaseVersion: true);
                Report($"Install login state: {installResult.State}");
                if (installResult.State != XivGame.LoginState.NeedsPatchGame)
                {
                    Report($"Unexpected login state {installResult.State} during install (expected NeedsPatchGame).");
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() => OpenProgressPopup("Installing game patches"));
                var okAll = await service.InstallPatchesAsync(Repository.Ffxiv, installResult.PendingPatches, installResult.UniqueId ?? string.Empty, s.GamePath, s.PatchPath, Report);
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
                var launchSteam1 = s.SteamAccount && !s.IgnoreSteam;   // XIVLauncher.Core IsIgnoringSteam
                if (withDalamud)
                {
                    if (!await WaitForDalamudAsync()) return;
                    LaunchWithDansharing(prefix, service, gameExe, gamePath, installResult, lang, launchSteam1, noPlugins, noThirdPlugins);
                }
                else
                    LaunchPlain(prefix, service, gamePath, installResult, lang, launchSteam1);
                return;
            }

            Report($"Logging in as {s.Username} (steam: {s.SteamAccount}, free trial: {s.FreeTrial})");
            var result = await service.LoginAsync(new LoginRequest { Username = s.Username, Password = s.Password, OneTimePassword = s.OneTimePassword, SteamAccount = s.SteamAccount, FreeTrial = s.FreeTrial }, gamePath);
            Report($"Login state: {result.State}");

            if (result.State == XivGame.LoginState.Ok)
            {
                // XIVLauncher.Core PersistAccount: merge login into the account store after
                // a successful login; the password is encrypted and stored only when the
                // Save-password checkbox was on (flag captured on the UI thread). The OTP
                // is not persisted.
                PersistAccount(s.Username, s.Password, !string.IsNullOrEmpty(s.OneTimePassword),
                    s.SteamAccount, s.FreeTrial, savePassword, saveAsCurrent: true);
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
                    var ok = await service.PatchAsync(result, s.GamePath, Report);
                    await Dispatcher.UIThread.InvokeAsync(() => CloseProgressPopup());
                    if (!ok)
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

            var launchSteam = s.SteamAccount && !s.IgnoreSteam;   // XIVLauncher.Core IsIgnoringSteam
            if (withDalamud)
            {
                if (!await WaitForDalamudAsync()) return;
                LaunchWithDansharing(prefix, service, gameExe, gamePath, result, lang, launchSteam, noPlugins, noThirdPlugins);
            }
            else
                LaunchPlain(prefix, service, gamePath, result, lang, launchSteam);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Login pipeline failed");
            Dispatcher.UIThread.Post(() => StatusLog.Text += $"Launch failed: {ex.Message}\n");
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
        var gameRunner = new LinuxGameRunner(prefix, this.umuRunPath, WineLogPath, this.settingsService.Settings.WineDebugEnv);
        var game = new XivGame(new LinuxSteamStub(), new NullUniqueIdCache(), this.frontierUrl, "en");

        var gs = this.settingsService.Settings;
        var userPathWine = prefix.UnixToWinePath(service.GameConfigFolder().FullName);
        var extraArgs = $" UserPath={userPathWine}";
        if (!string.IsNullOrWhiteSpace(gs.AdditionalArgs))
            extraArgs += " " + gs.AdditionalArgs;   // regex-split by Launcher.LaunchGame
        var dpi = Enum.TryParse<DpiAwareness>(gs.DpiAwareness, out var d) ? d : DpiAwareness.Unaware;
        Report("LaunchGame (login session, encrypted args)");
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

    /// <summary>Blocks the login pipeline until the background Dalamud updater reaches
    /// Done (injector + runtime downloaded/verified), reporting progress. On failure
    /// (NoIntegrity after 10 tries, or 5 min timeout) the launch is aborted — the user
    /// can retry, matching upstream's "hold for update" gate.</summary>
    private async Task<bool> WaitForDalamudAsync()
    {
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var reportedStep = "";
        Report("Checking Dalamud (background updater)...");
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
        var runner = new LinuxDalamudRunner(prefix, this.umuRunPath, WineLogPath, this.settingsService.Settings.WineDebugEnv);
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
