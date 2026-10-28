using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using SysFloat.Configuration;
using SysFloat.Monitoring;
using SysFloat.UI;

internal static class DualGpuChecks
{
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr process, uint flags);
    private static GpuMemoryInfo Card(string id, float percent) => new GpuMemoryInfo {
        Id = id, Name = id == "nvidia" ? "RTX 4070 Ti SUPER" : "AMD Radeon 780M",
        Available = true, Percent = percent,
        TotalBytes = id == "nvidia" ? 16UL * 1024 * 1024 * 1024 : 512UL * 1024 * 1024,
        UsedBytes = 128UL * 1024 * 1024, SharedAvailable = true, SharedUsedBytes = 256UL * 1024 * 1024 };

    private static MetricSnapshot Snapshot(float left = 95, float right = 20) => new MetricSnapshot {
        CpuPercent = 10, MemoryPercent = 30, VramAvailable = true, VramPercent = left,
        Gpus = new[] { Card("nvidia", left), Card("amd", right) } };

    internal static void Run(Action<bool, string> check, string output)
    {
        var settings = new AppSettings { HighLoadAlertsEnabled = true, HighLoadThresholdPercent = 90, HighLoadDurationSeconds = 30 };
        var tracker = new HighLoadAlertTracker();
        var snapshot = Snapshot();
        check(!tracker.Evaluate(snapshot, settings, 0).IsHighLoad, "dual GPU alerts wait for configured duration");
        check(!tracker.Evaluate(snapshot, settings, 29999).IsHighLoad, "dual GPU alert has no early threshold activation");
        var alert = tracker.Evaluate(snapshot, settings, 30000);
        check(alert.ActiveMetricsMask == 4 && alert.ActiveGpuIds.SequenceEqual(new[] { "nvidia" }) &&
            alert.Message.Contains("RTX 4070"), "only the sustained high GPU alerts, not an average of both cards");
        snapshot.Gpus[1].Percent = 95;
        tracker.Evaluate(snapshot, settings, 31000);
        var both = tracker.Evaluate(snapshot, settings, 61000);
        check(both.ActiveGpuIds.Count == 2 && both.JustActivated, "second GPU has its own sustained-load activation while first stays high");
        snapshot.Gpus[0].Percent = 20;
        check(tracker.Evaluate(snapshot, settings, 62000).ActiveGpuIds.SequenceEqual(new[] { "amd" }),
            "recovering one card clears only its alert even while the VRAM metric mask stays active");
        snapshot.Gpus[0].Percent = float.NaN;
        snapshot.Gpus[1].Available = false;
        check(!tracker.Evaluate(snapshot, settings, 63000).IsHighLoad, "invalid/unavailable GPU data never raises an alert");
        check(GpuDisplay.Summary(snapshot) == "-- | --", "unavailable card slots remain visible without showing invented zero usage");
        tracker.Reset();
        snapshot = Snapshot();
        tracker.Evaluate(snapshot, settings, 0);
        snapshot.Gpus = new[] { Card("amd", 95), Card("nvidia", 20) };
        check(!tracker.Evaluate(snapshot, settings, 20000).IsHighLoad &&
            !tracker.Evaluate(snapshot, settings, 30000).IsHighLoad,
            "alternating GPU load and reordered slots cannot share an alert timer");
        check(tracker.Evaluate(snapshot, settings, 50000).ActiveGpuIds.SequenceEqual(new[] { "amd" }),
            "GPU timers follow stable adapter IDs instead of visual indexes");
        snapshot.Gpus = Array.Empty<GpuMemoryInfo>(); snapshot.VramPercent = 0;
        tracker.Evaluate(snapshot, settings, 51000);
        var starts = (System.Collections.IDictionary)typeof(HighLoadAlertTracker)
            .GetField("_gpuStartedAt", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(tracker);
        check(starts.Count == 0, "removed adapters release their alert timer entries");
        tracker.Reset(); snapshot = Snapshot(); tracker.Evaluate(snapshot, settings, 0);
        settings.HighLoadAlertsEnabled = false;
        check(tracker.Evaluate(snapshot, settings, 30000).ActiveGpuIds.Count == 0, "disabled monitoring clears per-GPU colors and state");
        settings.HighLoadAlertsEnabled = true;
        check(!tracker.Evaluate(snapshot, settings, 31000).IsHighLoad, "re-enabled GPU alerts restart duration rather than inheriting stale high time");
        var calculate = typeof(GpuMemoryMonitor).GetMethod("CalculatePercent", BindingFlags.Static | BindingFlags.NonPublic);
        check((float)calculate.Invoke(null, new object[] { 256UL, 512UL }) == 50 &&
            (float)calculate.Invoke(null, new object[] { 10UL, 0UL }) == -1 &&
            (float)calculate.Invoke(null, new object[] { 513UL, 512UL }) == -1,
            "VRAM percentages use each card's own capacity; missing/inconsistent totals stay unknown");

        UILanguage language = UIStrings.Language;
        snapshot = Snapshot();
        foreach (var selected in new[] { UILanguage.Chinese, UILanguage.English })
        {
            UIStrings.SetLanguage(selected);
            string tip = GpuDisplay.ToolTip(snapshot);
            check(tip.Contains("RTX 4070") && tip.Contains("780M") && tip.Contains("16.00 GB") && tip.Contains("512 MB") &&
                !tip.Contains("tip.gpu"), $"{selected} dual GPU tooltip identifies capacities and separate shared memory");
        }
        UIStrings.SetLanguage(language);
        check(GpuDisplay.Summary(snapshot) == "95% | 20%", "dual GPU summary preserves exact left-to-right percentage format");
        check(GpuDisplay.Summary(new MetricSnapshot { VramAvailable = true, VramPercent = 40 }) == "40%",
            "legacy single-card snapshots retain their original value format");

        foreach (WidgetLayout layout in Enum.GetValues(typeof(WidgetLayout)))
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            snapshot = Snapshot(100, 100);
            Size logical = WidgetSizeHelper.GetSize(layout, 1f);
            Size physical = WidgetSizeHelper.GetSize(layout, scale);
            using (var bitmap = new Bitmap(physical.Width, physical.Height))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.ScaleTransform(scale, scale);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float available = layout == WidgetLayout.Horizontal ? (logical.Width - 124) / 3f - 8 :
                    layout == WidgetLayout.Vertical ? logical.Width - 18 : layout == WidgetLayout.Mini ? logical.Width / 3f - 8 : logical.Width * .55f - 30;
                float measured = (float)typeof(WidgetRenderer).GetMethod("MeasureGpuValues", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { g, snapshot, available, layout == WidgetLayout.Mini });
                check(measured <= available, $"{layout} {scale * 100:0}% fits worst-case 100% | 100% inside existing width");
                WidgetRenderer.DrawLayout(g, new Rectangle(Point.Empty, logical), layout, snapshot,
                    activeGpuIds: new[] { "nvidia" });
                bitmap.Save(Path.Combine(output, $"DualGpu-{layout}-{scale * 100:0}.png"), ImageFormat.Png);
            }
        }
        foreach (string activeId in new[] { "nvidia", "amd", "none" })
        {
            snapshot = Snapshot(95, 95);
            Size size = WidgetSizeHelper.GetSize(WidgetLayout.Horizontal, 1f);
            using (var bitmap = new Bitmap(size.Width, size.Height))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                WidgetRenderer.DrawHorizontal(g, new Rectangle(Point.Empty, size), snapshot, activeGpuIds: new[] { activeId });
                int leftOrange = 0, rightOrange = 0, leftGreen = 0, rightGreen = 0;
                int start = (size.Width - 124) / 3 * 2, width = (size.Width - 124) / 3;
                for (int y = 20; y < 45; y++) for (int x = start; x < start + width; x++)
                {
                    Color pixel = bitmap.GetPixel(x, y);
                    bool left = x < start + width / 2;
                    if (pixel.R > 230 && pixel.G > 135 && pixel.G < 200 && pixel.B < 130)
                    { if (left) leftOrange++; else rightOrange++; }
                    if (pixel.G > 180 && pixel.R < 180 && pixel.B > 120 && pixel.B < 200)
                    { if (left) leftGreen++; else rightGreen++; }
                }
                check(activeId == "nvidia" ? leftOrange > 0 && rightOrange == 0 && rightGreen > 0 :
                    activeId == "amd" ? rightOrange > 0 && leftOrange == 0 && leftGreen > 0 : leftOrange + rightOrange == 0,
                    $"pixel proof: per-card alert colors remain independent ({activeId})");
                bitmap.Save(Path.Combine(output, $"DualGpu-alert-{activeId}.png"), ImageFormat.Png);
            }
        }
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        using (var bitmap = new Bitmap(448, 60))
        using (var g = Graphics.FromImage(bitmap))
        {
            snapshot = Snapshot();
            uint before = GetGuiResources(process.Handle, 0);
            for (int i = 0; i < 150; i++)
                WidgetRenderer.DrawHorizontal(g, new Rectangle(0, 0, 448, 60), snapshot, activeGpuIds: new[] { "nvidia" });
            uint after = GetGuiResources(process.Handle, 0);
            check(after <= before + 2, $"dual GPU redraws reuse fonts and do not leak GDI objects ({before}->{after})");
        }
        using (var monitor = new GpuMemoryMonitor())
        {
            var cards = monitor.GetGpuMemoryInfo();
            check(cards.Count >= 2 && cards[0].Name.Contains("NVIDIA") && cards.Any(card => card.Name.Contains("780M")),
                "live dual GPU sampling has stable NVIDIA-left / AMD-right order");
            check(cards.All(card => card.Available && card.TotalBytes > 0 && card.Percent >= 0 && card.Percent <= 100),
                "both real GPU dedicated memory readings and capacities are valid");
            string[] ids = cards.Select(card => card.Id).ToArray();
            foreach (var card in cards)
                Console.WriteLine($"GPU: {card.Name}: {card.Percent:0.0}% dedicated={card.UsedBytes}/{card.TotalBytes} shared={card.SharedUsedBytes}");
            for (int i = 0; i < 3; i++)
                check(monitor.GetGpuMemoryInfo().Select(card => card.Id).SequenceEqual(ids), "changing GPU usage does not reorder visual slots");
            using (var process = System.Diagnostics.Process.GetCurrentProcess())
            {
                int handles = process.HandleCount;
                double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 5; i++) monitor.GetGpuMemoryInfo();
                watch.Stop(); process.Refresh();
                check(process.HandleCount <= handles + 4, "repeated dual GPU sensor reads keep native handle count stable");
                Console.WriteLine($"Dual GPU sampling: avg={watch.Elapsed.TotalMilliseconds / 5:F2}ms cpuAvg={(process.TotalProcessorTime.TotalMilliseconds - cpuBefore) / 5:F2}ms handles={handles}->{process.HandleCount}");
            }
            monitor.Dispose();
            check(monitor.GetGpuMemoryInfo().Count == 0, "disposed GPU monitor cannot reuse native resources");
        }
    }
}
