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
            VerifyProcessCpuTracker();
            VerifyProcessVramCounter();
            VerifyApplicationMemory();
            RenderLayouts();
            RenderLinkedRankings();
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

    private static void VerifyProcessCpuTracker()
    {
        var assembly = typeof(MonitorService).Assembly;
        var sampleType = assembly.GetType("SysFloat.Monitoring.ProcessCpuSample");
        var trackerType = assembly.GetType("SysFloat.Monitoring.ProcessCpuTracker");
        var tracker = Activator.CreateInstance(trackerType, true);
        var update = trackerType.GetMethod("Update");
        object Result(long timestamp, long cpuTicksA, long cpuTicksB, long startA = 2000)
        {
            var samples = Array.CreateInstance(sampleType, 2);
            foreach (int i in new[] { 0, 1 })
            {
                var sample = Activator.CreateInstance(sampleType, true);
                SetProperty(sample, "ProcessId", i + 7);
                SetProperty(sample, "Name", i == 0 ? "CPU A" : "CPU B");
                SetProperty(sample, "StartTimeTicks", i == 0 ? startA : 2000L);
                SetProperty(sample, "TotalProcessorTimeTicks", i == 0 ? cpuTicksA : cpuTicksB);
                SetProperty(sample, "MonotonicTimestamp", timestamp);
                samples.SetValue(sample, i);
            }
            return update.Invoke(tracker, new object[] { samples, 0, 4, 1000L });
        }
        object first = Result(1000, 10000000, 10000000);
        Check(!(bool)first.GetType().GetProperty("Available").GetValue(first), "CPU baseline has no fabricated ranking");
        object second = Result(3000, 20000000, 30000000);
        var rows = (List<ProcessInfo>)second.GetType().GetProperty("Processes").GetValue(second);
        Check(rows.Count == 2 && rows[0].ProcessId == 8 && Math.Abs(rows[0].CpuPercent - 25) < 0.001 &&
            Math.Abs(rows[1].CpuPercent - 12.5) < 0.001, "CPU deltas normalized over elapsed time and logical cores");
        object reusedPid = Result(5000, 90000000, 30000000, 4000);
        var reusedRows = (List<ProcessInfo>)reusedPid.GetType().GetProperty("Processes").GetValue(reusedPid);
        Check(reusedRows.TrueForAll(row => row.ProcessId != 7), "PID reuse never inherits previous CPU baseline");
        object stale = Result(70000, 100000000, 40000000, 4000);
        Check(!(bool)stale.GetType().GetProperty("Available").GetValue(stale), "stale CPU sampling gap resets delta baseline");
    }

    private static void VerifyProcessVramCounter()
    {
        var type = typeof(MonitorService).Assembly.GetType("SysFloat.Monitoring.ProcessVramMonitor");
        var parse = type.GetMethod("TryParseProcessId", BindingFlags.Static | BindingFlags.NonPublic);
        object[] args = { "pid_1234_luid_0x00000000_0x0000aab5_phys_0", 0 };
        Check((bool)parse.Invoke(null, args) && (int)args[1] == 1234, "VRAM instance PID parsed correctly");
        args = new object[] { "_Total", 0 };
        Check(!(bool)parse.Invoke(null, args), "VRAM total instance is not assigned to a fake process");
        using (var reader = (IDisposable)Activator.CreateInstance(type, true))
        {
            var read = type.GetMethod("ReadUsageByProcess");
            type.GetMethod("SetGeneration").Invoke(reader, new object[] { 1, true });
            object sample = read.Invoke(reader, new object[] { 1 });
            bool available = (bool)sample.GetType().GetProperty("Available").GetValue(sample);
            var values = (Dictionary<int, ulong>)sample.GetType().GetProperty("UsageByProcessId").GetValue(sample);
            string status = (string)sample.GetType().GetProperty("Status").GetValue(sample);
            Check(available && values.Count > 0, "native PDH query returns real local per-process dedicated VRAM: " + status);
            type.GetMethod("Reset").Invoke(reader, null);
            Check((IntPtr)Field(reader, "_query") == IntPtr.Zero, "leaving VRAM closes native PDH handle");
            reader.Dispose();
            object after = read.Invoke(reader, new object[] { 1 });
            Check(!(bool)after.GetType().GetProperty("Available").GetValue(after), "disposed VRAM reader cannot reopen a query");
        }
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

    private static void RenderLinkedRankings()
    {
        var history = new float[60];
        for (int i = 0; i < history.Length; i++) history[i] = 15 + (float)Math.Sin(i) * 4;
        foreach (ProcessMetric metric in Enum.GetValues(typeof(ProcessMetric)))
        {
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            {
                var snapshot = Sample();
                snapshot.RankingMetric = metric;
                snapshot.CpuProcessesAvailable = true;
                snapshot.VramProcessesAvailable = true;
                snapshot.TopCpuProcesses = new List<ProcessInfo> {
                    new ProcessInfo { Name = "Example CPU worker", CpuPercent = 23.5f },
                    new ProcessInfo { Name = "Browser", CpuPercent = 8.2f },
                    new ProcessInfo { Name = "Editor", CpuPercent = 3.1f } };
                snapshot.TopVramProcesses = new List<ProcessInfo> {
                    new ProcessInfo { Name = "Example GPU app", VramBytes = 512UL * 1024 * 1024 },
                    new ProcessInfo { Name = "Browser", VramBytes = 256UL * 1024 * 1024 },
                    new ProcessInfo { Name = "Desktop compositor", VramBytes = 128UL * 1024 * 1024 } };
                var logical = WidgetSizeHelper.GetSize(WidgetLayout.Expanded, 1f);
                var physical = WidgetSizeHelper.GetSize(WidgetLayout.Expanded, scale);
                using (var bitmap = new Bitmap(physical.Width, physical.Height))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    bitmap.SetResolution(scale * 96, scale * 96);
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.ScaleTransform(scale, scale);
                    WidgetRenderer.DrawLayout(graphics, new Rectangle(Point.Empty, logical), WidgetLayout.Expanded,
                        snapshot, history, history, history, selectedMetric: metric);
                    bitmap.Save(Path.Combine(Output, $"Expanded-{metric}-{scale * 100:0}.png"), ImageFormat.Png);
                }
            }
        }
        Check(WidgetSizeHelper.GetSize(WidgetLayout.Vertical, 1f).Width <= 96,
            "vertical widget reduced side margins to at most 96 logical pixels");
    }

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
    private static object Property(object obj, string name) => obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(obj);
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
            settings.PowerSavingMode = false;
            widget.ApplyMonitoringSettings();
            Pump(600);
            Check(Field(monitor, "_processTimer") != null, "visible expanded layout enables actual process scanner");
            Check(widget.SelectedProcessMetric == ProcessMetric.Memory, "expanded process ranking defaults to memory");
            PumpUntil(() => ((MetricSnapshot)Field(widget, "_snapshot")).TopMemoryProcesses.Count > 0 &&
                ((MetricSnapshot)Field(widget, "_snapshot")).CpuPercent >= 0, 3500,
                "real grouped application memory reaches the visible widget");
            var memorySnapshot = (MetricSnapshot)Field(widget, "_snapshot");
            Check(memorySnapshot.TopMemoryProcesses.Exists(row => row.ProcessCount > 1),
                "group process counts survive service snapshot cloning");
            var actualSize = WidgetSizeHelper.GetSize(WidgetLayout.Expanded, 1.5f);
            using (var bitmap = new Bitmap(actualSize.Width, actualSize.Height))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                graphics.ScaleTransform(1.5f, 1.5f);
                WidgetRenderer.DrawLayout(graphics, new Rectangle(Point.Empty, WidgetSizeHelper.GetSize(WidgetLayout.Expanded, 1f)),
                    WidgetLayout.Expanded, memorySnapshot, selectedMetric: ProcessMetric.Memory);
                bitmap.Save(Path.Combine(Output, "Expanded-ApplicationMemory-Live.png"), ImageFormat.Png);
            }
            var chronological = typeof(WidgetForm).GetMethod("GetChronologicalHistory", Hidden);
            Check(ReferenceEquals(chronological.Invoke(widget, new[] { Field(widget, "_cpuHistory") }),
                chronological.Invoke(widget, new[] { Field(widget, "_cpuHistory") })),
                "chart redraw reuses its chronological buffer instead of allocating each paint");
            ClickMetric(widget, 0);
            Check(widget.SelectedProcessMetric == ProcessMetric.Cpu, "actual CPU row click selects CPU ranking");
            PumpUntil(() => ((MetricSnapshot)Field(widget, "_snapshot")).RankingMetric == ProcessMetric.Cpu &&
                ((MetricSnapshot)Field(widget, "_snapshot")).CpuProcessesAvailable, 8500, "real CPU rank completes baseline and second sample");
            var cpuSnapshot = (MetricSnapshot)Field(widget, "_snapshot");
            Check(cpuSnapshot.TopCpuProcesses.Count > 0 && SortedCpu(cpuSnapshot.TopCpuProcesses),
                "real CPU ranking has valid percentages sorted by CPU, not RAM");
            settings.Locked = true;
            ClickMetric(widget, 2);
            Check(widget.SelectedProcessMetric == ProcessMetric.Vram, "locked window still allows VRAM row selection");
            PumpUntil(() => ((MetricSnapshot)Field(widget, "_snapshot")).RankingMetric == ProcessMetric.Vram, 4000,
                "real VRAM selection publishes its own metric snapshot");
            var gpuSnapshot = (MetricSnapshot)Field(widget, "_snapshot");
            Check(gpuSnapshot.VramProcessesAvailable && gpuSnapshot.TopVramProcesses.Count > 0,
                "local Windows dedicated GPU counters return actual process VRAM");
            Console.WriteLine("VRAM top: " + gpuSnapshot.TopVramProcesses[0].Name + " " + gpuSnapshot.TopVramProcesses[0].VramBytes + " bytes");
            ClickMetric(widget, 1);
            Check(widget.SelectedProcessMetric == ProcessMetric.Memory, "RAM click restores memory ranking");
            Check((IntPtr)Field(Field(monitor, "_processVramMonitor"), "_query") == IntPtr.Zero,
                "switching VRAM back to memory closes its native query");
            settings.Locked = false;
            DragMetricWithoutSwitch(widget);
            Check(widget.SelectedProcessMetric == ProcessMetric.Memory, "dragging a CPU row does not switch current ranking");
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
            Check(!((System.Windows.Forms.Timer)Field(widget, "_topMostTimer")).Enabled,
                "hidden widget also stops top-most timer wakeups");
            monitor.Stop();
            Check(Field(monitor, "_timer") == null, "stopping clears metric timer");
        }
    }

    private static bool HasOptions(ContextMenuStrip menu)
    {
        foreach (ToolStripItem item in menu.Items) if (item.Text.Contains("监控设置")) return true;
        return false;
    }

    private static void ClickMetric(WidgetForm widget, int metric)
    {
        var rects = (Rectangle[])Field(widget, "_metricHitRects");
        var area = rects[metric];
        var location = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var args = new MouseEventArgs(MouseButtons.Left, 1, location.X, location.Y, 0);
        typeof(WidgetForm).GetMethod("OnMouseDown", Hidden).Invoke(widget, new object[] { args });
        typeof(WidgetForm).GetMethod("OnMouseUp", Hidden).Invoke(widget, new object[] { args });
        Application.DoEvents();
    }

    private static void DragMetricWithoutSwitch(WidgetForm widget)
    {
        var area = ((Rectangle[])Field(widget, "_metricHitRects"))[0];
        var point = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var pressed = new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0);
        var moved = new MouseEventArgs(MouseButtons.Left, 1, point.X + 30, point.Y + 20, 0);
        typeof(WidgetForm).GetMethod("OnMouseDown", Hidden).Invoke(widget, new object[] { pressed });
        typeof(WidgetForm).GetMethod("OnMouseMove", Hidden).Invoke(widget, new object[] { moved });
        typeof(WidgetForm).GetMethod("OnMouseUp", Hidden).Invoke(widget, new object[] { moved });
    }

    private static void PumpUntil(Func<bool> predicate, int timeoutMilliseconds, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate() && timer.ElapsedMilliseconds < timeoutMilliseconds) Pump(50);
        Check(predicate(), message);
    }

    private static bool SortedCpu(List<ProcessInfo> rows)
    {
        float previous = 101;
        foreach (var row in rows)
        {
            if (float.IsNaN(row.CpuPercent) || row.CpuPercent < 0 || row.CpuPercent > previous) return false;
            previous = row.CpuPercent;
        }
        return true;
    }

    private static void VerifyApplicationMemory()
    {
        var assembly = typeof(MonitorService).Assembly;
        var sampleType = assembly.GetType("SysFloat.Monitoring.ProcessMemoryProcessSample");
        var groupType = assembly.GetType("SysFloat.Monitoring.ApplicationMemoryGrouper");
        var fixture = Array.CreateInstance(sampleType, 9);
        object New(int pid, int parent, string path, ulong start, bool readable, ulong bytes) =>
            Activator.CreateInstance(sampleType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { pid, parent, Path.GetFileName(path), path, start, readable, bytes }, null);
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        fixture.SetValue(New(1, 0, shell, 1, true, 50), 0);
        fixture.SetValue(New(10, 1, @"C:\Apps\A\editor.exe", 10, true, 1000), 1);
        fixture.SetValue(New(11, 10, @"C:\Apps\A\editor.exe", 11, true, 2000), 2);
        fixture.SetValue(New(12, 11, @"C:\Tools\helper.exe", 12, true, 3000), 3);
        fixture.SetValue(New(13, 10, @"C:\Tools\protected.exe", 13, false, 0), 4);
        fixture.SetValue(New(20, 10, @"C:\Apps\Viewer\viewer.exe", 20, true, 500), 5);
        fixture.SetValue(New(30, 1, @"D:\Another\editor.exe", 30, true, 700), 6);
        fixture.SetValue(New(40, 1, @"C:\Apps\NewParent\new.exe", 90, true, 900), 7);
        fixture.SetValue(New(41, 40, @"C:\OldChild\child.exe", 89, true, 400), 8);
        var group = groupType.GetMethod("GroupSamples", BindingFlags.Static | BindingFlags.NonPublic);
        var rows = (List<ProcessInfo>)group.Invoke(null, new object[] { fixture, new[] { 1, 10, 20, 30, 40 }, null });
        var editor = rows.Find(row => row.ProcessId == 10);
        Check(editor.MemoryBytes == 6000 && editor.ProcessCount == 4 && editor.MemoryIsPartial,
            "application memory combines mixed executable descendants and marks unreadable member");
        Check(rows.Exists(row => row.ProcessId == 20 && row.MemoryBytes == 500 && row.ProcessCount == 1),
            "independent child GUI application stays separate");
        Check(rows.Exists(row => row.ProcessId == 30 && row.MemoryBytes == 700),
            "same executable name in another directory stays separate");
        Check(rows.Exists(row => row.ProcessId == 41 && row.MemoryBytes == 400),
            "reused parent PID does not absorb an older child");
        Check(rows.Exists(row => row.ProcessId == 1 && row.MemoryBytes == 50),
            "desktop shell does not absorb launched applications");

        var readerType = assembly.GetType("SysFloat.Monitoring.ProcessMemoryReader");
        var reader = Activator.CreateInstance(readerType, true);
        var reference = PrivateWorkingSetReference.Read();
        int selfPid = Process.GetCurrentProcess().Id;
        var reading = readerType.GetMethod("Read", Hidden).Invoke(reader, new object[] { selfPid });
        ulong actual = (ulong)Property(reading, "PrivateWorkingSetBytes");
        Check((bool)Property(reading, "PrivateWorkingSetAvailable") && reference.ContainsKey(selfPid) &&
            Math.Abs((double)actual - reference[selfPid]) < 4 * 1024 * 1024,
            $"native private working set agrees with independent PDH: API={actual} PDH={reference.GetValueOrDefault(selfPid)}");

        var collectorType = assembly.GetType("SysFloat.Monitoring.ProcessMemoryCollector");
        var collector = Activator.CreateInstance(collectorType, true);
        var collect = collectorType.GetMethod("Collect", Hidden);
        var coldWatch = Stopwatch.StartNew();
        var collected = collect.Invoke(collector, new object[] { null });
        coldWatch.Stop();
        Check((bool)Property(collected, "Available"), "live application private-memory collector is available");
        var liveRows = (List<ProcessInfo>)Property(collected, "Processes");
        Check(liveRows.Exists(row => row.ProcessCount > 1 && row.MemoryBytes > 0), "live memory ranking contains aggregated applications");
        foreach (var row in liveRows)
            Console.WriteLine($"Application memory: {row.Name} ({row.ProcessCount}) {row.MemoryBytes / 1048576.0:0.0} MiB partial={row.MemoryIsPartial}");
        using (var current = Process.GetCurrentProcess())
        {
            current.Refresh();
            int handlesBefore = current.HandleCount;
            var hotWatch = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) collected = collect.Invoke(collector, new object[] { null });
            hotWatch.Stop();
            current.Refresh();
            Check(current.HandleCount <= handlesBefore + 4, "repeated memory collection does not leak handles");
            Console.WriteLine($"Memory collector: cold={coldWatch.Elapsed.TotalMilliseconds:0.0}ms cachedAvg={hotWatch.Elapsed.TotalMilliseconds / 5:0.0}ms handles={handlesBefore}->{current.HandleCount}");
        }
        var aborted = collect.Invoke(collector, new object[] { new Func<bool>(() => false) });
        Check((bool)Property(aborted, "Aborted"), "hidden/switched memory collection cancels before native work");
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
