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
            RenderLayouts();
            RenderTranslucentEvidence();
            VerifyEdgeAutoHideLogic();
            VerifyStartupRegistration();
            RenderLinkedRankings();
            VerifyLiveWiring();
            VerifySliverEdgeAndIdleRepaints();
            VerifyVerticalClockSingleLine();
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
        var scaleSettings = new AppSettings { ScalePercent = 999 };
        scaleSettings.Normalize();
        Check(scaleSettings.ScalePercent == 200, "缩放百分比 999 规范化后夹紧到 200");
        scaleSettings.ScalePercent = 10;
        scaleSettings.Normalize();
        Check(scaleSettings.ScalePercent == 50, "缩放百分比 10 规范化后夹紧到 50");
        scaleSettings.ScalePercent = 137;
        scaleSettings.Normalize();
        Check(scaleSettings.ScalePercent == 135, "缩放百分比 137 规范化为 5 的倍数 135");
        var store = new SettingsStore(Path.Combine(Output, "settings-test.json"));
        settings.PowerSavingMode = true;
        settings.AlertPopupEnabled = true;
        store.Save(settings);
        var loaded = store.Load();
        Check(loaded.PowerSavingMode && loaded.AlertPopupEnabled && loaded.SamplingIntervalSeconds == 5,
            "new settings persist through actual SettingsStore");
        store.Save(new AppSettings { ScalePercent = 125, SnapToEdges = false, EdgeAutoHide = false, DockedEdge = DockEdge.Right });
        var scaledLoaded = store.Load();
        Check(scaledLoaded.ScalePercent == 125 && !scaledLoaded.SnapToEdges,
            "用户缩放与边缘吸附设置经实际 SettingsStore 保存加载后原样保留");
        Check(!scaledLoaded.EdgeAutoHide && scaledLoaded.DockedEdge == DockEdge.Right,
            "贴边隐藏开关与停靠边经实际 SettingsStore 保存加载后原样保留");
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
        Check(WidgetSizeHelper.GetSize(WidgetLayout.Mini, 1f) == new Size(204, 44), "迷你版逻辑尺寸为 204x44");
        Check(WidgetSizeHelper.GetSize(WidgetLayout.Mini, 1f, 1.5f).Width == (int)(204 * 1.5f),
            "用户缩放重载按缩放倍率放大迷你版宽度");
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
                int expectedHits = layout == WidgetLayout.Mini ? 3 : 4;
                bool hitsInside = hits.Length == expectedHits;
                for (int i = 0; i < hits.Length; i++)
                    hitsInside &= hits[i].Left >= 0 && hits[i].Top >= 0 && hits[i].Right <= logical.Width && hits[i].Bottom <= logical.Height;
                Check(hitsInside, $"{layout} {scale * 100:0}% 渲染含 {expectedHits} 个命中区且全部位于边界内");
            }
            foreach (float userScale in new[] { 0.75f, 1.25f, 1.5f })
            {
                var logical = WidgetSizeHelper.GetSize(layout, 1f);
                var physical = WidgetSizeHelper.GetSize(layout, 1f, userScale);
                using (var bitmap = new Bitmap(physical.Width, physical.Height))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    bitmap.SetResolution(userScale * 96, userScale * 96);
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.ScaleTransform(userScale, userScale);
                    WidgetRenderer.DrawLayout(graphics, new Rectangle(Point.Empty, logical), layout, Sample(), history, history, history);
                    bitmap.Save(Path.Combine(Output, $"{layout}-userscale-{userScale * 100:0}.png"), ImageFormat.Png);
                }
            }
        }
    }

    private static void VerifyEdgeAutoHideLogic()
    {
        Check(new AppSettings().EdgeAutoHide && new AppSettings().DockedEdge == DockEdge.None,
            "贴边隐藏默认开启且默认未停靠");
        var wa = new Rectangle(0, 0, 1920, 1040);
        var detect = typeof(WidgetForm).GetMethod("DetectDockEdge", BindingFlags.Static | BindingFlags.NonPublic);
        var hiddenAt = typeof(WidgetForm).GetMethod("GetHiddenPosition", BindingFlags.Static | BindingFlags.NonPublic);
        Check((DockEdge)detect.Invoke(null, new object[] { new Rectangle(0, 100, 200, 60), wa }) == DockEdge.Left,
            "贴边检测识别左边缘");
        Check((DockEdge)detect.Invoke(null, new object[] { new Rectangle(1720, 100, 200, 60), wa }) == DockEdge.Right,
            "贴边检测识别右边缘");
        Check((DockEdge)detect.Invoke(null, new object[] { new Rectangle(500, 0, 200, 60), wa }) == DockEdge.Top,
            "贴边检测识别上边缘");
        Check((DockEdge)detect.Invoke(null, new object[] { new Rectangle(500, 980, 200, 60), wa }) == DockEdge.Bottom,
            "贴边检测识别下边缘");
        Check((DockEdge)detect.Invoke(null, new object[] { new Rectangle(500, 300, 200, 60), wa }) == DockEdge.None,
            "屏幕中部不判定贴边");
        Check((Point)hiddenAt.Invoke(null, new object[] { DockEdge.Left, new Rectangle(0, 100, 200, 60), wa }) == new Point(-(200 - 16), 100),
            "左停靠收起位置仅留 16px 小边");
        Check((Point)hiddenAt.Invoke(null, new object[] { DockEdge.Right, new Rectangle(1720, 100, 200, 60), wa }) == new Point(1920 - 16, 100),
            "右停靠收起位置仅留 16px 小边");
        Check((Point)hiddenAt.Invoke(null, new object[] { DockEdge.Top, new Rectangle(500, 0, 200, 60), wa }) == new Point(500, -(60 - 16)),
            "上停靠收起位置仅留 16px 小边");
        var dockLayoutFor = typeof(WidgetForm).GetMethod("GetDockLayout", BindingFlags.Static | BindingFlags.NonPublic);
        Check((WidgetLayout)dockLayoutFor.Invoke(null, new object[] { DockEdge.Left }) == WidgetLayout.Vertical &&
            (WidgetLayout)dockLayoutFor.Invoke(null, new object[] { DockEdge.Right }) == WidgetLayout.Vertical,
            "左右停靠使用竖版形态");
        Check((WidgetLayout)dockLayoutFor.Invoke(null, new object[] { DockEdge.Top }) == WidgetLayout.Horizontal &&
            (WidgetLayout)dockLayoutFor.Invoke(null, new object[] { DockEdge.Bottom }) == WidgetLayout.Horizontal,
            "上下停靠使用横版形态");
    }

    private static void VerifyStartupRegistration()
    {
        bool original = StartupRegistration.IsEnabled();
        try
        {
            StartupRegistration.SetEnabled(true);
            Check(StartupRegistration.IsEnabled(), "开机自启写入 HKCU Run 项后可查询到启用状态");
            StartupRegistration.SetEnabled(false);
            Check(!StartupRegistration.IsEnabled(), "关闭开机自启后 HKCU Run 项被移除");
        }
        finally
        {
            StartupRegistration.SetEnabled(original);
        }
    }

    private static void RenderTranslucentEvidence()
    {
        var history = new float[60];
        for (int i = 0; i < history.Length; i++) history[i] = 12 + (float)(Math.Sin(i * 1.2) * 5 + i % 4 * 4);
        var logical = WidgetSizeHelper.GetSize(WidgetLayout.Horizontal, 1f);
        foreach (int alpha in new[] { 191, 150 })
        {
            foreach (var backdrop in new[] { Tuple.Create(Color.White, "white"), Tuple.Create(Color.FromArgb(28, 31, 40), "dark") })
            {
                using (var bitmap = new Bitmap(logical.Width + 60, logical.Height + 60))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    using (var fill = new SolidBrush(backdrop.Item1)) graphics.FillRectangle(fill, 0, 0, bitmap.Width, bitmap.Height);
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    WidgetRenderer.DrawLayout(graphics, new Rectangle(30, 30, logical.Width, logical.Height),
                        WidgetLayout.Horizontal, Sample(), history, history, history, backgroundAlpha: alpha);
                    bitmap.Save(Path.Combine(Output, $"Horizontal-alpha{alpha}-{backdrop.Item2}.png"), ImageFormat.Png);
                }
            }
        }
        Check(File.Exists(Path.Combine(Output, "Horizontal-alpha191-white.png")) &&
            File.Exists(Path.Combine(Output, "Horizontal-alpha150-dark.png")),
            "半透明样式在明暗背景下的渲染证据已生成");
    }

    private static object Field(object obj, string name) => obj.GetType().GetField(name, Hidden).GetValue(obj);
    private static void SetProperty(object obj, string name, object value) => obj.GetType().GetProperty(name).SetValue(obj, value);
    // 子类实例上读写基类私有字段：Type.GetField 不回溯基类私有成员，须显式给声明类型。
    private static object FieldOn(object obj, Type declared, string name) => declared.GetField(name, Hidden).GetValue(obj);
    private static void SetFieldOn(object obj, Type declared, string name, object value) => declared.GetField(name, Hidden).SetValue(obj, value);

    private sealed class InvalidateCountingWidget : WidgetForm
    {
        public int InvalidateCount;
        public InvalidateCountingWidget(MonitorService monitor, SettingsStore store, AppSettings settings)
            : base(monitor, store, settings) { }
        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            InvalidateCount++;
            base.OnInvalidated(e);
        }
    }

    private static void VerifySliverEdgeAndIdleRepaints()
    {
        var store = new SettingsStore(Path.Combine(Output, "sliver-test-settings.json"));
        using (var monitor = new MonitorService())
        using (var widget = new InvalidateCountingWidget(monitor, store, new AppSettings()))
        {
            var drawSliver = typeof(WidgetForm).GetMethod("DrawSliverEdge", Hidden);
            var bounds = new Rectangle(0, 0, 200, 60);

            SetFieldOn(widget, typeof(WidgetForm), "_dockEdge", DockEdge.Right);
            SetFieldOn(widget, typeof(WidgetForm), "_isEdgeHidden", false);
            SetFieldOn(widget, typeof(WidgetForm), "_activeAlertMetrics", 0);
            using (var bmp = new Bitmap(200, 60))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                drawSliver.Invoke(widget, new object[] { g, bounds, 1f });
                var thin = bmp.GetPixel(1, 30);
                Check(thin.B > 100 && thin.B > thin.R + 60 && thin.G < thin.B,
                    $"弹出停靠态在内侧边缘绘制细蓝线 (R={thin.R} G={thin.G} B={thin.B})");

                SetFieldOn(widget, typeof(WidgetForm), "_dockEdge", DockEdge.None);
                g.Clear(Color.Black);
                drawSliver.Invoke(widget, new object[] { g, bounds, 1f });
                var none = bmp.GetPixel(1, 30);
                Check(none.R == 0 && none.G == 0 && none.B == 0,
                    "未停靠时不绘制任何高亮边");

                SetFieldOn(widget, typeof(WidgetForm), "_dockEdge", DockEdge.Right);
                SetFieldOn(widget, typeof(WidgetForm), "_isEdgeHidden", true);
                g.Clear(Color.Black);
                drawSliver.Invoke(widget, new object[] { g, bounds, 1f });
                var strong = bmp.GetPixel(5, 30);
                Check(strong.B > 180 && strong.R < 90 && strong.G < strong.B,
                    $"收起态整条留边高亮蓝 (R={strong.R} G={strong.G} B={strong.B})");
                var mid = bmp.GetPixel(10, 30);
                Check(mid.B > 180 && mid.R < 90,
                    "收起态高亮贯穿整条留边而非仅端点");
                bmp.Save(Path.Combine(Output, "sliver-edge-hidden.png"), ImageFormat.Png);

                SetFieldOn(widget, typeof(WidgetForm), "_activeAlertMetrics", 1);
                g.Clear(Color.Black);
                drawSliver.Invoke(widget, new object[] { g, bounds, 1f });
                var alert = bmp.GetPixel(5, 30);
                Check(alert.R > 180 && alert.R > alert.B + 60,
                    $"高负载告警时留边高亮转橙 (R={alert.R} G={alert.G} B={alert.B})");
                SetFieldOn(widget, typeof(WidgetForm), "_activeAlertMetrics", 0);
            }

            widget.ShowWidget();
            widget.HideWidget(); // 计数只关心显式调用，避免展示路径的初始化重绘干扰
            widget.ShowWidget();
            var dockedBounds = new Rectangle(widget.Left, widget.Top, widget.Width, widget.Height);
            SetFieldOn(widget, typeof(WidgetForm), "_dockEdge", DockEdge.Right);
            SetFieldOn(widget, typeof(WidgetForm), "_dockedBounds", dockedBounds);
            SetFieldOn(widget, typeof(WidgetForm), "_isEdgeHidden", true);
            int beforeHidden = widget.InvalidateCount;
            SetFieldOn(widget, typeof(WidgetForm), "_pendingSnapshot", Sample());
            typeof(WidgetForm).GetMethod("ProcessPendingSnapshot", Hidden).Invoke(widget, null);
            Check(widget.InvalidateCount == beforeHidden,
                "收起在边缘时数据快照不触发整帧重绘");

            SetFieldOn(widget, typeof(WidgetForm), "_isEdgeHidden", false);
            int beforeShown = widget.InvalidateCount;
            SetFieldOn(widget, typeof(WidgetForm), "_pendingSnapshot", Sample());
            typeof(WidgetForm).GetMethod("ProcessPendingSnapshot", Hidden).Invoke(widget, null);
            Check(widget.InvalidateCount == beforeShown + 1,
                "弹出状态数据快照仍触发重绘");

            int beforeHide = widget.InvalidateCount;
            typeof(WidgetForm).GetMethod("EnterHiddenState", Hidden).Invoke(widget, new object[] { false });
            Check(widget.InvalidateCount == beforeHide + 1,
                "切换到收起状态主动补一帧（透明模式留边高亮即时出现）");
            int beforeReveal = widget.InvalidateCount;
            typeof(WidgetForm).GetMethod("ExitHiddenState", Hidden).Invoke(widget, new object[] { false });
            Check(widget.InvalidateCount == beforeReveal + 1,
                "弹出收起状态主动补一帧（数据即时刷新）");
        }
    }

    private static void VerifyVerticalClockSingleLine()
    {
        // 扫描时钟区时间数字以下的文字带：合并成功应恰好一条（时区+日期单行），
        // 若实现回退成两行会扫描到两条带。
        var logical = WidgetSizeHelper.GetSize(WidgetLayout.Vertical, 1f);
        using (var bitmap = new Bitmap(logical.Width, logical.Height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            WidgetRenderer.DrawLayout(graphics, new Rectangle(Point.Empty, logical), WidgetLayout.Vertical,
                Sample(), new float[60], new float[60], new float[60]);
            int clockTop = logical.Height - 84;
            int bands = 0;
            bool inBand = false;
            for (int y = clockTop + 38; y < logical.Height - 4; y++)
            {
                int bg = BackgroundGray(bitmap, y);
                bool hasText = false;
                for (int x = 10; x < logical.Width - 8 && !hasText; x++)
                {
                    var p = bitmap.GetPixel(x, y);
                    if (Math.Abs((p.R + p.G + p.B) / 3 - bg) > 35) hasText = true;
                }
                if (hasText && !inBand) { bands++; inBand = true; }
                else if (!hasText) inBand = false;
            }
            Check(bands == 1, $"竖版时钟时区与日期合并为单行 (检测到 {bands} 条文字带)");
        }
    }

    private static int BackgroundGray(Bitmap bitmap, int y)
    {
        int sum = 0;
        for (int x = 1; x <= 6; x++)
        {
            var p = bitmap.GetPixel(x, y);
            sum += (p.R + p.G + p.B) / 3;
        }
        return sum / 6;
    }

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
            // 屏幕可能小于放大后的窗体（如高 DPI 的窄 RDP 屏幕），按当前工作区选一个放得下的档位，
            // 并先移到左侧，避免窗体长大后跨越到相邻显示器触发系统 DPI 重设尺寸。
            var scaleWa = Screen.FromControl(widget).WorkingArea;
            widget.Left = scaleWa.Left + 20;
            widget.Top = scaleWa.Top + 20;
            float dpiNow = DpiHelper.GetDpiScale(widget);
            int fitScale = 150;
            while (fitScale > 100 &&
                WidgetSizeHelper.GetSize(widget.GetCurrentLayout(), dpiNow, fitScale / 100f).Width > scaleWa.Width - 60)
                fitScale -= 25;
            widget.ApplyScale(fitScale);
            var expectedFit = WidgetSizeHelper.GetSize(widget.GetCurrentLayout(), DpiHelper.GetDpiScale(widget), fitScale / 100f);
            Check(widget.Width == expectedFit.Width,
                $"应用 {fitScale}% 用户缩放后窗体宽度按当前布局、DPI 与缩放立即调整 (actual={widget.Width} expected={expectedFit.Width})");
            Check(widget.Left >= scaleWa.Left && widget.Top >= scaleWa.Top &&
                widget.Right <= scaleWa.Right && widget.Bottom <= scaleWa.Bottom,
                $"放大后窗体整体保持在当前屏幕工作区内 (L={widget.Left} R={widget.Right} T={widget.Top} B={widget.Bottom})");
            widget.ApplyScale(100);
            widget.Left = scaleWa.Left + 40;
            widget.Top = scaleWa.Top + 40;
            var snap = typeof(WidgetForm).GetMethod("SnapPosition", Hidden);
            settings.SnapToEdges = true;
            var workArea = Screen.PrimaryScreen.WorkingArea;
            var snapped = (Point)snap.Invoke(widget, new object[] { workArea.Left + 10, workArea.Top + 10, 200, 100 });
            Check(snapped == new Point(workArea.Left, workArea.Top), "开启边缘吸附后距边缘 24px 内的位置吸附到工作区左上角");
            var keptFar = (Point)snap.Invoke(widget, new object[] { workArea.Left + 60, workArea.Top + 60, 200, 100 });
            Check(keptFar == new Point(workArea.Left + 60, workArea.Top + 60), "距边缘超过 24px 的位置不吸附保持原值");
            var clamped = (Point)snap.Invoke(widget, new object[] { workArea.Left - 70, workArea.Top + 100, 200, 100 });
            Check(clamped == new Point(workArea.Left, workArea.Top + 100), "拖出工作区的位置被挡在屏幕边缘并贴齐");
            settings.SnapToEdges = false;
            var keptOff = (Point)snap.Invoke(widget, new object[] { workArea.Left + 10, workArea.Top + 10, 200, 100 });
            Check(keptOff == new Point(workArea.Left + 10, workArea.Top + 10), "关闭边缘吸附后近边缘位置保持原值");
            settings.SnapToEdges = true;
            settings.EdgeAutoHide = true;
            // 缩到最小并停靠在远离真实光标的一侧：真实光标位于窗口内时收起计时按设计跳过，
            // 缩小窗口并避开光标可以让自动化断言稳定。
            widget.ApplyScale(50);
            var dockWa = Screen.FromPoint(new Point(widget.Left + widget.Width / 2, widget.Top + widget.Height / 2)).WorkingArea;
            bool dockAtRight = Cursor.Position.X < dockWa.Left + dockWa.Width / 2;
            widget.Left = dockAtRight ? dockWa.Right - widget.Width : dockWa.Left;
            widget.Top = Math.Max(dockWa.Top, Math.Min(widget.Top, dockWa.Bottom - widget.Height));
            typeof(WidgetForm).GetMethod("CompleteDrag", Hidden).Invoke(widget, null);
            Pump(4500);
            var verticalSize = WidgetSizeHelper.GetSize(WidgetLayout.Vertical, DpiHelper.GetDpiScale(widget), settings.ScalePercent / 100f);
            Check(widget.Width == verticalSize.Width && widget.Height == verticalSize.Height,
                "停靠侧边后自动变形为竖版形态");
            int shownLeft = dockAtRight ? dockWa.Right - verticalSize.Width : dockWa.Left;
            int hiddenLeft = dockAtRight ? dockWa.Right - 16 : dockWa.Left - (verticalSize.Width - 16);
            Check(widget.Left == hiddenLeft, "贴边释放后自动收起仅留 16px 小边");
            typeof(WidgetForm).GetMethod("OnMouseEnter", Hidden).Invoke(widget, new object[] { EventArgs.Empty });
            Pump(700);
            Check(widget.Left == shownLeft && widget.Height == verticalSize.Height, "鼠标移到小边后窗口滑出");
            typeof(WidgetForm).GetMethod("OnMouseLeave", Hidden).Invoke(widget, new object[] { EventArgs.Empty });
            Pump(3000);
            Check(widget.Left == hiddenLeft, "鼠标离开后窗口再次收起");
            widget.SetEdgeAutoHide(false);
            Pump(700);
            Check(widget.Left == shownLeft, "关闭贴边隐藏后窗口保持滑出状态");
            typeof(WidgetForm).GetMethod("OnMouseLeave", Hidden).Invoke(widget, new object[] { EventArgs.Empty });
            Pump(2500);
            Check(widget.Left == shownLeft, "关闭贴边隐藏后不再自动收起");
            widget.SetEdgeAutoHide(true);
            widget.Left = dockWa.Left + 60;
            widget.Top = dockWa.Top + 100;
            typeof(WidgetForm).GetMethod("CompleteDrag", Hidden).Invoke(widget, null);
            Pump(1500);
            Check(widget.Left == dockWa.Left + 60, "未贴边时释放不触发收起");
            var horizontalSize = WidgetSizeHelper.GetSize(WidgetLayout.Horizontal, DpiHelper.GetDpiScale(widget), settings.ScalePercent / 100f);
            Check(widget.Width == horizontalSize.Width && widget.Height == horizontalSize.Height,
                "拖离边缘后动画恢复停靠前的横版布局");
            widget.ApplyScale(100);
            widget.ApplyLayout(WidgetLayout.Expanded);
            settings.PowerSavingMode = false;
            widget.ApplyMonitoringSettings();
            Pump(600);
            Check(Field(monitor, "_processTimer") != null, "visible expanded layout enables actual process scanner");
            Check(widget.SelectedProcessMetric == ProcessMetric.Memory, "expanded process ranking defaults to memory");
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
            widget.ApplyLayout(WidgetLayout.Mini);
            var miniSize = WidgetSizeHelper.GetSize(WidgetLayout.Mini, DpiHelper.GetDpiScale(widget), settings.ScalePercent / 100f);
            Check(widget.Width == miniSize.Width && widget.Height == miniSize.Height, "迷你版布局尺寸按 DPI 与用户缩放计算");
            Check(Field(monitor, "_processTimer") == null, "迷你版布局不开启进程扫描");
            widget.ApplyLayout(WidgetLayout.Expanded);
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
            Check(HasMenuItem(widget.ContextMenuStrip.Items, "迷你版") && HasMenuItem(widget.ContextMenuStrip.Items, "大小") &&
                HasMenuItem(widget.ContextMenuStrip.Items, "边缘吸附"), "右键菜单包含迷你版、大小与边缘吸附文本项");
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

    private static bool HasMenuItem(ToolStripItemCollection items, string text)
    {
        foreach (ToolStripItem item in items)
        {
            if (item.Text.Replace("● ", "") == text) return true;
            if (item is ToolStripMenuItem menuItem && HasMenuItem(menuItem.DropDownItems, text)) return true;
        }
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
