using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLaunchDalamud(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => StartPipeline(true);

    private void OnLaunchNoDalamud(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => StartPipeline(false);

    /// <summary>Fires a launch pipeline. While a game is up, launches are refused
    /// (shared prefix is single-instance) rather than stacked.</summary>
    private void StartPipeline(bool withDalamud)
    {
        if (activeLaunches > 0)
        {
            Report("A game is already running — close it before launching another");
            return;
        }
        LaunchDalamudButton.IsEnabled = false;
        LaunchPlainButton.IsEnabled = false;
        _ = Task.Run(() => RunLaunchPipeline(withDalamud));
    }

    /// <summary>Log to Serilog (console/file) and mirror the line into the UI status box.</summary>
    private void Report(string line)
    {
        Log.Information(line);
        Dispatcher.UIThread.Post(() => StatusLog.Text += line + "\n");
    }

    /// <summary>
    /// Shared pipeline: Steam install detection -> GE-Proton 11-6 pin -> prefix ensure
    /// (create + bootstrap) -> credential-free fake-login launch, with the in-prefix
    /// Windows injector (Phase B) or plain LinuxGameRunner launch (Phase A).
    /// Runs off the UI thread; first-run prefix creation can take minutes.
    /// </summary>
    private void RunLaunchPipeline(bool withDalamud)
    {
        try
        {
            Report($"Pipeline start (dalamud: {withDalamud})");
            var steam = SteamInstall.Detect();
            if (steam is null)
            {
                Log.Error("Steam FFXIV install not found (steamapps root, appmanifest_39210.acf StateFlags=4, game/ffxiv_dx11.exe).");
                return;
            }
            Report($"Steam install: {steam.GamePath.FullName}");

            var proton = ProtonLocator.FindGeProton();
            if (proton is null)
            {
                Log.Error("GE-Proton 11-6 not found under compatibilitytools.d — umu auto-select would upgrade a GE prefix, so launching is refused.");
                return;
            }
            Report($"Proton pinned: {Path.GetFileName(proton)}");

            var prefix = new UmuPrefix(
                new DirectoryInfo(Path.Combine(steam.CompatDataPath, "pfx")),
                steam.CompatDataPath,
                steam.GamePath.FullName,
                "umu-steam",
                proton,
                steam.GamePath.FullName);

            Report("Ensuring prefix (create + bootstrap) — first run can take a few minutes");
            new UmuCommandRunner(prefix).EnsurePrefix();
            Report("Prefix ready");

            if (withDalamud)
                LaunchWithDalamud(prefix, steam);
            else
                LaunchPlain(prefix, steam);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Launch pipeline failed");
            Dispatcher.UIThread.Post(() => StatusLog.Text += $"Launch failed: {ex.Message}\n");
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                LaunchDalamudButton.IsEnabled = true;
                LaunchPlainButton.IsEnabled = true;
            });
        }
    }

    /// <summary>Phase A shape: upstream LoginAction.Fake — LaunchGame via LinuxGameRunner,
    /// Steam service account, encrypted args, no injector.</summary>
    private void LaunchPlain(UmuPrefix prefix, SteamInstall steam)
    {
        var gameRunner = new LinuxGameRunner(prefix);
        var game = new XivGame(new LinuxSteamStub(), new NullUniqueIdCache(), FrontierUrlTemplate, "en");

        Report("LaunchGame (fake login, Steam service account, encrypted args)");
        var proc = game.LaunchGame(gameRunner, "0", 1, 2, true, "", "",
            steam.GamePath, ClientLanguage.Japanese, true, DpiAwareness.Unaware);

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
    /// (spike/dalamud-harness verified). Run blocks for the game's lifetime — the injector
    /// monitors the game process, so this call returns when the game exits.</summary>
    private void LaunchWithDalamud(UmuPrefix prefix, SteamInstall steam)
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
        var gameExe = new FileInfo(Path.Combine(steam.GamePath.FullName, "game", "ffxiv_dx11.exe"));
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

        // Run returns as soon as the injector's JSON arrives (game is up); watch its exit
        // off the UI thread, buttons stay usable, report full injector output at exit.
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
