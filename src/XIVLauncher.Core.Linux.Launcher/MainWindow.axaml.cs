using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Core.Linux;
using XivGame = XIVLauncher.Common.Game.Launcher;

namespace XIVonLinux.Launcher;

public partial class MainWindow : Window
{
    // Launcher's frontier URL template (upstream LauncherApp shape; LaunchGame does not use it).
    private const string FrontierUrlTemplate = "https://sqex-fragment.legacy.net/v650/index.html?rc_lang=en&time={1}";

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLaunch(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LaunchButton.IsEnabled = false;
        _ = Task.Run(RunLaunchPipeline);
    }

    /// <summary>Log to Serilog (console/file) and mirror the line into the UI status box.</summary>
    private void Report(string line)
    {
        Log.Information(line);
        Dispatcher.UIThread.Post(() => StatusLog.Text += line + "\n");
    }

    /// <summary>
    /// Phase-A pipeline: Steam install detection -> GE-Proton 11-6 pin -> prefix ensure
    /// (create + bootstrap) -> credential-free LaunchGame via LinuxGameRunner/umu.
    /// Runs off the UI thread; first-run prefix creation can take minutes.
    /// </summary>
    private void RunLaunchPipeline()
    {
        try
        {
            Report("Detecting Steam install (appmanifest_39210.acf)");
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
                proton);

            Report("Ensuring prefix (create + bootstrap) — first run can take a few minutes");
            new UmuCommandRunner(prefix).EnsurePrefix();
            Report("Prefix ready");

            var gameRunner = new LinuxGameRunner(prefix);
            var game = new XivGame(new LinuxSteamStub(), new NullUniqueIdCache(), FrontierUrlTemplate, "en");

            // Credential-free launch, upstream LoginAction.Fake shape, Steam service account.
            Report("LaunchGame (fake login, Steam service account, encrypted args)");
            var proc = game.LaunchGame(gameRunner, "0", 1, 2, true, "", "",
                steam.GamePath, ClientLanguage.Japanese, true, DpiAwareness.Unaware);

            Report($"Launched ffxiv_dx11.exe via umu (unix pid {proc?.Id})");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Launch pipeline failed");
            Dispatcher.UIThread.Post(() => StatusLog.Text += $"Launch failed: {ex.Message}\n");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => LaunchButton.IsEnabled = true);
        }
    }
}
