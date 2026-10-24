using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SysFloat.Configuration;
using SysFloat.Monitoring;
using SysFloat.UI;

namespace SysFloat
{
    public class TrayApplicationContext : ApplicationContext
    {
        private NotifyIcon _notifyIcon;
        private MonitorService _monitorService;
        private SettingsStore _settingsStore;
        private AppSettings _settings;
        private WidgetForm _widgetForm;

        private Icon _normalIcon;
        private Icon _alertIcon;
        private Icon _disabledIcon;

        private readonly HighLoadAlertTracker _alertTracker = new HighLoadAlertTracker();
        private long _lastPopupAt = -1;
        private bool _closing;

        public TrayApplicationContext(SettingsStore settingsStore = null)
        {
            _settingsStore = settingsStore ?? new SettingsStore();
            _settings = _settingsStore.Load();
            _settings.Normalize();
            _monitorService = new MonitorService();

            CreateIcons();
            CreateTrayIcon();
            CreateWidgetForm();

            _monitorService.OnDataUpdated += OnDataUpdated;
            _monitorService.Start();

            var timer = new System.Windows.Forms.Timer();
            timer.Interval = 200;
            timer.Tick += (s, e) =>
            {
                timer.Dispose();
                if (_settings.Visible)
                    _widgetForm.ShowWidget();
            };
            timer.Start();
        }

        private void CreateIcons()
        {
            _normalIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                ?? (Icon)SystemIcons.Application.Clone();
            _alertIcon = CreateStatusIcon(_normalIcon, Color.FromArgb(255, 159, 50));
            _disabledIcon = CreateStatusIcon(_normalIcon, Color.FromArgb(100, 108, 120));
        }

