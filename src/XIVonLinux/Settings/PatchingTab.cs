using Avalonia.Interactivity;

namespace XIVonLinux.Settings;

/// <summary>Patching tab: download speed limit (KiB/s) and connections per server.
/// Partial of SettingsWindow; the core dispatches load through PatchingTabLoad and
/// save through PatchingTabSave.</summary>
public partial class SettingsWindow
{
    private void PatchingTabLoad()
    {
        var settings = this.settingsService.Settings;
        SpeedBox.Text = settings.MaxDownloadSpeedKibps.ToString();
        ConnectionsBox.Text = settings.MaxConnections.ToString();
    }

    /// <summary>Commits Patching tab fields into settings; returns false (alert shown)
    /// when a value is out of range. Speed in KiB/s (0 = unlimited), connections 1-16.</summary>
    private bool PatchingTabSave(LaunchSettings settings)
    {
        if (!int.TryParse(SpeedBox.Text, out var speedKibps) || speedKibps < 0 || speedKibps > 10485760)
        {
            ShowAlert("Speed must be a whole number 0-10485760 KiB/s — KiB/s means kilobytes per second, not kilobits. 0 = unlimited.");
            return false;
        }
        if (!int.TryParse(ConnectionsBox.Text, out var connections) || connections < 1 || connections > 16)
        {
            ShowAlert("Connections must be a whole number 1-16.");
            return false;
        }
        settings.MaxDownloadSpeedKibps = speedKibps;
        settings.MaxConnections = connections;
        return true;
    }
}
