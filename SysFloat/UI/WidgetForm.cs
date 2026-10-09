using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SysFloat.Configuration;
using SysFloat.Monitoring;
using SysFloat.Native;

namespace SysFloat.UI
{
    public class WidgetForm : Form
    {
        private readonly MonitorService _monitorService;
        private readonly SettingsStore _settingsStore;
        private AppSettings _settings;

        private WidgetLayout _currentLayout;
        private MetricSnapshot _snapshot = new MetricSnapshot();
        private readonly float[] _cpuHistory = CreateHistory();
        private readonly float[] _ramHistory = CreateHistory();
        private readonly float[] _vramHistory = CreateHistory();
        private readonly float[] _cpuDrawHistory = new float[60];
        private readonly float[] _ramDrawHistory = new float[60];
        private readonly float[] _vramDrawHistory = new float[60];
        private int _historyIndex;
        private int _historyCount;
        private readonly object _snapshotGate = new object();
        private MetricSnapshot _pendingSnapshot;
        private bool _snapshotDispatchQueued;
        private SynchronizationContext _uiSynchronizationContext;

        private bool _isDragging;
        private bool _isLeftPressActive;
        private bool _dragCandidate;
        private int _pressedProcessMetric = -1;
        private Point _mouseDownPoint;
        private Point _dragStart;
        private Point _formStart;
        private ProcessMetric _selectedProcessMetric = ProcessMetric.Memory;

        private bool _isCloseHover;
        private Rectangle _closeButtonRect;

        private ContextMenuStrip _contextMenu;
        private System.Windows.Forms.Timer _topMostTimer;
        private System.Windows.Forms.Timer _clockTimer;
        private ToolTip _metricToolTip;
        private Rectangle[] _metricHitRects = new Rectangle[0];
        private int _activeMetric = -1;
        private volatile bool _disposed;
        private int _activeAlertMetrics;

        public event Action CloseClicked;
        public event Action<WidgetLayout> LayoutChanged;
        public event Action<bool> TopMostChanged;
        public event Action PositionChanged;
        public event Action MonitoringOptionsRequested;

        public ProcessMetric SelectedProcessMetric => _selectedProcessMetric;

        public WidgetForm(MonitorService monitorService, SettingsStore settingsStore, AppSettings settings)
        {
            _monitorService = monitorService;
            _settingsStore = settingsStore;
            _settings = settings;
            _settings.Normalize();
            _currentLayout = settings.Layout;

            SetupForm();
            SetupContextMenu();
            SetupTopMostTimer();
            SetupClockTimer();
            _metricToolTip = new ToolTip { AutomaticDelay = 350, ReshowDelay = 150, AutoPopDelay = 12000, ShowAlways = true };

            _monitorService.OnDataUpdated += OnDataUpdated;
            ApplyMonitoringSettings();
        }

        private void SetupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = _settings.AlwaysOnTop;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(25, 30, 45);
            DoubleBuffered = true;

