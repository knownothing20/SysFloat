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
        private bool _handCursorActive;
        private volatile bool _disposed;
        private int _activeAlertMetrics;
        private LayeredWidgetSurface _layeredSurface;
        private int _layeredRenderQueued;
        private int _layeredRenderGeneration;

        private const int EdgeSliverSize = 16;
        private DockEdge _dockEdge = DockEdge.None;
        private Rectangle _dockedBounds;
        private bool _isEdgeHidden;
        private Rectangle _slideTarget;
        private Rectangle _slideBoundsWa;
        private bool _slideResizes;
        private bool _pendingHideAfterSlide;
        private WidgetLayout? _preDockLayout;
        private System.Windows.Forms.Timer _slideTimer;       // 15ms，向 _slideTarget 滑动/形变
        private System.Windows.Forms.Timer _hideCountdown;    // 600ms，贴边释放后收起
        private System.Windows.Forms.Timer _revealCountdown;  // 800ms，鼠标离开后收起

        private const int WsExLayered = 0x00080000;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpFrameChanged = 0x0020;

        public event Action CloseClicked;
        public event Action<WidgetLayout> LayoutChanged;
        public event Action<bool> TopMostChanged;
        public event Action PositionChanged;
        public event Action MonitoringOptionsRequested;

        public ProcessMetric SelectedProcessMetric => _selectedProcessMetric;

        private float UserScale => Math.Max(0.5f, Math.Min(2f, (_settings?.ScalePercent ?? 100) / 100f));

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
            SetupDockTimers();
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

            var size = WidgetSizeHelper.GetSize(_currentLayout, 1.0f, UserScale);
            Size = size;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW;
                if (UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque))
                    cp.ExStyle |= WsExLayered;
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

            var miniItem = new ToolStripMenuItem("迷你版");
            miniItem.Click += (s, e) => ApplyLayout(WidgetLayout.Mini);
            _contextMenu.Items.Add(miniItem);

            var sizeMenu = new ToolStripMenuItem("大小");
            var scale75Item = new ToolStripMenuItem("75%");
            scale75Item.Click += (s, e) => ApplyScale(75);
            sizeMenu.DropDownItems.Add(scale75Item);
            var scale100Item = new ToolStripMenuItem("100%");
            scale100Item.Click += (s, e) => ApplyScale(100);
            sizeMenu.DropDownItems.Add(scale100Item);
            var scale125Item = new ToolStripMenuItem("125%");
            scale125Item.Click += (s, e) => ApplyScale(125);
            sizeMenu.DropDownItems.Add(scale125Item);
            var scale150Item = new ToolStripMenuItem("150%");
            scale150Item.Click += (s, e) => ApplyScale(150);
            sizeMenu.DropDownItems.Add(scale150Item);
            sizeMenu.DropDownItems.Add(new ToolStripSeparator());
            var scaleHintItem = new ToolStripMenuItem("提示：Ctrl+滚轮 微调大小");
            scaleHintItem.Enabled = false;
            sizeMenu.DropDownItems.Add(scaleHintItem);
            _contextMenu.Items.Add(sizeMenu);

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

            var snapItem = new ToolStripMenuItem("边缘吸附");
            snapItem.Click += (s, e) =>
            {
                _settings.SnapToEdges = !_settings.SnapToEdges;
                _settingsStore.Save(_settings);
                UpdateMenuChecks();
            };
            _contextMenu.Items.Add(snapItem);

            var autoStartItem = new ToolStripMenuItem("开机自启");
            autoStartItem.Click += (s, e) =>
            {
                StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
                UpdateMenuChecks();
            };
            _contextMenu.Items.Add(autoStartItem);

            var edgeHideItem = new ToolStripMenuItem("贴边隐藏");
            edgeHideItem.Click += (s, e) => SetEdgeAutoHide(!_settings.EdgeAutoHide);
            _contextMenu.Items.Add(edgeHideItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var op100 = new ToolStripMenuItem("背景不透明");
            op100.Click += (s, e) => SetOpacity(WidgetOpacity.Opaque);
            _contextMenu.Items.Add(op100);

            var op75 = new ToolStripMenuItem("背景 75%");
            op75.Click += (s, e) => SetOpacity(WidgetOpacity.Percent75);
            _contextMenu.Items.Add(op75);

            var op50 = new ToolStripMenuItem("背景 50%");
            op50.Click += (s, e) => SetOpacity(WidgetOpacity.Percent50);
            _contextMenu.Items.Add(op50);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var monitorSettingsItem = new ToolStripMenuItem("监控设置…");
            monitorSettingsItem.Click += (s, e) => MonitoringOptionsRequested?.Invoke();
            _contextMenu.Items.Add(monitorSettingsItem);

            _contextMenu.Items.Add(new ToolStripSeparator());

            var aboutItem = new ToolStripMenuItem("关于");
            aboutItem.Click += (s, e) => MessageBox.Show("SysFloat v1.2\n轻量级系统监控悬浮窗", "关于 SysFloat",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _contextMenu.Items.Add(aboutItem);

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += (s, e) => CloseClicked?.Invoke();
            _contextMenu.Items.Add(exitItem);

            // 菜单关闭后若鼠标不在窗口上，恢复“离开后自动收起”的倒计时。
            _contextMenu.Closed += (s, e) =>
            {
                if (_dockEdge != DockEdge.None && !_isEdgeHidden && _settings.EdgeAutoHide &&
                    !Bounds.Contains(Cursor.Position))
                    _revealCountdown.Start();
            };

            ContextMenuStrip = _contextMenu;
        }

        public void UpdateMenuChecks()
        {
            string[] layoutTexts = { "横版", "竖版", "展开版", "迷你版" };
            WidgetLayout[] layouts = { WidgetLayout.Horizontal, WidgetLayout.Vertical, WidgetLayout.Expanded, WidgetLayout.Mini };
            string[] opacityTexts = { "背景不透明", "背景 75%", "背景 50%" };
            WidgetOpacity[] opacities = { WidgetOpacity.Opaque, WidgetOpacity.Percent75, WidgetOpacity.Percent50 };
            string[] scaleTexts = { "75%", "100%", "125%", "150%" };
            int[] scaleValues = { 75, 100, 125, 150 };

            void UpdateItems(ToolStripItemCollection items)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i] is ToolStripMenuItem item)
                    {
                        string baseText = item.Text.Replace("● ", "");
                        bool isChecked = false;

                        for (int j = 0; j < layoutTexts.Length; j++)
                        {
                            if (baseText == layoutTexts[j]) { isChecked = _currentLayout == layouts[j]; break; }
                        }
                        if (!isChecked && baseText == "始终置顶") isChecked = _settings.AlwaysOnTop;
                        if (!isChecked && baseText == "锁定位置") isChecked = _settings.Locked;
                        if (!isChecked && baseText == "边缘吸附") isChecked = _settings.SnapToEdges;
                        if (!isChecked && baseText == "贴边隐藏") isChecked = _settings.EdgeAutoHide;
                        if (!isChecked && baseText == "开机自启") isChecked = StartupRegistration.IsEnabled();
                        for (int j = 0; j < opacityTexts.Length; j++)
                        {
                            if (baseText == opacityTexts[j]) { isChecked = _settings.Opacity == opacities[j]; break; }
                        }
                        for (int j = 0; j < scaleTexts.Length; j++)
                        {
                            if (baseText == scaleTexts[j]) { isChecked = _settings.ScalePercent == scaleValues[j]; break; }
                        }

                        item.Text = isChecked ? "● " + baseText : baseText;
                        item.Checked = isChecked;

                        if (item.DropDownItems.Count > 0)
                            UpdateItems(item.DropDownItems);
                    }
                }
            }

            UpdateItems(_contextMenu.Items);
        }

        public void SetOpacity(WidgetOpacity opacity)
        {
            if (!Enum.IsDefined(typeof(WidgetOpacity), opacity)) opacity = WidgetOpacity.Opaque;
            _settings.Opacity = opacity;
            ApplyOpacity();
            _settingsStore.Save(_settings);
            UpdateMenuChecks();
        }

        public void ApplyOpacity()
        {
            bool useLayered = UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque);
            if (!IsHandleCreated)
            {
                if (!useLayered) DisposeLayeredSurface();
                return;
            }

            int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
            bool hasLayeredStyle = (exStyle & WsExLayered) != 0;
            if (hasLayeredStyle != useLayered)
            {
                int updatedStyle = useLayered ? exStyle | WsExLayered : exStyle & ~WsExLayered;
                NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, updatedStyle);
                NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE |
                    SwpNoZOrder | SwpFrameChanged);
            }

            if (useLayered)
            {
                if (Visible) RenderLayeredSurface();
            }
            else
            {
                CancelQueuedLayeredRender();
                DisposeLayeredSurface();
                Invalidate();
            }
        }

        private static bool UsesLayeredOpacity(WidgetOpacity opacity) => opacity == WidgetOpacity.Percent75 || opacity == WidgetOpacity.Percent50;

        private int GetBackgroundAlpha()
        {
            switch (_settings?.Opacity ?? WidgetOpacity.Opaque)
            {
                case WidgetOpacity.Percent75: return 191;
                case WidgetOpacity.Percent50: return 150;
                default: return 255;
            }
        }

        private void DisposeLayeredSurface()
        {
            var surface = _layeredSurface;
            _layeredSurface = null;
            surface?.Dispose();
        }

        private void CancelQueuedLayeredRender()
        {
            Interlocked.Increment(ref _layeredRenderGeneration);
            Interlocked.Exchange(ref _layeredRenderQueued, 0);
        }

        public void ApplyLayout(WidgetLayout layout, bool save = true)
        {
            _currentLayout = layout;
            if (save)
            {
                _settings.Layout = layout;
                // 用户显式选择布局后，拖离边缘不再自动恢复旧布局。
                _preDockLayout = null;
            }

            float dpiScale = DpiHelper.GetDpiScale(this);
            var size = WidgetSizeHelper.GetSize(layout, dpiScale, UserScale);

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
            ReapplyDockAfterResize();
        }

        public void ApplyScale(int scalePercent, bool save = true)
        {
            scalePercent = Math.Max(50, Math.Min(200, scalePercent));
            scalePercent = (int)Math.Round(scalePercent / 5.0) * 5;
            _settings.ScalePercent = scalePercent;
            float dpiScale = DpiHelper.GetDpiScale(this);
            var size = WidgetSizeHelper.GetSize(_currentLayout, dpiScale, UserScale);
            // 一次性设置位置与尺寸并钳制在当前屏内：放大时若跨越到相邻显示器，
            // 系统会按邻屏 DPI 重设窗体尺寸（混合 DPI 多屏），导致尺寸与所在屏不一致。
            var wa = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2)).WorkingArea;
            int newLeft = Math.Max(wa.Left, Math.Min(Left, wa.Right - size.Width));
            int newTop = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - size.Height));
            Bounds = new Rectangle(newLeft, newTop, size.Width, size.Height);
            SetRoundedRegion();
            HideMetricToolTip();
            UpdateMetricHitRects();
            Invalidate();
            if (save)
            {
                _settingsStore.Save(_settings);
                UpdateMenuChecks();
            }
            ReapplyDockAfterResize();
        }

        private void SetRoundedRegion()
        {
            if (!IsHandleCreated) return;
            var bounds = new Rectangle(0, 0, Width, Height);
            int radius = (int)Math.Round(GetCornerRadius(_currentLayout) * (Math.Max(1, DeviceDpi) / 96f) * UserScale);
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
                case WidgetLayout.Mini: return 12;
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

        private void SetupDockTimers()
        {
            _slideTimer = new System.Windows.Forms.Timer { Interval = 15 };
            _slideTimer.Tick += (s, e) =>
            {
                int dx = _slideTarget.X - Left;
                int dy = _slideTarget.Y - Top;
                int dw = _slideTarget.Width - Width;
                int dh = _slideTarget.Height - Height;
                if (dx == 0 && dy == 0 && dw == 0 && dh == 0)
                {
                    CompleteSlide();
                    return;
                }
                // 每轴按距离 1/4（至少 2px）推进，且不越过目标。
                Left += Math.Sign(dx) * Math.Min(Math.Abs(dx), Math.Max(2, Math.Abs(dx) / 4));
                Top += Math.Sign(dy) * Math.Min(Math.Abs(dy), Math.Max(2, Math.Abs(dy) / 4));
                if (dw != 0 || dh != 0)
                {
                    Width += Math.Sign(dw) * Math.Min(Math.Abs(dw), Math.Max(2, Math.Abs(dw) / 4));
                    Height += Math.Sign(dh) * Math.Min(Math.Abs(dh), Math.Max(2, Math.Abs(dh) / 4));
                    // 形变时尺寸增长优先向屏幕内让位，避免中途跨越到相邻显示器触发 DPI 重设。
                    if (!_slideBoundsWa.IsEmpty && Width <= _slideBoundsWa.Width && Right > _slideBoundsWa.Right)
                        Left = _slideBoundsWa.Right - Width;
                    if (!_slideBoundsWa.IsEmpty && Height <= _slideBoundsWa.Height && Bottom > _slideBoundsWa.Bottom)
                        Top = _slideBoundsWa.Bottom - Height;
                    SetRoundedRegion();
                }
                if (Left == _slideTarget.X && Top == _slideTarget.Y &&
                    Width == _slideTarget.Width && Height == _slideTarget.Height)
                    CompleteSlide();
            };

            _hideCountdown = new System.Windows.Forms.Timer { Interval = 600 };
            _hideCountdown.Tick += (s, e) =>
            {
                _hideCountdown.Stop();
                // 右键菜单打开时暂缓收起，菜单关闭后由 Closed 处理重新计起。
                if (_settings.EdgeAutoHide && _dockEdge != DockEdge.None && !_isDragging && !_contextMenu.Visible)
                    EnterHiddenState(true);
            };

            _revealCountdown = new System.Windows.Forms.Timer { Interval = 800 };
            _revealCountdown.Tick += (s, e) =>
            {
                _revealCountdown.Stop();
                if (_settings.EdgeAutoHide && _dockEdge != DockEdge.None && !_isEdgeHidden &&
                    !_contextMenu.Visible && !Bounds.Contains(Cursor.Position))
                    EnterHiddenState(true);
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
                // 置顶计时器每 3 秒重申一次 z 序；样式位已置时跳过 SetWindowLong，
                // 避免每次触发样式变更消息。SetWindowPos 对已置顶窗口开销极小。
                int exStyle = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
                if ((exStyle & NativeMethods.WS_EX_TOPMOST) == 0)
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
            if (UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque)) RenderLayeredSurface();
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
                CancelQueuedLayeredRender();
                DisposeLayeredSurface();
            }
            ApplyMonitoringSettings();
            if (Visible && UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque)) QueueLayeredRender();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque)) QueueLayeredRender();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            var size = WidgetSizeHelper.GetSize(_currentLayout, Math.Max(1, DeviceDpi) / 96f, UserScale);
            Size = size;
            SetRoundedRegion();
            UpdateMetricHitRects();
            Invalidate();
            ReapplyDockAfterResize();
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
            bool wasVisible = Visible;
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
            if (wasVisible && _isEdgeHidden) ExitHiddenState(true);
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
            var size = WidgetSizeHelper.GetSize(_currentLayout, dpiScale, UserScale);

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

            // 上次收起在边缘：本次直接以收起状态出现，不闪；停靠方向决定横版/竖版形态。
            if (_settings.DockedEdge != DockEdge.None && _settings.EdgeAutoHide)
            {
                _dockEdge = _settings.DockedEdge;
                var dockLayout = GetDockLayout(_dockEdge);
                if (_currentLayout != dockLayout)
                {
                    _preDockLayout = _currentLayout;
                    _currentLayout = dockLayout;
                }
                var dockSize = WidgetSizeHelper.GetSize(dockLayout, dpiScale, UserScale);
                var wa = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2)).WorkingArea;
                int alongX = Math.Max(wa.Left, Math.Min(Left, wa.Right - dockSize.Width));
                int alongY = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - dockSize.Height));
                switch (_dockEdge)
                {
                    case DockEdge.Left: _dockedBounds = new Rectangle(wa.Left, alongY, dockSize.Width, dockSize.Height); break;
                    case DockEdge.Right: _dockedBounds = new Rectangle(wa.Right - dockSize.Width, alongY, dockSize.Width, dockSize.Height); break;
                    case DockEdge.Top: _dockedBounds = new Rectangle(alongX, wa.Top, dockSize.Width, dockSize.Height); break;
                    case DockEdge.Bottom: _dockedBounds = new Rectangle(alongX, wa.Bottom - dockSize.Height, dockSize.Width, dockSize.Height); break;
                }
                Size = dockSize;
                Location = GetHiddenPosition(_dockEdge, _dockedBounds, wa);
                _isEdgeHidden = true;
            }
            else
            {
                _settings.DockedEdge = DockEdge.None;
            }
        }

        private void EnsureOnScreen()
        {
            var screen = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2)).WorkingArea;
            if (Left < screen.Left) Left = screen.Left;
            if (Top < screen.Top) Top = screen.Top;
            if (Right > screen.Right) Left = screen.Right - Width;
            if (Bottom > screen.Bottom) Top = screen.Bottom - Height;
        }

        private const int EdgeSnapThreshold = 24;

        private Point SnapPosition(int left, int top, int width, int height)
        {
            if (!_settings.SnapToEdges) return new Point(left, top);
            var screen = Screen.FromPoint(new Point(left + width / 2, top + height / 2)).WorkingArea;
            if (Math.Abs(left - screen.Left) <= EdgeSnapThreshold) left = screen.Left;
            else if (Math.Abs(left + width - screen.Right) <= EdgeSnapThreshold) left = screen.Right - width;
            if (Math.Abs(top - screen.Top) <= EdgeSnapThreshold) top = screen.Top;
            else if (Math.Abs(top + height - screen.Bottom) <= EdgeSnapThreshold) top = screen.Bottom - height;
            // 拖到屏幕边界即停住贴齐，窗口不允许被推出工作区。
            left = Math.Max(screen.Left, Math.Min(left, screen.Right - width));
            top = Math.Max(screen.Top, Math.Min(top, screen.Bottom - height));
            return new Point(left, top);
        }

        // ===== 贴边自动隐藏 =====

        private static DockEdge DetectDockEdge(Rectangle bounds, Rectangle workingArea)
        {
            if (Math.Abs(bounds.Left - workingArea.Left) <= 2) return DockEdge.Left;
            if (Math.Abs(bounds.Right - workingArea.Right) <= 2) return DockEdge.Right;
            if (Math.Abs(bounds.Top - workingArea.Top) <= 2) return DockEdge.Top;
            if (Math.Abs(bounds.Bottom - workingArea.Bottom) <= 2) return DockEdge.Bottom;
            return DockEdge.None;
        }

        private static Point GetHiddenPosition(DockEdge edge, Rectangle bounds, Rectangle workingArea)
        {
            int alongX = Math.Max(workingArea.Left, Math.Min(bounds.X, workingArea.Right - bounds.Width));
            int alongY = Math.Max(workingArea.Top, Math.Min(bounds.Y, workingArea.Bottom - bounds.Height));
            switch (edge)
            {
                case DockEdge.Left: return new Point(workingArea.Left - (bounds.Width - EdgeSliverSize), alongY);
                case DockEdge.Right: return new Point(workingArea.Right - EdgeSliverSize, alongY);
                case DockEdge.Top: return new Point(alongX, workingArea.Top - (bounds.Height - EdgeSliverSize));
                case DockEdge.Bottom: return new Point(alongX, workingArea.Bottom - EdgeSliverSize);
                default: return bounds.Location;
            }
        }

        private static WidgetLayout GetDockLayout(DockEdge edge)
        {
            return edge == DockEdge.Left || edge == DockEdge.Right ? WidgetLayout.Vertical : WidgetLayout.Horizontal;
        }

        private void CompleteDrag()
        {
            CancelDockTimers();
            _settings.Left = Left;
            _settings.Top = Top;
            var wa = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2)).WorkingArea;
            _dockEdge = DetectDockEdge(Bounds, wa);
            _isEdgeHidden = false;

            if (_dockEdge == DockEdge.None)
            {
                _settings.DockedEdge = DockEdge.None;
                // 拖离边缘：动画恢复停靠前的布局。
                if (_preDockLayout.HasValue && _preDockLayout.Value != _currentLayout)
                {
                    var restore = _preDockLayout.Value;
                    _preDockLayout = null;
                    _currentLayout = restore;
                    var size = WidgetSizeHelper.GetSize(restore, DpiHelper.GetDpiScale(this), UserScale);
                    int left = Math.Max(wa.Left, Math.Min(Left, wa.Right - size.Width));
                    int top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - size.Height));
                    UpdateMenuChecks();
                    SlideBoundsTo(new Rectangle(left, top, size.Width, size.Height));
                }
                else
                {
                    _preDockLayout = null;
                }
                _settingsStore.Save(_settings);
                PositionChanged?.Invoke();
                return;
            }

            _dockedBounds = Bounds;
            _settings.DockedEdge = _dockEdge;

            // 贴边隐藏开启时按边缘方向重塑：顶部/底部用横版，左右用竖版；用户布局偏好保留，拖离后恢复。
            if (_settings.EdgeAutoHide)
            {
                var dockLayout = GetDockLayout(_dockEdge);
                if (!_preDockLayout.HasValue) _preDockLayout = _currentLayout;
                if (_currentLayout != dockLayout || Size != WidgetSizeHelper.GetSize(dockLayout, DpiHelper.GetDpiScale(this), UserScale))
                {
                    var size = WidgetSizeHelper.GetSize(dockLayout, DpiHelper.GetDpiScale(this), UserScale);
                    int alongX, alongY;
                    if (_dockEdge == DockEdge.Left || _dockEdge == DockEdge.Right)
                    {
                        alongX = _dockEdge == DockEdge.Left ? wa.Left : wa.Right - size.Width;
                        alongY = Math.Max(wa.Top, Math.Min(Top + Height / 2 - size.Height / 2, wa.Bottom - size.Height));
                    }
                    else
                    {
                        alongX = Math.Max(wa.Left, Math.Min(Left + Width / 2 - size.Width / 2, wa.Right - size.Width));
                        alongY = _dockEdge == DockEdge.Top ? wa.Top : wa.Bottom - size.Height;
                    }
                    _currentLayout = dockLayout;
                    _dockedBounds = new Rectangle(alongX, alongY, size.Width, size.Height);
                    UpdateMenuChecks();
                    _pendingHideAfterSlide = true;
                    SlideBoundsTo(_dockedBounds);
                    _settingsStore.Save(_settings);
                    PositionChanged?.Invoke();
                    return;
                }
            }
            else
            {
                _preDockLayout = null;
            }

            _settingsStore.Save(_settings);
            PositionChanged?.Invoke();
            if (_settings.EdgeAutoHide)
                _hideCountdown.Start();
        }

        private void CancelDockTimers()
        {
            _slideTimer.Stop();
            _hideCountdown.Stop();
            _revealCountdown.Stop();
            _slideResizes = false;
            _pendingHideAfterSlide = false;
        }

        private void SlideBoundsTo(Rectangle target)
        {
            _slideTarget = target;
            _slideResizes = target.Width != Width || target.Height != Height;
            var wa = Screen.FromPoint(new Point(target.X + target.Width / 2, target.Y + target.Height / 2)).WorkingArea;
            _slideBoundsWa = target.Width <= wa.Width && target.Height <= wa.Height ? wa : Rectangle.Empty;
            _slideTimer.Start();
        }

        private void CompleteSlide()
        {
            _slideTimer.Stop();
            if (_slideResizes)
            {
                _slideResizes = false;
                SetRoundedRegion();
                HideMetricToolTip();
                UpdateMetricHitRects();
                ApplyMonitoringSettings();
                Invalidate();
            }
            if (_pendingHideAfterSlide)
            {
                _pendingHideAfterSlide = false;
                if (_settings.EdgeAutoHide && _dockEdge != DockEdge.None)
                    _hideCountdown.Start();
            }
        }

        private void EnterHiddenState(bool animate)
        {
            if (_dockEdge == DockEdge.None) return;
            var wa = Screen.FromPoint(new Point(_dockedBounds.X + _dockedBounds.Width / 2, _dockedBounds.Y + _dockedBounds.Height / 2)).WorkingArea;
            var hidden = GetHiddenPosition(_dockEdge, _dockedBounds, wa);
            _isEdgeHidden = true;
            HideMetricToolTip();
            if (animate) SlideBoundsTo(new Rectangle(hidden, _dockedBounds.Size));
            else { CancelDockTimers(); Location = hidden; }
            // 主动重绘：透明模式下移动窗口不会重新提交位图，收起高亮条需随状态立即出现。
            Invalidate();
        }

        private void ExitHiddenState(bool animate)
        {
            _isEdgeHidden = false;
            HideMetricToolTip();
            if (animate) SlideBoundsTo(_dockedBounds);
            else { CancelDockTimers(); Location = _dockedBounds.Location; }
            // 收起期间跳过了数据重绘，弹出时用最新快照刷一帧。
            Invalidate();
        }

        public Point GetPersistPosition() => _dockEdge == DockEdge.None ? Location : _dockedBounds.Location;

        public void SetEdgeAutoHide(bool enabled)
        {
            _settings.EdgeAutoHide = enabled;
            if (!enabled && _isEdgeHidden) ExitHiddenState(true);
            _settingsStore.Save(_settings);
            UpdateMenuChecks();
        }

        private void ReapplyDockAfterResize()
        {
            if (_dockEdge == DockEdge.None) return;
            var wa = Screen.FromPoint(new Point(_dockedBounds.X + _dockedBounds.Width / 2, _dockedBounds.Y + _dockedBounds.Height / 2)).WorkingArea;
            int left = Left, top = Top;
            int alongX = Math.Max(wa.Left, Math.Min(_dockedBounds.X, wa.Right - Width));
            int alongY = Math.Max(wa.Top, Math.Min(_dockedBounds.Y, wa.Bottom - Height));
            switch (_dockEdge)
            {
                case DockEdge.Left: left = wa.Left; top = alongY; break;
                case DockEdge.Right: left = wa.Right - Width; top = alongY; break;
                case DockEdge.Top: top = wa.Top; left = alongX; break;
                case DockEdge.Bottom: top = wa.Bottom - Height; left = alongX; break;
            }
            _dockedBounds = new Rectangle(left, top, Width, Height);
            Location = new Point(left, top);
            if (_settings.EdgeAutoHide && _isEdgeHidden)
                Location = GetHiddenPosition(_dockEdge, _dockedBounds, wa);
            else _isEdgeHidden = false;
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
            // 收起在边缘时窗口只剩留边条，跳过每秒整帧重绘；告警变色仍由
            // SetAlertState 触发，弹出时 ExitHiddenState 会补一帧最新数据。
            if (Visible && !_isEdgeHidden) Invalidate();
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

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            base.OnInvalidated(e);
            QueueLayeredRender();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque)) return;
            base.OnPaintBackground(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque))
                return;

            DrawWidget(e.Graphics, ClientSize.Width, ClientSize.Height, false);
        }

        private void DrawWidget(Graphics g, int pixelWidth, int pixelHeight, bool layered)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = layered
                ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit
                : System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

            float scale = (Math.Max(1, DeviceDpi) / 96f) * UserScale;
            g.ScaleTransform(scale, scale);
            WidgetRenderer.ClockZone = _settings.ClockZone;

            var bounds = new Rectangle(0, 0, (int)Math.Ceiling(pixelWidth / scale), (int)Math.Ceiling(pixelHeight / scale));
            int threshold = _settings.HighLoadAlertsEnabled ? _settings.HighLoadThresholdPercent : int.MaxValue;
            int backgroundAlpha = GetBackgroundAlpha();

            switch (_currentLayout)
            {
                case WidgetLayout.Horizontal:
                    WidgetRenderer.DrawHorizontal(g, bounds, _snapshot, threshold, _settings.HighLoadAlertsEnabled, _activeAlertMetrics, backgroundAlpha);
                    break;
                case WidgetLayout.Vertical:
                    WidgetRenderer.DrawVertical(g, bounds, _snapshot, threshold, _settings.HighLoadAlertsEnabled, _activeAlertMetrics, backgroundAlpha);
                    break;
                case WidgetLayout.Expanded:
                    WidgetRenderer.DrawExpanded(g, bounds, _snapshot,
                        GetChronologicalHistory(_cpuHistory), GetChronologicalHistory(_ramHistory), GetChronologicalHistory(_vramHistory), _isCloseHover,
                        threshold, _settings.HighLoadAlertsEnabled, _activeAlertMetrics, _selectedProcessMetric, backgroundAlpha);
                    _closeButtonRect = ScaleRect(WidgetRenderer.GetCloseButtonRect(bounds, WidgetLayout.Expanded), scale);
                    break;
                case WidgetLayout.Mini:
                    WidgetRenderer.DrawMini(g, bounds, _snapshot, threshold, _settings.HighLoadAlertsEnabled, _activeAlertMetrics, backgroundAlpha);
                    break;
            }

            DrawSliverEdge(g, bounds, scale);
        }

        private void DrawSliverEdge(Graphics g, Rectangle bounds, float scale)
        {
            if (_dockEdge == DockEdge.None) return;
            // 停靠期间高亮常显，不再"收起才有"：收起时整条留边高亮（便于找到+看告警色），
            // 弹出时在内侧边缘留一条细线指示"已停靠"；高负载告警时两态都转橙。
            bool verticalStrip = _dockEdge == DockEdge.Left || _dockEdge == DockEdge.Right;
            float w = _isEdgeHidden ? Math.Max(3f, EdgeSliverSize / scale) : Math.Max(1.5f, 3f / scale);
            int alpha = _isEdgeHidden ? 215 : 120;
            var strip = verticalStrip
                ? new RectangleF(_dockEdge == DockEdge.Left ? bounds.Right - w : bounds.X, bounds.Y, w, bounds.Height)
                : new RectangleF(bounds.X, _dockEdge == DockEdge.Top ? bounds.Bottom - w : bounds.Y, bounds.Width, w);
            Color color = _activeAlertMetrics != 0 ? Color.FromArgb(255, 174, 91) : Color.FromArgb(67, 142, 255);
            using (var path = WidgetRenderer.CreateRoundedRectPath(bounds, GetCornerRadius(_currentLayout)))
            using (var brush = new SolidBrush(Color.FromArgb(alpha, color)))
            {
                g.SetClip(path);
                g.FillRectangle(brush, strip);
                g.ResetClip();
            }
        }

        private void QueueLayeredRender()
        {
            if (_disposed || IsDisposed || Disposing || !IsHandleCreated || !Visible ||
                !UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque)) return;
            if (Interlocked.CompareExchange(ref _layeredRenderQueued, 1, 0) != 0) return;
            int generation = Volatile.Read(ref _layeredRenderGeneration);

            try
            {
                BeginInvoke((MethodInvoker)(() =>
                {
                    if (generation != Volatile.Read(ref _layeredRenderGeneration)) return;
                    Interlocked.Exchange(ref _layeredRenderQueued, 0);
                    if (!_disposed && !IsDisposed && !Disposing && IsHandleCreated && Visible &&
                        UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque))
                        RenderLayeredSurface();
                }));
            }
            catch (InvalidOperationException)
            {
                if (generation == Volatile.Read(ref _layeredRenderGeneration))
                    Interlocked.Exchange(ref _layeredRenderQueued, 0);
            }
        }

        private void RenderLayeredSurface()
        {
            if (_disposed || IsDisposed || Disposing || !IsHandleCreated || !Visible ||
                !UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque)) return;

            int width = ClientSize.Width;
            int height = ClientSize.Height;
            if (width <= 0 || height <= 0) return;

            try
            {
                if (_layeredSurface == null || _layeredSurface.Width != width || _layeredSurface.Height != height)
                {
                    DisposeLayeredSurface();
                    _layeredSurface = new LayeredWidgetSurface(width, height);
                }

                Graphics g = _layeredSurface.DrawingGraphics;
                g.ResetTransform();
                g.ResetClip();
                g.Clear(Color.Transparent);
                DrawWidget(g, width, height, true);
                if (!_layeredSurface.Present(Handle, Location))
                    System.Diagnostics.Debug.WriteLine("UpdateLayeredWindow failed: " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ExternalException ||
                ex is InvalidOperationException || ex is OutOfMemoryException || ex is System.ComponentModel.Win32Exception)
            {
                System.Diagnostics.Debug.WriteLine("Layered widget render failed: " + ex.Message);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) HideMetricToolTip();
            if (e.Button == MouseButtons.Left)
            {
                CancelDockTimers();
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
                var snapped = SnapPosition(_formStart.X + dx, _formStart.Y + dy, Width, Height);
                Left = snapped.X;
                Top = snapped.Y;
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
                    CompleteDrag();
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

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) == Keys.Control)
            {
                ApplyScale(_settings.ScalePercent + (e.Delta > 0 ? 5 : -5));
                return;
            }
            base.OnMouseWheel(e);
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
            // 鼠标移动高频触发；仅在手型/移动型切换时真正设置 Cursor。
            bool hand = _currentLayout == WidgetLayout.Expanded &&
                (_closeButtonRect.Contains(location) || GetProcessMetricAt(location) >= 0);
            if (hand == _handCursorActive) return;
            _handCursorActive = hand;
            Cursor = hand ? Cursors.Hand : (_settings.Locked ? Cursors.Default : Cursors.SizeAll);
        }

        public WidgetLayout GetCurrentLayout() => _currentLayout;

        private void UpdateMetricHitRects()
        {
            float scale = (Math.Max(1, DeviceDpi) / 96f) * UserScale;
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
                var zone = _settings.ClockZone;
                double behind = ClockDisplay.HoursBehindBeijing(utc, zone);
                string difference = behind == 0 ? "与北京时间一致"
                    : behind > 0 ? $"比北京时间慢 {behind:0.#} 小时"
                    : $"比北京时间快 {Math.Abs(behind):0.#} 小时";
                return ClockDisplay.GetZoneName(zone) + "时间：" + ClockDisplay.FormatTime(utc, zone) + " " + ClockDisplay.GetShortZone(utc, zone) + "\n" +
                    ClockDisplay.FormatFullDate(utc, zone) + "\n" + difference;
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

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _revealCountdown.Stop();
            if (_dockEdge != DockEdge.None && _isEdgeHidden)
                ExitHiddenState(true);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            HideMetricToolTip();
            bool wasCloseHover = _isCloseHover;
            _isCloseHover = false;
            if (!_isLeftPressActive) { Cursor = Cursors.Default; _handCursorActive = false; }
            if (wasCloseHover) Invalidate();
            base.OnMouseLeave(e);
            if (_dockEdge != DockEdge.None && !_isEdgeHidden && _settings.EdgeAutoHide && !_contextMenu.Visible)
                _revealCountdown.Start();
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
            if (Visible && UsesLayeredOpacity(_settings?.Opacity ?? WidgetOpacity.Opaque)) QueueLayeredRender();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            CancelQueuedLayeredRender();
            base.OnHandleDestroyed(e);
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
                _slideTimer?.Stop();
                _slideTimer?.Dispose();
                _hideCountdown?.Stop();
                _hideCountdown?.Dispose();
                _revealCountdown?.Stop();
                _revealCountdown?.Dispose();
                _metricToolTip?.Dispose();
                _contextMenu?.Dispose();
                DisposeLayeredSurface();
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
            CancelDockTimers();
            _dockEdge = DockEdge.None;
            _settings.DockedEdge = DockEdge.None;
            _isEdgeHidden = false;
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
            // DeviceDpi 是 WinForms 自己跟踪的窗口 DPI（混合 DPI 下随窗口所在屏变化），
            // 与 DrawWidget 的缩放口径一致，且不像 CreateGraphics 那样强制建句柄/拿 HDC。
            try
            {
                return Math.Max(1, control.DeviceDpi) / 96f;
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
