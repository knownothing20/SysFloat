using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using SysFloat.Configuration;
using SysFloat.Monitoring;
using SysFloat.UI;
using SysFloat;

internal static class Program
{
    private static readonly string Output = Path.Combine(AppContext.BaseDirectory, "evidence");
    private static int _passed;
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Directory.CreateDirectory(Output);
            VerifyClock();
            VerifyAlert();
            VerifySettings();
            VerifyGpuUnits();
            RenderLayouts();
            VerifyLiveWiring();
            VerifyTrayWiring();
            Console.WriteLine($"PASS {_passed} checks. Evidence: {Output}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _passed++;
        Console.WriteLine("PASS " + message);
    }

    private static DateTime Utc(string value) => DateTime.SpecifyKind(DateTime.Parse(value), DateTimeKind.Utc);

    private static void VerifyClock()
    {
        Check(PacificClock.FormatTime(Utc("2026-01-15 20:00")) == "12:00" &&
            PacificClock.GetAbbreviation(Utc("2026-01-15 20:00")) == "PST", "winter PST conversion");
        Check(PacificClock.FormatTime(Utc("2026-07-15 20:00")) == "13:00" &&
            PacificClock.GetAbbreviation(Utc("2026-07-15 20:00")) == "PDT", "summer PDT conversion");
        Check(PacificClock.FormatDate(Utc("2026-10-09 05:39")) == "10/08" &&
            PacificClock.FormatTime(Utc("2026-10-09 05:39")) == "22:39", "previous-day conversion");
        Check(PacificClock.FormatTime(Utc("2026-11-01 08:30")) == "01:30" &&
            PacificClock.GetAbbreviation(Utc("2026-11-01 08:30")) == "PDT" &&
            PacificClock.FormatTime(Utc("2026-11-01 09:30")) == "01:30" &&
            PacificClock.GetAbbreviation(Utc("2026-11-01 09:30")) == "PST", "DST repeated-hour distinction");
    }

    private static void VerifyAlert()
    {
        var settings = new AppSettings();
        var tracker = new HighLoadAlertTracker();
        var high = new MetricSnapshot { CpuPercent = 95, MemoryPercent = 33, VramAvailable = true, VramPercent = 11 };
        Check(!tracker.Evaluate(high, settings, 0).IsHighLoad, "no immediate alert");
        Check(!tracker.Evaluate(high, settings, 29000).IsHighLoad, "duration gate independent of sample count");
        var active = tracker.Evaluate(high, settings, 30000);
        Check(active.IsHighLoad && active.JustActivated && active.ActiveMetricsMask == 1, "alert after 30 actual seconds, CPU-only color mask");
        Check(!tracker.Evaluate(high, settings, 31000).JustActivated, "one activation per continuous episode");
        high.CpuPercent = 40;
        Check(!tracker.Evaluate(high, settings, 32000).IsHighLoad, "recovery clears alert");
        tracker.Reset();
        high.CpuPercent = 95;
        tracker.Evaluate(high, settings, 40000);
        high.CpuPercent = 20; high.MemoryPercent = 95;
        Check(!tracker.Evaluate(high, settings, 70000).IsHighLoad, "alternating high metrics cannot share duration");
        settings.HighLoadAlertsEnabled = false;
        Check(!tracker.Evaluate(high, settings, 100000).IsHighLoad, "disabled alerts stay off");
        settings.HighLoadAlertsEnabled = true;
        high.MemoryPercent = float.NaN;
        Check(!tracker.Evaluate(high, settings, 110000).IsHighLoad, "invalid metric does not alert");
    }