            var size = WidgetSizeHelper.GetSize(_currentLayout, 1.0f);
            Size = size;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        private void SetupContextMenu()
        {
            _contextMenu = new ContextMenuStrip();
            _contextMenu.Renderer = new DarkMenuRenderer();

            var showItem = new ToolStripMenuItem("显示悬浮窗");
            showItem.Click += (s, e) => ShowWidget();
            _contextMenu.Items.Add(showItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var horizontalItem = new ToolStripMenuItem("横版");
            horizontalItem.Click += (s, e) => ApplyLayout(WidgetLayout.Horizontal);
            _contextMenu.Items.Add(horizontalItem);

            var verticalItem = new ToolStripMenuItem("竖版");
            verticalItem.Click += (s, e) => ApplyLayout(WidgetLayout.Vertical);
            _contextMenu.Items.Add(verticalItem);

            var expandedItem = new ToolStripMenuItem("展开版");
            expandedItem.Click += (s, e) => ApplyLayout(WidgetLayout.Expanded);
            _contextMenu.Items.Add(expandedItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var topMostItem = new ToolStripMenuItem("始终置顶");
            topMostItem.Click += (s, e) => SetTopMost(!_settings.AlwaysOnTop);
            _contextMenu.Items.Add(topMostItem);

            var lockItem = new ToolStripMenuItem("锁定位置");
            lockItem.Click += (s, e) =>
            {
                _settings.Locked = !_settings.Locked;
                _settingsStore.Save(_settings);
                UpdateMenuChecks();
            };
            _contextMenu.Items.Add(lockItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var op100 = new ToolStripMenuItem("不透明");
            op100.Click += (s, e) => SetOpacity(WidgetOpacity.Opaque);
            _contextMenu.Items.Add(op100);

            var op75 = new ToolStripMenuItem("75% 透明");
            op75.Click += (s, e) => SetOpacity(WidgetOpacity.Percent75);
            _contextMenu.Items.Add(op75);

            var op50 = new ToolStripMenuItem("50% 透明");
            op50.Click += (s, e) => SetOpacity(WidgetOpacity.Percent50);
            _contextMenu.Items.Add(op50);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var monitorSettingsItem = new ToolStripMenuItem("监控设置…");
            monitorSettingsItem.Click += (s, e) => MonitoringOptionsRequested?.Invoke();
            _contextMenu.Items.Add(monitorSettingsItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var aboutItem = new ToolStripMenuItem("关于");
            aboutItem.Click += (s, e) => MessageBox.Show("SysFloat v1.0\n轻量级系统监控", "关于 SysFloat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _contextMenu.Items.Add(aboutItem);

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => CloseClicked?.Invoke();
            _contextMenu.Items.Add(exitItem);

            ContextMenuStrip = _contextMenu;
        }

        public void UpdateMenuChecks()
        {
            string[] layoutTexts = { "横版", "竖版", "展开版" };
            WidgetLayout[] layouts = { WidgetLayout.Horizontal, WidgetLayout.Vertical, WidgetLayout.Expanded };
            string[] opacityTexts = { "不透明", "75% 透明", "50% 透明" };
            WidgetOpacity[] opacities = { WidgetOpacity.Opaque, WidgetOpacity.Percent75, WidgetOpacity.Percent50 };

            for (int i = 0; i < _contextMenu.Items.Count; i++)
            {
                if (_contextMenu.Items[i] is ToolStripMenuItem item)
                {
                    string baseText = item.Text.Replace("● ", "");
                    bool isChecked = false;

                    for (int j = 0; j < layoutTexts.Length; j++)
                    {
                        if (baseText == layoutTexts[j]) { isChecked = _currentLayout == layouts[j]; break; }
                    }
                    if (!isChecked && baseText == "始终置顶") isChecked = _settings.AlwaysOnTop;
                    if (!isChecked && baseText == "锁定位置") isChecked = _settings.Locked;
                    for (int j = 0; j < opacityTexts.Length; j++)
                    {
                        if (baseText == opacityTexts[j]) { isChecked = _settings.Opacity == opacities[j]; break; }
                    }

                    item.Text = isChecked ? "● " + baseText : baseText;
                    item.Checked = isChecked;
                }
            }
        }

        public void SetOpacity(WidgetOpacity opacity)
        {
            _settings.Opacity = opacity;
            Opacity = (double)opacity / 100.0;
            _settingsStore.Save(_settings);
            UpdateMenuChecks();
        }

        public void ApplyOpacity()
        {
            Opacity = (double)_settings.Opacity / 100.0;
        }

        public void ApplyLayout(WidgetLayout layout, bool save = true)
        {
            _currentLayout = layout;
            if (save) _settings.Layout = layout;

            float dpiScale = DpiHelper.GetDpiScale(this);
            var size = WidgetSizeHelper.GetSize(layout, dpiScale);

            int rightEdge = Right;
            int topEdge = Top;

            Size = size;

            if (save && rightEdge > 0)
            {
                Left = Math.Max(0, rightEdge - size.Width);
                Top = topEdge;
            }

            SetRoundedRegion();
            UpdateMenuChecks();

            if (save)
            {
                _settingsStore.Save(_settings);
                LayoutChanged?.Invoke(layout);
            }

            HideMetricToolTip();
            UpdateMetricHitRects();
            ApplyMonitoringSettings();

            Invalidate();
        }

        private void SetRoundedRegion()
        {
            if (!IsHandleCreated) return;
            var bounds = new Rectangle(0, 0, Width, Height);
            int radius = (int)Math.Round(GetCornerRadius(_currentLayout) * (Math.Max(1, DeviceDpi) / 96f));
            using (var gp = WidgetRenderer.CreateRoundedRectPath(bounds, radius))
            {
                var oldRegion = Region;
                Region = new Region(gp);
                oldRegion?.Dispose();
            }
        }

        private int GetCornerRadius(WidgetLayout layout)
        {
            switch (layout)
            {
                case WidgetLayout.Horizontal: return 18;
                case WidgetLayout.Vertical: return 16;
                case WidgetLayout.Expanded: return 14;
                default: return 18;
            }
        }

        private void SetupTopMostTimer()
        {
            _topMostTimer = new System.Windows.Forms.Timer();
            _topMostTimer.Interval = 3000;
            _topMostTimer.Tick += (s, e) =>
            {
                if (_settings.AlwaysOnTop && Visible) ForceTopMost();
            };
            if (Visible && _settings.AlwaysOnTop) _topMostTimer.Start();
        }

        private void SetupClockTimer()
        {
            _clockTimer = new System.Windows.Forms.Timer { Interval = MillisecondsUntilNextMinute() };
            _clockTimer.Tick += (s, e) =>
            {
                _clockTimer.Interval = MillisecondsUntilNextMinute();
                if (Visible) Invalidate();
            };
        }

        private static int MillisecondsUntilNextMinute()
        {
            var utc = DateTime.UtcNow;
            return Math.Max(1, 60000 - utc.Second * 1000 - utc.Millisecond);
        }

        public void ApplyMonitoringSettings()
        {
            if (_settings == null || _monitorService == null || _disposed) return;
            _settings.Normalize();
            int seconds = _settings.PowerSavingMode ? _settings.SamplingIntervalSeconds : 1;
            int effectiveMs = seconds * 1000;
            _monitorService.ConfigurePolling(effectiveMs, Visible && _currentLayout == WidgetLayout.Expanded, _selectedProcessMetric);
            Invalidate();
        }

        public void SelectProcessMetric(ProcessMetric metric)
        {
            if (!Enum.IsDefined(typeof(ProcessMetric), metric)) return;
            _selectedProcessMetric = metric;
            ApplyMonitoringSettings();
            HideMetricToolTip();
            if (Visible) Invalidate();
        }

        public void SetAlertState(AlertEvaluation state)
        {
            if (_disposed || _activeAlertMetrics == state.ActiveMetricsMask) return;
            _activeAlertMetrics = state.ActiveMetricsMask;
            if (Visible) Invalidate();
        }

        private void ForceTopMost()
        {
            if (!IsHandleCreated) return;
            if (_settings.AlwaysOnTop)
            {
                int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, exStyle | NativeMethods.WS_EX_TOPMOST);
                NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ForceTopMost();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                ForceTopMost();
                if (_settings.AlwaysOnTop) _topMostTimer?.Start();
                if (_clockTimer != null) _clockTimer.Interval = MillisecondsUntilNextMinute();
                _clockTimer?.Start();
            }
            else
            {
                _clockTimer?.Stop();
                _topMostTimer?.Stop();
                HideMetricToolTip();
            }
            ApplyMonitoringSettings();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            var size = WidgetSizeHelper.GetSize(_currentLayout, Math.Max(1, DeviceDpi) / 96f);
            Size = size;
            SetRoundedRegion();
            UpdateMetricHitRects();
            Invalidate();
        }

        public void SetTopMost(bool topMost)
        {
            _settings.AlwaysOnTop = topMost;
            TopMost = topMost;
            ForceTopMost();
            if (topMost && Visible) _topMostTimer?.Start();
            else _topMostTimer?.Stop();
            _settingsStore.Save(_settings);
            TopMostChanged?.Invoke(topMost);
            UpdateMenuChecks();
        }

        public void ShowWidget()
        {
            _settings.Visible = true;
            if (!IsHandleCreated)
            {
                SetInitialPosition();
            }
            SetRoundedRegion();
            ApplyOpacity();
            Show();
            EnableShadow();
            UpdateMetricHitRects();
            ApplyMonitoringSettings();
            _settingsStore.Save(_settings);
        }

        public void HideWidget()
        {
            _settings.Visible = false;
            Hide();
            HideMetricToolTip();
            ApplyMonitoringSettings();
            _settingsStore.Save(_settings);
        }

        public void SetInitialPosition()
        {
            float dpiScale = DpiHelper.GetDpiScale(this);
            var size = WidgetSizeHelper.GetSize(_currentLayout, dpiScale);

            if (_settings.Left >= 0 && _settings.Top >= 0)
            {
                Left = _settings.Left;
                Top = _settings.Top;
                EnsureOnScreen();
            }
            else
            {
                var screen = Screen.PrimaryScreen.WorkingArea;
                Left = screen.Right - size.Width - 20;
                Top = screen.Top + 20;
            }

            Size = size;
        }

        private void EnsureOnScreen()
        {
            var screen = Screen.FromPoint(new Point(Left, Top)).WorkingArea;
            if (Left < screen.Left) Left = screen.Left;
            if (Top < screen.Top) Top = screen.Top;
            if (Right > screen.Right) Left = screen.Right - Width;
            if (Bottom > screen.Bottom) Top = screen.Bottom - Height;
        }

        private void OnDataUpdated(MetricSnapshot snapshot)
        {
            if (snapshot == null || _disposed) return;
            bool post = false;
            lock (_snapshotGate)
            {
                _pendingSnapshot = snapshot;
                if (!_snapshotDispatchQueued && _uiSynchronizationContext != null)
                {
                    _snapshotDispatchQueued = true;
                    post = true;
                }
            }
            if (post) PostSnapshotDispatch();
        }

        private void PostSnapshotDispatch()
        {
            var context = _uiSynchronizationContext;
            if (_disposed || context == null) return;
            try { context.Post(_ => ProcessPendingSnapshot(), null); }
            catch (InvalidOperationException) { lock (_snapshotGate) _snapshotDispatchQueued = false; }
        }

        private void ProcessPendingSnapshot()
        {
            MetricSnapshot latest;
            lock (_snapshotGate)
            {
                latest = _pendingSnapshot;
                _pendingSnapshot = null;
                _snapshotDispatchQueued = false;
            }
            if (latest == null || _disposed || IsDisposed || Disposing) return;
            _snapshot = latest;
            _cpuHistory[_historyIndex] = ValidSample(latest.CpuPercent);
            _ramHistory[_historyIndex] = ValidSample(latest.MemoryPercent);
            _vramHistory[_historyIndex] = ValidSample(latest.VramPercent);
            _historyIndex = (_historyIndex + 1) % _cpuHistory.Length;
            if (_historyCount < _cpuHistory.Length) _historyCount++;
            if (Visible) Invalidate();
        }

        private static float ValidSample(float value) => float.IsNaN(value) || float.IsInfinity(value) || value < 0 ? -1 : Math.Max(0, Math.Min(100, value));

        private static float[] CreateHistory()
        {
            var history = new float[60];
            for (int i = 0; i < history.Length; i++) history[i] = -1;
            return history;
        }

        private float[] GetChronologicalHistory(float[] history)
        {
            var result = ReferenceEquals(history, _cpuHistory) ? _cpuDrawHistory :
                ReferenceEquals(history, _ramHistory) ? _ramDrawHistory : _vramDrawHistory;
            int first = (_historyIndex - _historyCount + history.Length) % history.Length;
            for (int i = 0; i < result.Length; i++) result[i] = i < _historyCount ? history[(first + i) % history.Length] : -1;
            return result;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

            float scale = Math.Max(1, DeviceDpi) / 96f;
            g.ScaleTransform(scale, scale);

            var bounds = new Rectangle(0, 0, (int)Math.Ceiling(Width / scale), (int)Math.Ceiling(Height / scale));
            int threshold = _settings.HighLoadAlertsEnabled ? _settings.HighLoadThresholdPercent : int.MaxValue;

            switch (_currentLayout)
            {
                case WidgetLayout.Horizontal:
                    WidgetRenderer.DrawHorizontal(g, bounds, _snapshot, threshold, _settings.HighLoadAlertsEnabled, _activeAlertMetrics);
                    break;
                case WidgetLayout.Vertical:
                    WidgetRenderer.DrawVertical(g, bounds, _snapshot, threshold, _settings.HighLoadAlertsEnabled, _activeAlertMetrics);
                    break;
                case WidgetLayout.Expanded:
                    WidgetRenderer.DrawExpanded(g, bounds, _snapshot,
                        GetChronologicalHistory(_cpuHistory), GetChronologicalHistory(_ramHistory), GetChronologicalHistory(_vramHistory), _isCloseHover,
                        threshold, _settings.HighLoadAlertsEnabled, _activeAlertMetrics, _selectedProcessMetric);
                    _closeButtonRect = ScaleRect(WidgetRenderer.GetCloseButtonRect(bounds, WidgetLayout.Expanded), scale);
                    break;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) HideMetricToolTip();
            if (e.Button == MouseButtons.Left)
            {
                if (_currentLayout == WidgetLayout.Expanded && _closeButtonRect.Contains(e.Location))
                {
                    HideWidget();
                    return;
                }
                HideMetricToolTip();
                _isLeftPressActive = true;
                _dragCandidate = !_settings.Locked;
                _isDragging = false;
                _mouseDownPoint = e.Location;
                _pressedProcessMetric = GetProcessMetricAt(e.Location);
                _dragStart = Cursor.Position;
                _formStart = Location;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_isLeftPressActive && _dragCandidate && !_isDragging && ExceededDragThreshold(e.Location))
                _isDragging = true;

            if (_isDragging)
            {
                int dx = Cursor.Position.X - _dragStart.X;
                int dy = Cursor.Position.Y - _dragStart.Y;
                Left = _formStart.X + dx;
                Top = _formStart.Y + dy;
            }
            else if (!_isLeftPressActive && _currentLayout == WidgetLayout.Expanded)
            {
                bool wasHover = _isCloseHover;
                _isCloseHover = _closeButtonRect.Contains(e.Location);
                if (wasHover != _isCloseHover) Invalidate();
            }
            if (!_isLeftPressActive && !_isDragging)
            {
                UpdateCursor(e.Location);
                UpdateMetricToolTip(e.Location);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_isLeftPressActive && e.Button == MouseButtons.Left)
            {
                bool dragged = _isDragging;
                _isLeftPressActive = false;
                _isDragging = false;
                _dragCandidate = false;
                Capture = false;
                if (dragged)
                {
                    _settings.Left = Left;
                    _settings.Top = Top;
                    _settingsStore.Save(_settings);
                    PositionChanged?.Invoke();
                }
                else
                {
                    int releasedMetric = GetProcessMetricAt(e.Location);
                    if (_pressedProcessMetric >= 0 && releasedMetric == _pressedProcessMetric)
                        SelectProcessMetric((ProcessMetric)_pressedProcessMetric);
                }
                _pressedProcessMetric = -1;
                UpdateCursor(e.Location);
            }
            base.OnMouseUp(e);
        }

        private bool ExceededDragThreshold(Point location)
        {
            Size dragSize = SystemInformation.DragSize;
            int dx = Math.Abs(location.X - _mouseDownPoint.X);
            int dy = Math.Abs(location.Y - _mouseDownPoint.Y);
            return dx >= Math.Max(1, dragSize.Width / 2) || dy >= Math.Max(1, dragSize.Height / 2);
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _isLeftPressActive)
            {
                _isLeftPressActive = false;
                _isDragging = false;
                _dragCandidate = false;
                _pressedProcessMetric = -1;
            }
        }

        private int GetProcessMetricAt(Point physicalLocation)
        {
            if (_currentLayout != WidgetLayout.Expanded) return -1;
            for (int i = 0; i < 3 && i < _metricHitRects.Length; i++)
                if (_metricHitRects[i].Contains(physicalLocation)) return i;
            return -1;
        }

        private void UpdateCursor(Point location)
        {
            if (_currentLayout == WidgetLayout.Expanded &&
                (_closeButtonRect.Contains(location) || GetProcessMetricAt(location) >= 0))
                Cursor = Cursors.Hand;
            else
                Cursor = _settings.Locked ? Cursors.Default : Cursors.SizeAll;
        }

        public WidgetLayout GetCurrentLayout() => _currentLayout;

        private void UpdateMetricHitRects()
        {
            float scale = Math.Max(1, DeviceDpi) / 96f;
            var logical = new Rectangle(0, 0, (int)Math.Ceiling(Width / scale), (int)Math.Ceiling(Height / scale));
            var logicalRects = WidgetRenderer.GetMetricHitRects(logical, _currentLayout);
            _metricHitRects = new Rectangle[logicalRects.Length];
            for (int i = 0; i < logicalRects.Length; i++) _metricHitRects[i] = ScaleRect(logicalRects[i], scale);
            if (_currentLayout == WidgetLayout.Expanded) _closeButtonRect = ScaleRect(WidgetRenderer.GetCloseButtonRect(logical, _currentLayout), scale);
        }

        private static Rectangle ScaleRect(Rectangle rect, float scale) => new Rectangle(
            (int)Math.Floor(rect.X * scale), (int)Math.Floor(rect.Y * scale),
            (int)Math.Ceiling(rect.Width * scale), (int)Math.Ceiling(rect.Height * scale));

        private void UpdateMetricToolTip(Point location)
        {
            int metric = -1;
            for (int i = 0; i < _metricHitRects.Length; i++) if (_metricHitRects[i].Contains(location)) { metric = i; break; }
            if (metric == _activeMetric) return;
            HideMetricToolTip();
            if (metric < 0 || !Visible || _isDragging) return;
            _activeMetric = metric;
            string text = BuildMetricToolTip(metric);
            _metricToolTip.Show(text, this, Math.Min(Width - 10, location.X + 12), Math.Min(Height - 10, location.Y + 18), 12000);
        }

        private string BuildMetricToolTip(int metric)
        {
            if (metric == 3)
            {
                DateTime utc = DateTime.UtcNow;
                DateTime pacific = PacificClock.GetPacificTime(utc);
                int hoursBehindBeijing = (int)(utc.AddHours(8) - pacific).TotalHours;
                return "太平洋时间：" + pacific.ToString("HH:mm") + " " + PacificClock.GetAbbreviation(utc) + "\n" +
                    PacificClock.FormatFullDate(utc) + $"\n比北京时间慢 {hoursBehindBeijing} 小时";
            }
            if (metric == 0)
            {
                float min = 100, max = 0; bool any = false;
                foreach (float value in GetChronologicalHistory(_cpuHistory)) if (value >= 0 && !float.IsNaN(value) && !float.IsInfinity(value)) { min = Math.Min(min, value); max = Math.Max(max, value); any = true; }
                return !HasSample(_snapshot.CpuPercent) ? "CPU：不可用" : string.Format("CPU：{0:0.0}%\n最近区间：{1}", _snapshot.CpuPercent, any ? string.Format("{0:0.0}% – {1:0.0}%", min, max) : "暂无历史");
            }
            if (metric == 1)
                return _snapshot.MemoryTotalBytes > 0 ? string.Format("RAM：{0}\n已用 {1} / 共 {2}", FormatPercent(_snapshot.MemoryPercent), FormatBytes(_snapshot.MemoryUsedBytes), FormatBytes(_snapshot.MemoryTotalBytes)) : "RAM：不可用";
            if (!_snapshot.VramAvailable) return "VRAM：不可用";
            if (_snapshot.VramTotalBytes == 0) return $"VRAM：已用 {FormatBytes(_snapshot.VramUsedBytes)}\n总容量暂不可用";
            return string.Format("VRAM：{0}\n已用 {1} / 共 {2}", FormatPercent(_snapshot.VramPercent), FormatBytes(_snapshot.VramUsedBytes), FormatBytes(_snapshot.VramTotalBytes));
        }

        private static bool HasSample(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;
        private static string FormatPercent(float value) => HasSample(value) ? string.Format("{0:0.0}%", value) : "不可用";

        private static string FormatBytes(ulong bytes)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            return gb >= 1 ? gb.ToString("0.00") + " GB" : (bytes / (1024.0 * 1024.0)).ToString("0") + " MB";
        }

        private void HideMetricToolTip()
        {
            _metricToolTip?.Hide(this);
            _activeMetric = -1;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            HideMetricToolTip();
            _isCloseHover = false;
            if (!_isLeftPressActive) Cursor = Cursors.Default;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _uiSynchronizationContext = SynchronizationContext.Current as WindowsFormsSynchronizationContext
                ?? new WindowsFormsSynchronizationContext();
            UpdateMetricHitRects();
            bool post;
            lock (_snapshotGate)
            {
                post = _pendingSnapshot != null && !_snapshotDispatchQueued;
                if (post) _snapshotDispatchQueued = true;
            }
            if (post) PostSnapshotDispatch();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (_monitorService != null) _monitorService.OnDataUpdated -= OnDataUpdated;
                _clockTimer?.Stop();
                _clockTimer?.Dispose();
                _topMostTimer?.Stop();
                _topMostTimer?.Dispose();
                _metricToolTip?.Dispose();
                _contextMenu?.Dispose();
            }
            base.Dispose(disposing);
        }

        public void ToggleVisibility()
        {
            if (Visible) HideWidget();
            else ShowWidget();
        }

        public void MoveToVisibleArea()
        {
            var screen = Screen.PrimaryScreen.WorkingArea;
            if (Left < screen.Left || Top < screen.Top || Left > screen.Right - 50 || Top > screen.Bottom - 50)
            {
                Left = screen.Right - Width - 20;
                Top = screen.Top + 20;
            }
            ShowWidget();
        }

        [DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
        private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll", EntryPoint = "SetClassLong")]
        private static extern IntPtr SetClassLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private const int GCL_STYLE = -26;
        private const int CS_DROPSHADOW = 0x00020000;

        private void EnableShadow()
        {
            if (!IsHandleCreated) return;
            IntPtr result;
            if (IntPtr.Size == 8)
                result = SetClassLongPtr64(Handle, GCL_STYLE, (IntPtr)CS_DROPSHADOW);
            else
                result = SetClassLongPtr32(Handle, GCL_STYLE, (IntPtr)CS_DROPSHADOW);
        }
    }

    public static class DpiHelper
    {
        public static float GetDpiScale(Control control)
        {
            try
            {
                using (var g = control.CreateGraphics())
                    return g.DpiX / 96f;
            }
            catch { return 1.0f; }
        }
    }

    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Enabled) e.TextColor = Color.FromArgb(240, 242, 245);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using (var brush = new SolidBrush(e.Item.Selected ? Color.FromArgb(40, 48, 60) : Color.FromArgb(30, 36, 46)))
                e.Graphics.FillRectangle(brush, e.Item.ContentRectangle);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (var pen = new Pen(Color.FromArgb(50, 58, 70)))
                e.Graphics.DrawLine(pen, e.Item.ContentRectangle.Left + 4, e.Item.ContentRectangle.Top,
                    e.Item.ContentRectangle.Right - 4, e.Item.ContentRectangle.Top);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using (var brush = new SolidBrush(Color.FromArgb(30, 36, 46)))
                e.Graphics.FillRectangle(brush, e.ToolStrip.ClientRectangle);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var brush = new SolidBrush(Color.FromArgb(30, 36, 46)))
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using (var brush = new SolidBrush(Color.FromArgb(67, 142, 255)))
                e.Graphics.FillEllipse(brush, new Rectangle(e.ImageRectangle.X, e.ImageRectangle.Y + 2, 6, 6));
        }
    }

    public class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => Color.FromArgb(30, 36, 46);
        public override Color MenuStripGradientEnd => Color.FromArgb(30, 36, 46);
        public override Color MenuItemBorder => Color.FromArgb(67, 142, 255);
        public override Color MenuItemSelected => Color.FromArgb(40, 48, 60);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(40, 48, 60);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(40, 48, 60);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(30, 36, 46);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(30, 36, 46);
        public override Color ImageMarginGradientBegin => Color.FromArgb(30, 36, 46);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(30, 36, 46);
        public override Color ImageMarginGradientEnd => Color.FromArgb(30, 36, 46);
    }
}
