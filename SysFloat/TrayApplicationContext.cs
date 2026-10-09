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
            aboutItem.Click += (s, e) => MessageBox.Show("SysFloat v1.0\n轻量级系统监控悬浮窗",
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
            for (int i = 0; i < menu.Items.Count; i++)
            {
                if (menu.Items[i] is ToolStripMenuItem item)
                {
                    if (item.Text == "横版")
                        item.Checked = _widgetForm.GetCurrentLayout() == WidgetLayout.Horizontal;
                    else if (item.Text == "竖版")
                        item.Checked = _widgetForm.GetCurrentLayout() == WidgetLayout.Vertical;
                    else if (item.Text == "展开版")
                        item.Checked = _widgetForm.GetCurrentLayout() == WidgetLayout.Expanded;
                    else if (item.Text == "始终置顶")
                        item.Checked = _settings.AlwaysOnTop;
                    else if (item.Text == "锁定位置")
                        item.Checked = _settings.Locked;
                    else if (item.Text == "背景不透明")
                        item.Checked = _settings.Opacity == WidgetOpacity.Opaque;
                    else if (item.Text == "背景 75%")
                        item.Checked = _settings.Opacity == WidgetOpacity.Percent75;
                    else if (item.Text == "背景 50%")
                        item.Checked = _settings.Opacity == WidgetOpacity.Percent50;
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
            _notifyIcon.Text = $"SysFloat\nCPU {FormatPercent(snapshot.CpuPercent)} · RAM {FormatPercent(snapshot.MemoryPercent)} · VRAM {FormatPercent(snapshot.VramAvailable ? snapshot.VramPercent : -1)}";

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
            _settings.Left = _widgetForm.Left;
            _settings.Top = _widgetForm.Top;
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