    private static void VerifySettings()
    {
        var settings = new AppSettings { SamplingIntervalSeconds = 999, HighLoadThresholdPercent = 999,
            HighLoadDurationSeconds = -1, Layout = (WidgetLayout)100, Opacity = (WidgetOpacity)2 };
        settings.Normalize();
        Check(settings.SamplingIntervalSeconds == 5 && settings.HighLoadThresholdPercent == 100 &&
            settings.HighLoadDurationSeconds == 1 && settings.Layout == WidgetLayout.Horizontal &&
            settings.Opacity == WidgetOpacity.Opaque, "invalid settings clamp safely");
        var store = new SettingsStore(Path.Combine(Output, "settings-test.json"));
        settings.PowerSavingMode = true;
        settings.AlertPopupEnabled = true;
        store.Save(settings);
        var loaded = store.Load();
        Check(loaded.PowerSavingMode && loaded.AlertPopupEnabled && loaded.SamplingIntervalSeconds == 5,
            "new settings persist through actual SettingsStore");
        using (var dialog = new MonitoringOptionsDialog(new AppSettings()))
        {
            dialog.Show();
            Application.DoEvents();
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                typeof(MonitoringOptionsDialog).GetField("_scale", Hidden).SetValue(dialog, scale);
                typeof(MonitoringOptionsDialog).GetMethod("ApplyScaleAndLayout", Hidden).Invoke(dialog, null);
                Application.DoEvents();
                foreach (Control control in dialog.Controls)
                    Check(control.Right <= dialog.ClientSize.Width && control.Bottom <= dialog.ClientSize.Height,
                        $"settings {scale * 100:0}% {control.GetType().Name} stays inside window");
                using (var bitmap = new Bitmap(dialog.Width, dialog.Height))
                {
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(Output, $"settings-dialog-{scale * 100:0}.png"), ImageFormat.Png);
                }
            }
            dialog.Hide();
        }
        var cancelledSettings = new AppSettings();
        using (var cancelDialog = new MonitoringOptionsDialog(cancelledSettings))
        {
            SetProperty(Field(cancelDialog, "_powerSaving"), "Checked", true);
            cancelDialog.Show();
            ((Button)Field(cancelDialog, "_cancel")).PerformClick();
            Check(!cancelledSettings.PowerSavingMode, "settings Cancel does not mutate preferences");
        }
        var savedSettings = new AppSettings();
        using (var saveDialog = new MonitoringOptionsDialog(savedSettings))
        {
            SetProperty(Field(saveDialog, "_powerSaving"), "Checked", true);
            SetProperty(Field(saveDialog, "_threshold"), "Value", 85);
            SetProperty(Field(saveDialog, "_duration"), "Value", 45);
            var segment = (Control)Field(saveDialog, "_interval");
            segment.GetType().GetMethod("OnMouseDown", Hidden).Invoke(segment,
                new object[] { new MouseEventArgs(MouseButtons.Left, 1, segment.Width - 4, 8, 0) });
            saveDialog.Show();
            ((Button)Field(saveDialog, "_save")).PerformClick();
            Check(savedSettings.PowerSavingMode && savedSettings.HighLoadThresholdPercent == 85 &&
                savedSettings.HighLoadDurationSeconds == 45 && savedSettings.SamplingIntervalSeconds == 5,
                "settings Save commits toggles, numeric fields and selected interval");
        }
    }

    private static void VerifyGpuUnits()
    {
        var convert = typeof(GpuMemoryMonitor).GetMethod("TryConvertMegabytesToBytes", BindingFlags.Static | BindingFlags.NonPublic);
        object[] args = { 16384f, 0UL };
        Check((bool)convert.Invoke(null, args) && (ulong)args[1] == 16UL * 1024 * 1024 * 1024,
            "16384 MB sensor becomes 16 GB");
        args = new object[] { float.NaN, 0UL };
        Check(!(bool)convert.Invoke(null, args), "nonfinite GPU sensor rejected");
    }

    private static MetricSnapshot Sample() => new MetricSnapshot
    {
        CpuPercent = 23, MemoryPercent = 33, MemoryUsedBytes = (ulong)(15.5 * 1024 * 1024 * 1024),
        MemoryTotalBytes = 46UL * 1024 * 1024 * 1024, VramPercent = 11, VramAvailable = true,
        VramUsedBytes = (ulong)(1.8 * 1024 * 1024 * 1024), VramTotalBytes = 16UL * 1024 * 1024 * 1024,
        TopMemoryProcesses = new List<ProcessInfo> {
            new ProcessInfo { Name = "WorkBuddyAI", MemoryBytes = 947UL * 1024 * 1024 },
            new ProcessInfo { Name = "ChatGPT", MemoryBytes = 908UL * 1024 * 1024 },
            new ProcessInfo { Name = "Very long process name should be ellipsized", MemoryBytes = 557UL * 1024 * 1024 },
            new ProcessInfo { Name = "claude", MemoryBytes = 527UL * 1024 * 1024 },
            new ProcessInfo { Name = "Explorer", MemoryBytes = 221UL * 1024 * 1024 } }
    };

    private static void RenderLayouts()
    {
        var history = new float[60];
        for (int i = 0; i < history.Length; i++) history[i] = 12 + (float)(Math.Sin(i * 1.2) * 5 + i % 4 * 4);
        foreach (WidgetLayout layout in Enum.GetValues(typeof(WidgetLayout)))
        {
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                var logical = WidgetSizeHelper.GetSize(layout, 1f);
                var physical = WidgetSizeHelper.GetSize(layout, scale);
                using (var bitmap = new Bitmap(physical.Width, physical.Height))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    bitmap.SetResolution(scale * 96, scale * 96);
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.ScaleTransform(scale, scale);
                    WidgetRenderer.DrawLayout(graphics, new Rectangle(Point.Empty, logical), layout, Sample(), history, history, history);
                    bitmap.Save(Path.Combine(Output, $"{layout}-{scale * 100:0}.png"), ImageFormat.Png);
                }
                var hits = WidgetRenderer.GetMetricHitRects(new Rectangle(Point.Empty, logical), layout);
                Check(hits.Length == 4 && hits[3].Right <= logical.Width && hits[3].Bottom <= logical.Height,
                    $"{layout} {scale * 100:0}% render and clock hit bounds");
            }
        }
    }

    private static object Field(object obj, string name) => obj.GetType().GetField(name, Hidden).GetValue(obj);
    private static void SetProperty(object obj, string name, object value) => obj.GetType().GetProperty(name).SetValue(obj, value);

    private static void Pump(int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void VerifyLiveWiring()
    {
        var settings = new AppSettings { AlwaysOnTop = false, PowerSavingMode = false };
        var store = new SettingsStore(Path.Combine(Output, "widget-test-settings.json"));
        using (var monitor = new MonitorService())
        using (var widget = new WidgetForm(monitor, store, settings))
        {
            Check((int)Field(monitor, "_pollingIntervalMilliseconds") == 1000, "ordinary mode polls every second");
            settings.PowerSavingMode = true;
            foreach (int seconds in new[] { 1, 3, 5 })
            {
                settings.SamplingIntervalSeconds = seconds;
                widget.ApplyMonitoringSettings();
                Check((int)Field(monitor, "_pollingIntervalMilliseconds") == seconds * 1000,
                    $"power saving selected {seconds}s applied to real service");
            }
            monitor.Start();
            Check(Field(monitor, "_processTimer") == null, "hidden widget creates no process scanner");
            widget.ShowWidget();
            widget.ApplyLayout(WidgetLayout.Expanded);
            Pump(600);
            Check(Field(monitor, "_processTimer") != null, "visible expanded layout enables actual process scanner");
            widget.ApplyLayout(WidgetLayout.Vertical);
            Check(Field(monitor, "_processTimer") == null, "vertical layout stops process scanner");
            widget.ApplyLayout(WidgetLayout.Horizontal);
            typeof(WidgetForm).GetMethod("OnDataUpdated", Hidden).Invoke(widget, new object[] { Sample() });
            Pump(150);
            string ram = (string)typeof(WidgetForm).GetMethod("BuildMetricToolTip", Hidden).Invoke(widget, new object[] { 1 });
            string vram = (string)typeof(WidgetForm).GetMethod("BuildMetricToolTip", Hidden).Invoke(widget, new object[] { 2 });
            Check(ram.Contains("15.50 GB") && vram.Contains("16.00 GB"), "hover detail uses cached snapshot bytes");
            Check(widget.ContextMenuStrip.Items.ContainsKey("unused") == false &&
                HasOptions(widget.ContextMenuStrip), "widget menu exposes monitoring settings");
            widget.ApplyLayout(WidgetLayout.Expanded);
            widget.HideWidget();
            Check(Field(monitor, "_processTimer") == null && !((System.Windows.Forms.Timer)Field(widget, "_clockTimer")).Enabled,
                "hiding stops process scan and clock redraw timer");
            monitor.Stop();
            Check(Field(monitor, "_timer") == null, "stopping clears metric timer");
        }
    }

    private static bool HasOptions(ContextMenuStrip menu)
    {
        foreach (ToolStripItem item in menu.Items) if (item.Text.Contains("监控设置")) return true;
        return false;
    }

    private static void VerifyTrayWiring()
    {
        var store = new SettingsStore(Path.Combine(Output, "tray-test-settings.json"));
        store.Save(new AppSettings { Visible = false, AlwaysOnTop = false,
            HighLoadThresholdPercent = 1, HighLoadDurationSeconds = 1, AlertPopupEnabled = false });
        using (var context = new TrayApplicationContext(store))
        {
            try
            {
                Pump(3400);
                var tray = (NotifyIcon)Field(context, "_notifyIcon");
                var widget = (WidgetForm)Field(context, "_widgetForm");
                Check(!widget.Visible && tray.Text.Contains("RAM ") && !tray.Text.Contains("RAM --"),
                    "hidden-at-startup tray receives real metrics on UI thread");
                Check(HasOptions(tray.ContextMenuStrip), "tray menu exposes same monitoring settings");
                Check(ReferenceEquals(tray.Icon, Field(context, "_alertIcon")) &&
                    (int)Field(widget, "_activeAlertMetrics") != 0,
                    "sustained real high load reaches tray and widget alert color");
                var actual = (MetricSnapshot)Field(widget, "_snapshot");
                Console.WriteLine($"Live metrics CPU={actual.CpuPercent:0.0}% RAM={actual.MemoryPercent:0.0}% VRAM={actual.VramPercent:0.0}% used={actual.VramUsedBytes} total={actual.VramTotalBytes}");
            }
            finally
            {
                typeof(TrayApplicationContext).GetMethod("ExitThreadCore", Hidden).Invoke(context, null);
            }
        }
    }
}
