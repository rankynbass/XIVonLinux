using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Core.Linux;
using XivGame = XIVLauncher.Common.Game.Launcher;

namespace XIVonLinux.Launcher;

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

        StartPipeline(s.DalamudEnabled);
    }

    /// <summary>Fires the login pipeline. While a game is up, launches are refused
    /// (shared prefix is single-instance) rather than stacked.</summary>
    private void StartPipeline(bool withDalamud)
    {
        if (activeLaunches > 0)
        {
            Report("A game is already running — close it before launching another");
            return;
        }
        LoginButton.IsEnabled = false;
        _ = Task.Run(() => RunLoginPipelineAsync(withDalamud));
    }

    /// <summary>Log to Serilog (console/file) and mirror the line into the UI status box.</summary>
    private void Report(string line)
    {
        Log.Information(line);
        Dispatcher.UIThread.Post(() => StatusLog.Text += line + "\n");
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
    private async Task RunLoginPipelineAsync(bool withDalamud)
    {
        LoginService? service = null;
        try
        {
            var s = this.settingsService.Settings;
            Report($"Pipeline start (login, dalamud: {withDalamud})");

            var prefixRoot = string.IsNullOrEmpty(s.PrefixPath)
                ? this.settingsService.Root.FullName
                : s.PrefixPath;
            Directory.CreateDirectory(prefixRoot);

            var gamePath = new DirectoryInfo(s.GameLocation);
            var gameExe = new FileInfo(Path.Combine(gamePath.FullName, "game", "ffxiv_dx11.exe"));
            if (!File.Exists(gameExe.FullName))
            {
                Log.Error($"Game exe not found: {gameExe.FullName} — pick the game folder containing game/ffxiv_dx11.exe.");
                return;
            }
            Report($"Game location: {gamePath.FullName}");

            var proton = s.ProtonPath;
            if (string.IsNullOrEmpty(proton) || !Directory.Exists(proton))
            {
                Log.Error($"Proton path not found: {proton} — pick a Proton (default GE-Proton 11-6) under compatibilitytools.d; umu auto-select would upgrade a GE prefix, so launching is refused.");
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

            Report($"Logging in as {s.Username} (steam: {s.SteamAccount}, free trial: {s.FreeTrial})");
            var result = await service.LoginAsync(s, gamePath);
            Report($"Login state: {result.State}");

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
                    var ok = await service.PatchAsync(result, s, Report);
                    if (!ok)
                    {
                        Report("Patching failed — game not launched.");
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
            Dispatcher.UIThread.Post(() => LoginButton.IsEnabled = true);
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
