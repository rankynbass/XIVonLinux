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
    }

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
        this.settingsService.Save();

        StartPipeline(s.DalamudEnabled);
    }

    /// <summary>Fires a launch pipeline. While a game is up, launches are refused
    /// (shared prefix is single-instance) rather than stacked.</summary>
    private void StartPipeline(bool withDalamud)
    {
        if (activeLaunches > 0)
        {
            Report("A game is already running — close it before launching another");
            return;
        }
        LoginButton.IsEnabled = false;
        _ = Task.Run(() => RunLaunchPipeline(withDalamud));
    }

    /// <summary>Log to Serilog (console/file) and mirror the line into the UI status box.</summary>
    private void Report(string line)
    {
        Log.Information(line);
        Dispatcher.UIThread.Post(() => StatusLog.Text += line + "\n");
    }

    /// <summary>
    /// Shared pipeline: settings (prefix, game location, proton path) -> prefix ensure
    /// (create + bootstrap) -> credential-free fake-login launch, with the in-prefix
    /// Windows injector (Phase B) or plain LinuxGameRunner launch (Phase A).
    /// Runs off the UI thread; first-run prefix creation can take minutes.
    /// Login path is not built yet — login dispatches to the fake-launch pipeline.
    /// The WINEPREFIX root comes from settings.PrefixPath (default ~/.xivonlinux),
    /// so prefix, settings and dalamud data all live under ~/.xivonlinux.
    /// </summary>
    private void RunLaunchPipeline(bool withDalamud)
    {
        try
        {
            var s = this.settingsService.Settings;
            Report($"Pipeline start (dalamud: {withDalamud})");

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
            Report($"Proton: {Path.GetFileName(proton)}");

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

            if (withDalamud)
                LaunchWithDansharing(prefix, gameExe, gamePath);
            else
                LaunchPlain(prefix, gamePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Launch pipeline failed");
            Dispatcher.UIThread.Post(() => StatusLog.Text += $"Launch failed: {ex.Message}\n");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => LoginButton.IsEnabled = true);
        }
    }

    /// <summary>Phase A shape: upstream LoginAction.Fake — LaunchGame via LinuxGameRunner,
    /// Steam service account, encrypted args, no injector.</summary>
    private void LaunchPlain(UmuPrefix prefix, DirectoryInfo gamePath)
    {
        var gameRunner = new LinuxGameRunner(prefix);
        var game = new XivGame(new LinuxSteamStub(), new NullUniqueIdCache(), FrontierUrlTemplate, "en");

        Report("LaunchGame (fake login, Steam service account, encrypted args)");
        var proc = game.LaunchGame(gameRunner, "0", 1, 2, true, "", "",
            gamePath, ClientLanguage.Japanese, true, DpiAwareness.Unaware);

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

    /// <summary>Phase B shape: Windows Dalamud injector run inside the prefix via umu
    /// (spike/dalamud-harness verified). Run returns as soon as the injector's JSON
    /// arrives (game is up); game exit is watched off the UI thread.</summary>
    private void LaunchWithDansharing(UmuPrefix prefix, FileInfo gameExe, DirectoryInfo gamePath)
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
            Language = ClientLanguage.Japanese,
            DelayInitializeMs = 0,
        };

        // Wine .NET runtime for the in-prefix injector (M1-verified env shape; Wine-style paths).
        var runtimeWine = prefix.UnixToWinePath(Path.Combine(home, ".xlcore", "runtime"));
        var env = new UmuInPrefixEnvironment(prefix).Build(
            ("DOTNET_ROOT", runtimeWine),
            ("WINEDOTNET_ROOT", runtimeWine),
            ("DALAMUD_RUNTIME", runtimeWine));

        Interlocked.Increment(ref activeLaunches);
        Report("In-prefix injector: fake login + DllInject");
        var runner = new LinuxDalamudRunner(prefix);
        var proc = runner.Run(injectorExe, true, false, false, gameExe,
            "-fake -autoStart -autoLogin a -autoAcceptedTerms yes", "", env,
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
