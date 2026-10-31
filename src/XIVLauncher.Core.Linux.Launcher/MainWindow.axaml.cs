using System;
using System.IO;
using Avalonia.Controls;
using XIVLauncher.Core.Linux;

namespace XIVonLinux.Launcher;

public partial class MainWindow : Window
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnFakeLaunch(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Development spike launch: Steam game path, GE-Proton prefix, umu game-drive env,
        // credential-free game args (same shape as the verified Milestone 1 command).
        var prefix = new UmuPrefix(
            new DirectoryInfo($"{Home}/.xlcore/protonprefix"),
            $"{Home}/.local/share/Steam/steamapps/compatdata/39210",
            $"{Home}/.local/share/Steam/steamapps/common/FINAL FANTASY XIV Online");

        var runner = new LinuxGameRunner(prefix);
        var gameExe = $"{Home}/.local/share/Steam/steamapps/common/FINAL FANTASY XIV Online/game/ffxiv_dx11.exe";

        try
        {
            var proc = runner.Start(gameExe, "", "-fake -autoStart -autoLogin a -autoAcceptedTerms yes",
                prefix.UmuEnvironment(), XIVLauncher.Common.DpiAwareness.Unaware, "");
            StatusText.Text = $"Launched ffxiv_dx11.exe via umu (unix pid {proc?.Id})";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Launch failed: {ex.Message}";
        }
    }
}