        private static Icon CreateStatusIcon(Icon sourceIcon, Color color)
        {
            using (var source = sourceIcon.ToBitmap())
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            using (var attributes = new ImageAttributes())
            {
                // Preserve the logo silhouette and transparency for status colors.
                attributes.SetColorMatrix(new ColorMatrix(new float[][]
                {
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 },
                    new float[] { color.R / 255f, color.G / 255f, color.B / 255f, 0, 1 }
                }));
                g.DrawImage(source, new Rectangle(0, 0, 32, 32),
                    0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);

                var handle = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        private void CreateTrayIcon()
        {
            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = _normalIcon;
            _notifyIcon.Text = "SysFloat";
            _notifyIcon.Visible = true;

            _notifyIcon.MouseClick += OnTrayMouseClick;
            _notifyIcon.MouseDoubleClick += OnTrayMouseDoubleClick;

            _notifyIcon.ContextMenuStrip = CreateTrayMenu();
        }

        private ContextMenuStrip CreateTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();

            var showItem = new ToolStripMenuItem("显示悬浮窗");
            showItem.Click += (s, e) => _widgetForm.ShowWidget();
            menu.Items.Add(showItem);

            menu.Items.Add(new ToolStripSeparator());

            var horizontalItem = new ToolStripMenuItem("横版");
            horizontalItem.Click += (s, e) => _widgetForm.ApplyLayout(WidgetLayout.Horizontal);
            menu.Items.Add(horizontalItem);

            var verticalItem = new ToolStripMenuItem("竖版");
            verticalItem.Click += (s, e) => _widgetForm.ApplyLayout(WidgetLayout.Vertical);
            menu.Items.Add(verticalItem);

            var expandedItem = new ToolStripMenuItem("展开版");
            expandedItem.Click += (s, e) => _widgetForm.ApplyLayout(WidgetLayout.Expanded);
            menu.Items.Add(expandedItem);

            var miniItem = new ToolStripMenuItem("迷你版");
            miniItem.Click += (s, e) => _widgetForm.ApplyLayout(WidgetLayout.Mini);
            menu.Items.Add(miniItem);

            var sizeMenu = new ToolStripMenuItem("大小");
            var trayScale75 = new ToolStripMenuItem("75%");
            trayScale75.Click += (s, e) => { _widgetForm.ApplyScale(75); UpdateTrayMenuChecks(menu); };
            sizeMenu.DropDownItems.Add(trayScale75);
            var trayScale100 = new ToolStripMenuItem("100%");
            trayScale100.Click += (s, e) => { _widgetForm.ApplyScale(100); UpdateTrayMenuChecks(menu); };
            sizeMenu.DropDownItems.Add(trayScale100);
            var trayScale125 = new ToolStripMenuItem("125%");
            trayScale125.Click += (s, e) => { _widgetForm.ApplyScale(125); UpdateTrayMenuChecks(menu); };
            sizeMenu.DropDownItems.Add(trayScale125);
            var trayScale150 = new ToolStripMenuItem("150%");
            trayScale150.Click += (s, e) => { _widgetForm.ApplyScale(150); UpdateTrayMenuChecks(menu); };
            sizeMenu.DropDownItems.Add(trayScale150);
            sizeMenu.DropDownItems.Add(new ToolStripSeparator());
            var trayScaleHint = new ToolStripMenuItem("提示：Ctrl+滚轮 微调大小");
            trayScaleHint.Enabled = false;
            sizeMenu.DropDownItems.Add(trayScaleHint);
            menu.Items.Add(sizeMenu);

            menu.Items.Add(new ToolStripSeparator());

            var topMostItem = new ToolStripMenuItem("始终置顶");
            topMostItem.Click += (s, e) =>
            {
                _widgetForm.SetTopMost(!_settings.AlwaysOnTop);
                UpdateTrayMenuChecks(menu);
            };
            menu.Items.Add(topMostItem);

            var lockItem = new ToolStripMenuItem("锁定位置");
            lockItem.Click += (s, e) =>
            {
                _settings.Locked = !_settings.Locked;
                _settingsStore.Save(_settings);
                _widgetForm.UpdateMenuChecks();
                UpdateTrayMenuChecks(menu);
            };
            menu.Items.Add(lockItem);

            var snapItem = new ToolStripMenuItem("边缘吸附");
            snapItem.Click += (s, e) =>
            {
                _settings.SnapToEdges = !_settings.SnapToEdges;
                _settingsStore.Save(_settings);
                _widgetForm.UpdateMenuChecks();
                UpdateTrayMenuChecks(menu);
            };
            menu.Items.Add(snapItem);

            var trayAutoStart = new ToolStripMenuItem("开机自启");
            trayAutoStart.Click += (s, e) =>
            {
                StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
                _widgetForm.UpdateMenuChecks();
                UpdateTrayMenuChecks(menu);
            };
            menu.Items.Add(trayAutoStart);

            var edgeHideItem = new ToolStripMenuItem("贴边隐藏");
            edgeHideItem.Click += (s, e) =>
            {
                _widgetForm.SetEdgeAutoHide(!_settings.EdgeAutoHide);
                UpdateTrayMenuChecks(menu);
            };
            menu.Items.Add(edgeHideItem);

            menu.Items.Add(new ToolStripSeparator());

            var op100 = new ToolStripMenuItem("背景不透明");
            op100.Click += (s, e) => { _widgetForm.SetOpacity(WidgetOpacity.Opaque); UpdateTrayMenuChecks(menu); };
            menu.Items.Add(op100);

            var op75 = new ToolStripMenuItem("背景 75%");
            op75.Click += (s, e) => { _widgetForm.SetOpacity(WidgetOpacity.Percent75); UpdateTrayMenuChecks(menu); };
            menu.Items.Add(op75);

            var op50 = new ToolStripMenuItem("背景 50%");
            op50.Click += (s, e) => { _widgetForm.SetOpacity(WidgetOpacity.Percent50); UpdateTrayMenuChecks(menu); };
            menu.Items.Add(op50);

            menu.Items.Add(new ToolStripSeparator());

            var monitoringItem = new ToolStripMenuItem("监控设置…");
            monitoringItem.Click += (s, e) => ShowMonitoringOptions();
            menu.Items.Add(monitoringItem);

            menu.Items.Add(new ToolStripSeparator());

            var aboutItem = new ToolStripMenuItem("关于");
            aboutItem.Click += (s, e) => MessageBox.Show("SysFloat v1.2.1\n轻量级系统监控悬浮窗",
                "关于 SysFloat", MessageBoxButtons.OK, MessageBoxIcon.Information);
            menu.Items.Add(aboutItem);

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += OnExitClick;
            menu.Items.Add(exitItem);

            menu.Opening += (s, e) => UpdateTrayMenuChecks(menu);

            return menu;
        }

        private void UpdateTrayMenuChecks(ContextMenuStrip menu)
        {
            UpdateTrayItemsChecks(menu.Items);
        }

