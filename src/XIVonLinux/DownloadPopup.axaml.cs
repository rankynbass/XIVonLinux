using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Serilog;
using XIVLauncher.Common.Game.Patch;

namespace XIVonLinux;

/// <summary>
/// Non-modal download progress popup: one longest overall bar spanning every patch
/// (finished included), then one bar per downloading/verifying file — min 0, max 4
/// (1-3 downloading files show that many bars, 4+ show 4). Rows rebuild every second;
/// queued files count in the summary line, finished downloads disappear. The popup
/// auto-closes when all downloads are complete.
///
/// Real mode reads LoginService.CurrentPatcher via LoginService.ProgressSnapshot.
/// Fake test mode (XIVON_FAKE_DOWNLOADS=1, no network) drives the same layout from
/// 40 simulated patches (20-119 MB) across 4 slots at 10-25 MB/s, each patch
/// verifying for 2 ticks — built so the bar layout can be exercised without aria2.
/// Cancel pauses (cancels) all downloads, then closes; the X button does the same
/// via the Closing event.
/// </summary>
public partial class DownloadPopup : Window
{
    private readonly bool fakeMode;
    private DispatcherTimer? timer;

    // Fake test simulation state.
    private sealed class FakePatch
    {
        public string VersionId = "";
        public long Length;
        public long Downloaded;
        public int VerifyTicks;
        public bool Done;
    }

    private const int FakeCount = 40;
    private readonly List<FakePatch> fakePatches = new();
    private readonly int[] fakeSlotIdx = new int[4];

    public DownloadPopup(string title, bool fakeMode = false)
    {
        InitializeComponent();
        Title = title;
        this.fakeMode = fakeMode;

        if (fakeMode)
        {
            for (var i = 0; i < FakeCount; i++)
                fakePatches.Add(new FakePatch { VersionId = $"fake-{i:D2}", Length = (20 + i * 37 % 100) * 1024 * 1024 });
            for (var s = 0; s < 4; s++)
                fakeSlotIdx[s] = s;   // slot s owns patches s, s+4, s+8, ...
            Log.Information("Fake download test mode: {Count} fake patches, 4 slots", FakeCount);
        }

        CancelButton.Click += (_, __) =>
        {
            LoginService.CancelCurrentDownloads();
            Dispatcher.UIThread.Post(() => ClosePopup());
        };
        Closing += (_, __) => LoginService.CancelCurrentDownloads();

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, __) => Update();
        timer.Start();
        Update();
    }

    /// <summary>Stop the tick and close the window (pipeline install completion,
    /// Cancel, and auto-close on all-done all route through here).</summary>
    public void ClosePopup()
    {
        timer?.Stop();
        timer = null;
        Close();
    }

    private void Update()
    {
        List<LoginService.DownloadSlotInfo> rows;
        int done, totalFiles, queued;
        double speed;
        long totalBytes, downloadedBytes;

        var patcher = LoginService.CurrentPatcher;
        if (fakeMode)
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
            ClosePopup();
            return;
        }

        // File bars: min 0, max 4. 1-3 downloading files show that many bars, 4+ show 4.
        var activeBars = rows.Where(r => r.State != "queued").Take(4).ToList();

        SummaryText.Text = $"{done}/{totalFiles} downloaded · {queued} queued · {speed:0.0} MB/s";

        if (fakeMode)
            Log.Information("Fake tick: bars={Bars} done={Done} queued={Queued} bytes={Bytes}/{Total}",
                activeBars.Count, done, queued, downloadedBytes, totalBytes);

        BarsPanel.Children.Clear();

        // Overall bar (longest): spans every patch, finished included.
        BarsPanel.Children.Add(new TextBlock
        {
            Text = "Overall",
            Margin = new Thickness(0, 2, 0, 0),
        });
        BarsPanel.Children.Add(new ProgressBar
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
            BarsPanel.Children.Add(new TextBlock
            {
                Text = label,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
            BarsPanel.Children.Add(new ProgressBar
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

    // ---------- fake test simulation ----------

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
            var p = fakePatches[idx];
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
            var p = fakePatches[idx];
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

    private static string FmtBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / 1024.0 / 1024 / 1024:0.00} GB";
        if (bytes >= 1L << 20) return $"{bytes / 1024.0 / 1024:0.0} MB";
        if (bytes >= 1L << 10) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes} B";
    }
}
