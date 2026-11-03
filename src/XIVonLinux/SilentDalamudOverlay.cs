using XIVLauncher.Common.PlatformAbstractions;

namespace XIVonLinux;

/// <summary>No-op overlay for the UI-less launcher: DalamudUpdater.SetOverlayProgress
/// dereferences Overlay unconditionally, so an updater without an overlay object throws
/// NullReferenceException and every update try fails. This sink accepts all calls.</summary>
public sealed class SilentDalamudOverlay : IDalamudLoadingOverlay
{
    public void SetStep(IDalamudLoadingOverlay.DalamudUpdateStep step) { }
    public void SetVisible() { }
    public void SetInvisible() { }
    public void ReportProgress(long? size, long downloaded, double? progress) { }
}