        private void UpdateTrayItemsChecks(ToolStripItemCollection items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is ToolStripMenuItem item)
                {
                    string baseText = item.Text.Replace("● ", "");
                    if (baseText == "横版")
                        item.Checked = _widgetForm.GetCurrentLayout() == WidgetLayout.Horizontal;
                    else if (baseText == "竖版")
                        item.Checked = _widgetForm.GetCurrentLayout() == WidgetLayout.Vertical;
                    else if (baseText == "展开版")
                        item.Checked = _widgetForm.GetCurrentLayout() == WidgetLayout.Expanded;
                    else if (baseText == "迷你版")
                        item.Checked = _widgetForm.GetCurrentLayout() == WidgetLayout.Mini;
                    else if (baseText == "始终置顶")
                        item.Checked = _settings.AlwaysOnTop;
                    else if (baseText == "锁定位置")
                        item.Checked = _settings.Locked;
                    else if (baseText == "边缘吸附")
                        item.Checked = _settings.SnapToEdges;
                    else if (baseText == "贴边隐藏")
                        item.Checked = _settings.EdgeAutoHide;
                    else if (baseText == "开机自启")
                        item.Checked = StartupRegistration.IsEnabled();
                    else if (baseText == "背景不透明")
                        item.Checked = _settings.Opacity == WidgetOpacity.Opaque;
                    else if (baseText == "背景 75%")
                        item.Checked = _settings.Opacity == WidgetOpacity.Percent75;
                    else if (baseText == "背景 50%")
                        item.Checked = _settings.Opacity == WidgetOpacity.Percent50;
                    else if (baseText == "75%")
                        item.Checked = _settings.ScalePercent == 75;
                    else if (baseText == "100%")
                        item.Checked = _settings.ScalePercent == 100;
                    else if (baseText == "125%")
                        item.Checked = _settings.ScalePercent == 125;
                    else if (baseText == "150%")
                        item.Checked = _settings.ScalePercent == 150;
                    if (item.DropDownItems.Count > 0)
                        UpdateTrayItemsChecks(item.DropDownItems);
                }
            }
        }

        private void CreateWidgetForm()
        {
            _widgetForm = new WidgetForm(_monitorService, _settingsStore, _settings);
            _widgetForm.CloseClicked += () => OnExitClick(this, EventArgs.Empty);
            _widgetForm.MonitoringOptionsRequested += ShowMonitoringOptions;
            _widgetForm.SetInitialPosition();
            _ = _widgetForm.Handle;
            _widgetForm.ApplyMonitoringSettings();
        }

        private void ShowMonitoringOptions()
        {
            using (var dialog = new MonitoringOptionsDialog(_settings))
            {
                dialog.TopMost = _settings.AlwaysOnTop;
                if (dialog.ShowDialog() != DialogResult.OK) return;
            }
            _settingsStore.Save(_settings);
            _alertTracker.Reset();
            _notifyIcon.Icon = _normalIcon;
            _widgetForm.SetAlertState(new AlertEvaluation(false, false, string.Empty));
            _widgetForm.ApplyMonitoringSettings();
        }

        private void OnTrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _widgetForm.ToggleVisibility();
            }
        }

        private void OnTrayMouseDoubleClick(object sender, MouseEventArgs e)
        {
            _widgetForm.MoveToVisibleArea();
        }

        private void OnDataUpdated(MetricSnapshot snapshot)
        {
            var form = _widgetForm;
            if (_closing || form == null || form.IsDisposed || !form.IsHandleCreated) return;
            if (form.InvokeRequired)
            {
                try { form.BeginInvoke(new Action(() => OnDataUpdated(snapshot))); }
                catch (InvalidOperationException) { }
                return;
            }

            long now = Environment.TickCount64;
            var alert = _alertTracker.Evaluate(snapshot, _settings, now);
            _widgetForm.SetAlertState(alert);
            bool allOk = snapshot.CpuPercent >= 0 && snapshot.MemoryPercent >= 0;
            var desiredIcon = alert.IsHighLoad ? _alertIcon : allOk ? _normalIcon : _disabledIcon;
            if (!ReferenceEquals(_notifyIcon.Icon, desiredIcon)) _notifyIcon.Icon = desiredIcon;
            // 托盘文本只在内容变化时更新（通常是整百分比变化才变），避免每秒无效赋值。
            string trayText = $"SysFloat\nCPU {FormatPercent(snapshot.CpuPercent)} · RAM {FormatPercent(snapshot.MemoryPercent)} · VRAM {FormatPercent(snapshot.VramAvailable ? snapshot.VramPercent : -1)}";
            if (!string.Equals(_notifyIcon.Text, trayText, StringComparison.Ordinal))
                _notifyIcon.Text = trayText;

            if (alert.JustActivated && _settings.AlertPopupEnabled &&
                (_lastPopupAt < 0 || now - _lastPopupAt >= 300000))
            {
                _lastPopupAt = now;
                _notifyIcon.ShowBalloonTip(5000, "SysFloat · 高占用提醒", alert.Message, ToolTipIcon.Warning);
            }
        }

        private static string FormatPercent(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value < 0
                ? "--" : $"{Math.Round(Math.Min(value, 100)):0}%";
        }

        private void OnExitClick(object sender, EventArgs e)
        {
            if (_closing) return;
            _closing = true;
            var persistPosition = _widgetForm.GetPersistPosition();
            _settings.Left = persistPosition.X;
            _settings.Top = persistPosition.Y;
            _settingsStore.Save(_settings);

            _monitorService.Stop();
            _monitorService.Dispose();
            _widgetForm.Close();
            _notifyIcon.Visible = false;
            Application.Exit();
        }

        protected override void ExitThreadCore()
        {
            _closing = true;
            if (_monitorService != null)
            {
                _monitorService.OnDataUpdated -= OnDataUpdated;
                _monitorService.Stop();
                _monitorService.Dispose();
            }
            _widgetForm?.Dispose();
            _notifyIcon?.ContextMenuStrip?.Dispose();
            _notifyIcon?.Dispose();
            _normalIcon?.Dispose();
            _alertIcon?.Dispose();
            _disabledIcon?.Dispose();
            base.ExitThreadCore();
        }
    }
}
