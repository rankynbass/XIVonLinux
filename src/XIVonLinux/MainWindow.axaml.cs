using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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
    // Launcher's frontier URL template (upstream LauncherApp shape; LaunchGame does not use it).
    private const string FrontierUrlTemplate = "https://sqex-fragment.legacy.net/v650/index.html?rc_lang=en&time={1}";

    private int activeLaunches;
    private readonly SettingsService settingsService;

    public MainWindow()
    {
        InitializeComponent();

        this.settingsService = new SettingsService();
        var s = this.settingsService.Settings;
        ProtonPathBox.Text = s.ProtonPath;
        PrefixPathBox.Text = s.PrefixPath;
        GameLocationBox.Text = s.GameLocation;
        PatchFolderBox.Text = s.PatchFolder;
        UsernameBox.Text = s.Username;
        PasswordBox.Text = s.Password;
        OtpBox.Text = s.OneTimePassword;
        SteamAccountToggle.IsChecked = s.SteamAccount;
        FreeTrialToggle.IsChecked = s.FreeTrial;
        DalamudToggle.IsChecked = s.DalamudEnabled;

        // Client language selector: persisted setting, drives the encrypted launch args
        // (language=N), the gate login region, and Dalamud's Language field.
        foreach (var l in new[] { "English", "Japanese", "German", "French" })
            LanguageCombo.Items.Add(l);
        LanguageCombo.SelectedItem = s.Language;
        if (LanguageCombo.SelectedItem is null)
            LanguageCombo.SelectedIndex = 0;
    }

    /// <summary>The combo selection as the Common enum (falls back to English).</summary>
    private ClientLanguage CurrentLanguage()
        => Enum.TryParse<ClientLanguage>(LanguageCombo.SelectedItem as string, out var lang) ? lang : ClientLanguage.English;

    // ---------- folder pickers ----------

    private async void OnBrowseProton(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(ProtonPathBox);

    private async void OnBrowsePrefix(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(PrefixPathBox);

    private async void OnBrowseGame(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(GameLocationBox);

    private async void OnBrowsePatch(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await PickFolderAsync(PatchFolderBox);

    private async Task PickFolderAsync(TextBox box)
    {
        var folders = await this.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
        if (folders.Count > 0)
            box.Text = folders[0].Path.LocalPath;
    }

    // ---------- login ----------

    private void OnLogin(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var s = this.settingsService.Settings;
        s.ProtonPath = ProtonPathBox.Text;
        s.PrefixPath = PrefixPathBox.Text;
        s.GameLocation = GameLocationBox.Text;
        s.PatchFolder = PatchFolderBox.Text;
        s.Username = UsernameBox.Text;
        s.Password = PasswordBox.Text;
        s.OneTimePassword = OtpBox.Text;
        s.SteamAccount = SteamAccountToggle.IsChecked == true;
        s.FreeTrial = FreeTrialToggle.IsChecked == true;
        s.DalamudEnabled = DalamudToggle.IsChecked == true;
        s.Language = LanguageCombo.SelectedItem as string ?? "English";
        this.settingsService.Save();

        StartPipeline(s.DalamudEnabled, false);
    }

    /// <summary>Download-test: login, fetch the patch list against an overridden
    /// (old) reported version, download the latest patch zip into an empty scratch
    /// dir (~/.xivonlinux/patches-test) and SHA1-verify it. No install, no launch —
    /// the game folder is never touched.</summary>
    private void OnDownloadTest(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var s = this.settingsService.Settings;
        s.ProtonPath = ProtonPathBox.Text;
        s.PrefixPath = PrefixPathBox.Text;
        s.GameLocation = GameLocationBox.Text;
        s.PatchFolder = PatchFolderBox.Text;
        s.Username = UsernameBox.Text;
        s.Password = PasswordBox.Text;
        s.OneTimePassword = OtpBox.Text;
        s.SteamAccount = SteamAccountToggle.IsChecked == true;
        s.FreeTrial = FreeTrialToggle.IsChecked == true;
        s.DalamudEnabled = DalamudToggle.IsChecked == true;
        s.Language = LanguageCombo.SelectedItem as string ?? "English";
        this.settingsService.Save();

        StartPipeline(false, true);
    }

    /// <summary>Fires the login pipeline. While a game is up, launches are refused
    /// (shared prefix is single-instance) rather than stacked.</summary>
    private void StartPipeline(bool withDalamud, bool downloadTest)
    {
        if (activeLaunches > 0)
        {
            Report("A game is already running — close it before launching another");
            return;
        }
        LoginButton.IsEnabled = false;
        DownloadTestButton.IsEnabled = false;
        _ = Task.Run(() => RunLoginPipelineAsync(withDalamud, downloadTest));
    }

    /// <summary>Log to Serilog (console/file) and mirror the line into the UI status box.</summary>
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

    // ---- download progress popup ----------------------------------------------------
    // A non-modal popup showing up to 5 live download bars. Rows are rebuilt each
    // second from LoginService.ProgressSnapshot: in-progress slots show bytes/speed
    // bars, queued patches show as queued; finished downloads are excluded, so bars
    // disappear one by one as fewer pieces remain.

    private Window? progressWindow;
    private DispatcherTimer? progressTimer;
    private StackPanel? progressPanel;
    private TextBlock? progressSummary;

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

        var patcher = LoginService.CurrentPatcher;
        var rows = LoginService.ProgressSnapshot(patcher);

        var done = patcher?.Downloads.Count(d => d.State == PatchState.Finished) ?? 0;
        var totalPatches = patcher?.Downloads.Count ?? 0;
        var speed = rows.Sum(r => r.SpeedBps) / 1024 / 1024;

        progressSummary.Text = $"{done}/{totalPatches} downloaded · {speed:0.0} MB/s";

        progressPanel.Children.Clear();
        foreach (var r in rows)
        {
            var label = r.State switch
            {
                "queued" => $"{r.VersionId} — queued ({FmtBytes(r.Total)})",
                "verifying" => $"{r.VersionId} — verifying SHA1 {FmtBytes(r.Downloaded)}/{FmtBytes(r.Total)}",
                _ => $"{r.VersionId} — {FmtBytes(r.Downloaded)}/{FmtBytes(r.Total)} at {r.SpeedBps / 1024 / 1024:0.0} MB/s",
            };
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
    private async Task RunLoginPipelineAsync(bool withDalamud, bool downloadTest)
    {
        LoginService? service = null;
        try
        {
            var s = this.settingsService.Settings;
            Report($"Pipeline start (login, download test: {downloadTest}, dalamud: {withDalamud})");

            var prefixRoot = string.IsNullOrEmpty(s.PrefixPath)
                ? this.settingsService.Root.FullName
                : s.PrefixPath;
            Directory.CreateDirectory(prefixRoot);

            var gamePath = new DirectoryInfo(s.GameLocation);
            var gameExe = new FileInfo(Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe"));
            var gameInstalled = File.Exists(gameExe.FullName);
            Report($"Game location: {gamePath.FullName}");

            if (!gameInstalled && downloadTest)
            {
                Report($"Game exe not found: {gameExe.FullName} — the download test needs an installed game (version + boot files to report).");
                return;
            }

            var proton = s.ProtonPath;
            if (string.IsNullOrEmpty(proton) || !Directory.Exists(proton))
            {
                Report($"Proton path not found: {proton} — pick a Proton (default GE-Proton 11-6) under compatibilitytools.d; umu auto-select would upgrade a GE prefix, so launching is refused.");
                return;
            }
            Report($"Proton: {Path.GetFileName(proton.TrimEnd('/', '\\'))}");

            // GE/Proton convention: WINEPREFIX is the compatdata ROOT, proton appends pfx/.
            var prefix = new UmuPrefix(
                new DirectoryInfo(Path.Combine(prefixRoot, "pfx")),
                prefixRoot,
                gamePath.FullName,
                "umu-steam",
                proton,
                gamePath.FullName);
            Report($"Prefix root: {prefixRoot}");

            Report("Ensuring prefix (create + bootstrap) — first run can take a few minutes");
            new UmuCommandRunner(prefix).EnsurePrefix();
            Report("Prefix ready");

            var lang = Enum.TryParse<ClientLanguage>(s.Language, out var l) ? l : ClientLanguage.English;
            service = new LoginService(this.settingsService, prefix, lang);
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
                    var okBoot = await service.InstallPatchesAsync(Repository.Boot, bootPatches, string.Empty, s, Report);
                    await Dispatcher.UIThread.InvokeAsync(() => CloseProgressPopup());
                    if (!okBoot)
                    {
                        Report("Boot install stopped (cancelled from the progress popup, or failed).");
                        return;
                    }
                }

                Report($"Install step 2: login with base game version report (steam: {s.SteamAccount}, free trial: {s.FreeTrial})");
                var installResult = await service.LoginAsync(s, gamePath, forceBaseVersion: true);
                Report($"Install login state: {installResult.State}");
                if (installResult.State != XivGame.LoginState.NeedsPatchGame)
                {
                    Report($"Unexpected login state {installResult.State} during install (expected NeedsPatchGame).");
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() => OpenProgressPopup("Installing game patches"));
                var okAll = await service.InstallPatchesAsync(Repository.Ffxiv, installResult.PendingPatches, installResult.UniqueId ?? string.Empty, s, Report);
                await Dispatcher.UIThread.InvokeAsync(() => CloseProgressPopup());
                if (!okAll)
                {
                    Report("Game install stopped (cancelled from the progress popup, or failed) — game not installed.");
                    return;
                }

                Report($"Install complete: game ver {Repository.Ffxiv.GetVer(gamePath)}");
                if (withDalamud)
                    LaunchWithDansharing(prefix, service, gameExe, gamePath, installResult, lang);
                else
                    LaunchPlain(prefix, service, gamePath, installResult, lang, s.SteamAccount);
                return;
            }

            Report($"Logging in as {s.Username} (steam: {s.SteamAccount}, free trial: {s.FreeTrial})");
            var result = await service.LoginAsync(s, gamePath);
            Report($"Login state: {result.State}");

            if (downloadTest)
            {
                // Download test: override the reported game version to
                // Constants.BASE_GAME_VERSION so the server returns the FULL patch list
                // for this install, then download the latest patch zip into an empty
                // scratch dir (~/.xivonlinux/patches-test) and verify its per-block
                // SHA1 hashes. No install, no launch — the game folder is untouched.
                var testDir = new DirectoryInfo(Path.Combine(this.settingsService.Root.FullName, "patches-test"));
                if (Directory.Exists(testDir.FullName))
                {
                    Directory.Delete(testDir.FullName, true);
                    Report("Cleared previous patches-test dir");
                }
                Directory.CreateDirectory(testDir.FullName);

                var patches = await service.FetchPatchListAsync(result, gamePath, Constants.BASE_GAME_VERSION);
                Report($"Override report (base ver): server returned {patches.Length} patch entries");
                if (patches.Length == 0)
                {
                    Report("No patches returned — nothing to download.");
                    return;
                }

                var latest = patches[^1];
                Report($"Latest patch: {latest.GetRepoName()}/{latest.VersionId} ({latest.Length / 1048576} MB, {latest.Hashes?.Length ?? 0} sha1 blocks) -> {testDir.FullName}");
                var ok = await service.DownloadPatchAsync(latest, testDir, Report);
                Report(ok ? $"Download test PASS: {latest.VersionId}" : $"Download test FAIL: {latest.VersionId}");
                return;
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
                    var ok = await service.PatchAsync(result, s, Report);
                    await Dispatcher.UIThread.InvokeAsync(() => CloseProgressPopup());
                    if (!ok)
                    {
                        Report("Patching stopped (cancelled from the progress popup, or failed) — game not launched.");
                        return;
                    }
                    result.State = XivGame.LoginState.Ok;
                    break;
            }

            if (withDalamud)
                LaunchWithDansharing(prefix, service, gameExe, gamePath, result, lang);
            else
                LaunchPlain(prefix, service, gamePath, result, lang, s.SteamAccount);
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
                DownloadTestButton.IsEnabled = true;
            });
        }
    }

    /// <summary>Plain launch: LaunchGame with the real login session (uid, region,
    /// expansion) and encrypted args, UserPath pointing at ~/.xivonlinux/ffxivConfig
    /// in Wine-style prefix path, no injector.</summary>
    private void LaunchPlain(UmuPrefix prefix, LoginService service, DirectoryInfo gamePath, XivGame.LoginResult result, ClientLanguage lang, bool isSteam)
    {
        var gameRunner = new LinuxGameRunner(prefix);
        var game = new XivGame(new LinuxSteamStub(), new NullUniqueIdCache(), FrontierUrlTemplate, "en");

        var userPathWine = prefix.UnixToWinePath(service.GameConfigFolder().FullName);
        Report("LaunchGame (login session, encrypted args)");
        var proc = game.LaunchGame(gameRunner, result.UniqueId ?? string.Empty, result.OauthLogin!.Region,
            result.OauthLogin.MaxExpansion, isSteam, $" UserPath={userPathWine}", "",
            gamePath, lang, true, DpiAwareness.Unaware);

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

    /// <summary>In-prefix injector launch with the real login: the injector gets the
    /// encrypted game args (session, region, expansion, ver, UserPath); Run returns
    /// as soon as the injector's JSON arrives (game is up); game exit is watched
    /// off the UI thread.</summary>
    private void LaunchWithDansharing(UmuPrefix prefix, LoginService service, FileInfo gameExe, DirectoryInfo gamePath, XivGame.LoginResult result, ClientLanguage lang)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dalamudFiles = Path.Combine(home, "projects", "AI", "DalamudFiles");
        var hooks = Path.Combine(dalamudFiles, "dalamud", "Hooks", "dev");
        var injectorExe = new FileInfo(Path.Combine(hooks, "Dalamud.Injector.exe"));
        if (!File.Exists(injectorExe.FullName))
        {
            Report($"FAIL: injector exe missing at {injectorExe.FullName}");
            return;
        }
        var logs = Path.Combine(home, ".xlcore", "xivonlinux-logs");
        Directory.CreateDirectory(logs);

        var startInfo = new DalamudStartInfo
        {
            WorkingDirectory = hooks,
            ConfigurationPath = Path.Combine(home, ".xlcore", "xivonlinux-dalamudConfig.json"),
            LoggingPath = logs,
            PluginDirectory = Path.Combine(home, ".xlcore", "installedPlugins"),
            AssetDirectory = Path.Combine(dalamudFiles, "dalamudAssets", "447"),
            Language = lang,
            DelayInitializeMs = 0,
        };

        // Wine .NET runtime for the in-prefix injector (M1-verified env shape; Wine-style paths).
        var runtimeWine = prefix.UnixToWinePath(Path.Combine(home, ".xlcore", "runtime"));
        var env = new UmuInPrefixEnvironment(prefix).Build(
            ("DOTNET_ROOT", runtimeWine),
            ("WINEDOTNET_ROOT", runtimeWine),
            ("DALAMUD_RUNTIME", runtimeWine));

        var gameArgs = service.BuildEncryptedGameArgs(result, gamePath, service.GameConfigFolder());

        Interlocked.Increment(ref activeLaunches);
        Report("In-prefix injector: SE login session + DllInject");
        var runner = new LinuxDalamudRunner(prefix);
        var proc = runner.Run(injectorExe, false, false, false, gameExe, gameArgs, "", env,
            DalamudLoadMethod.DllInject, startInfo);

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
