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
            UIStrings.SetLanguage(_settings.Language);
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

        private ToolStripMenuItem TrayMenuItem(string key, EventHandler onClick)
        {
            var item = new ToolStripMenuItem(UIStrings.Get(key)) { Tag = key };
            if (onClick != null) item.Click += onClick;
            return item;
        }

        private ContextMenuStrip CreateTrayMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();

            menu.Items.Add(TrayMenuItem("menu.show", (s, e) => _widgetForm.ShowWidget()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(TrayMenuItem("menu.horizontal", (s, e) => _widgetForm.ApplyLayout(WidgetLayout.Horizontal)));
            menu.Items.Add(TrayMenuItem("menu.vertical", (s, e) => _widgetForm.ApplyLayout(WidgetLayout.Vertical)));
            menu.Items.Add(TrayMenuItem("menu.expanded", (s, e) => _widgetForm.ApplyLayout(WidgetLayout.Expanded)));
            menu.Items.Add(TrayMenuItem("menu.mini", (s, e) => _widgetForm.ApplyLayout(WidgetLayout.Mini)));

            var sizeMenu = TrayMenuItem("menu.size", null);
            sizeMenu.DropDownItems.Add(TrayMenuItem("menu.scale75", (s, e) => { _widgetForm.ApplyScale(75); UpdateTrayMenuChecks(menu); }));
            sizeMenu.DropDownItems.Add(TrayMenuItem("menu.scale100", (s, e) => { _widgetForm.ApplyScale(100); UpdateTrayMenuChecks(menu); }));
            sizeMenu.DropDownItems.Add(TrayMenuItem("menu.scale125", (s, e) => { _widgetForm.ApplyScale(125); UpdateTrayMenuChecks(menu); }));
            sizeMenu.DropDownItems.Add(TrayMenuItem("menu.scale150", (s, e) => { _widgetForm.ApplyScale(150); UpdateTrayMenuChecks(menu); }));
            sizeMenu.DropDownItems.Add(new ToolStripSeparator());
            var trayScaleHint = TrayMenuItem("menu.sizeHint", null);
            trayScaleHint.Enabled = false;
            sizeMenu.DropDownItems.Add(trayScaleHint);
            menu.Items.Add(sizeMenu);

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(TrayMenuItem("menu.topMost", (s, e) =>
            {
                _widgetForm.SetTopMost(!_settings.AlwaysOnTop);
                UpdateTrayMenuChecks(menu);
            }));
            menu.Items.Add(TrayMenuItem("menu.lock", (s, e) =>
            {
                _settings.Locked = !_settings.Locked;
                _settingsStore.Save(_settings);
                _widgetForm.UpdateMenuChecks();
                UpdateTrayMenuChecks(menu);
            }));
            menu.Items.Add(TrayMenuItem("menu.snap", (s, e) =>
            {
                _settings.SnapToEdges = !_settings.SnapToEdges;
                _settingsStore.Save(_settings);
                _widgetForm.UpdateMenuChecks();
                UpdateTrayMenuChecks(menu);
            }));
            menu.Items.Add(TrayMenuItem("menu.autoStart", (s, e) =>
            {
                StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled());
                bool actual = StartupRegistration.IsEnabled();
                _widgetForm.UpdateMenuChecks();
                UpdateTrayMenuChecks(menu);
                ShowAutoStartFeedback(actual);
            }));
            menu.Items.Add(TrayMenuItem("menu.edgeHide", (s, e) =>
            {
                _widgetForm.SetEdgeAutoHide(!_settings.EdgeAutoHide);
                UpdateTrayMenuChecks(menu);
            }));

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(TrayMenuItem("menu.opacityOpaque", (s, e) => { _widgetForm.SetOpacity(WidgetOpacity.Opaque); UpdateTrayMenuChecks(menu); }));
            menu.Items.Add(TrayMenuItem("menu.opacity75", (s, e) => { _widgetForm.SetOpacity(WidgetOpacity.Percent75); UpdateTrayMenuChecks(menu); }));
            menu.Items.Add(TrayMenuItem("menu.opacity50", (s, e) => { _widgetForm.SetOpacity(WidgetOpacity.Percent50); UpdateTrayMenuChecks(menu); }));

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(TrayMenuItem("menu.monitorSettings", (s, e) => ShowMonitoringOptions()));

            menu.Items.Add(new ToolStripSeparator());

            menu.Items.Add(TrayMenuItem("menu.about", (s, e) => MessageBox.Show(
                "SysFloat v1.2.2\n" + UIStrings.Get("about.body"), UIStrings.Get("about.title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information)));
            menu.Items.Add(TrayMenuItem("menu.exit", OnExitClick));

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
                    if (item.Tag is string key)
                    {
                        bool isChecked;
                        switch (key)
                        {
                            case "menu.horizontal": isChecked = _widgetForm.GetCurrentLayout() == WidgetLayout.Horizontal; break;
                            case "menu.vertical": isChecked = _widgetForm.GetCurrentLayout() == WidgetLayout.Vertical; break;
                            case "menu.expanded": isChecked = _widgetForm.GetCurrentLayout() == WidgetLayout.Expanded; break;
                            case "menu.mini": isChecked = _widgetForm.GetCurrentLayout() == WidgetLayout.Mini; break;
                            case "menu.topMost": isChecked = _settings.AlwaysOnTop; break;
                            case "menu.lock": isChecked = _settings.Locked; break;
                            case "menu.snap": isChecked = _settings.SnapToEdges; break;
                            case "menu.edgeHide": isChecked = _settings.EdgeAutoHide; break;
                            case "menu.autoStart": isChecked = StartupRegistration.IsEnabled(); break;
                            case "menu.opacityOpaque": isChecked = _settings.Opacity == WidgetOpacity.Opaque; break;
                            case "menu.opacity75": isChecked = _settings.Opacity == WidgetOpacity.Percent75; break;
                            case "menu.opacity50": isChecked = _settings.Opacity == WidgetOpacity.Percent50; break;
                            case "menu.scale75": isChecked = _settings.ScalePercent == 75; break;
                            case "menu.scale100": isChecked = _settings.ScalePercent == 100; break;
                            case "menu.scale125": isChecked = _settings.ScalePercent == 125; break;
                            case "menu.scale150": isChecked = _settings.ScalePercent == 150; break;
                            default: isChecked = false; break;
                        }
                        item.Checked = isChecked;
                        item.Text = isChecked ? "● " + UIStrings.Get(key) : UIStrings.Get(key);
                    }
                    if (item.DropDownItems.Count > 0)
                        UpdateTrayItemsChecks(item.DropDownItems);
                }
            }
        }

        /// <summary>语言切换后刷新托盘菜单文本（悬浮窗菜单由 WidgetForm 自行刷新）。</summary>
        private void RefreshTrayLanguage()
        {
            if (_notifyIcon?.ContextMenuStrip == null) return;
            void Apply(ToolStripItemCollection items)
            {
                foreach (ToolStripItem entry in items)
                {
                    if (entry is ToolStripMenuItem item)
                    {
                        if (item.Tag is string key) item.Text = UIStrings.Get(key);
                        if (item.DropDownItems.Count > 0) Apply(item.DropDownItems);
                    }
                }
            }
            Apply(_notifyIcon.ContextMenuStrip.Items);
            UpdateTrayMenuChecks(_notifyIcon.ContextMenuStrip);
        }

        private void CreateWidgetForm()
        {
            _widgetForm = new WidgetForm(_monitorService, _settingsStore, _settings);
            _widgetForm.CloseClicked += () => OnExitClick(this, EventArgs.Empty);
            _widgetForm.MonitoringOptionsRequested += ShowMonitoringOptions;
            _widgetForm.AutoStartChanged += ShowAutoStartFeedback;
            _widgetForm.SetInitialPosition();
            _ = _widgetForm.Handle;
            _widgetForm.ApplyMonitoringSettings();
        }

        // 开关自启后弹托盘气泡确认；菜单点完即关，没有这条反馈用户无从得知结果。
        // 注：Windows 免打扰/专注模式下系统可能不显示气泡，重开菜单看 ● 仍可确认状态。
        private void ShowAutoStartFeedback(bool enabled)
        {
            _notifyIcon.ShowBalloonTip(2500, "SysFloat",
                UIStrings.Get(enabled ? "toast.autoStartOn" : "toast.autoStartOff"), ToolTipIcon.Info);
        }

        private void ShowMonitoringOptions()
        {
            using (var dialog = new MonitoringOptionsDialog(_settings))
            {
                dialog.TopMost = _settings.AlwaysOnTop;
                if (dialog.ShowDialog() != DialogResult.OK) return;
            }
            _settingsStore.Save(_settings);
            if (UIStrings.Language != _settings.Language)
            {
                UIStrings.SetLanguage(_settings.Language);
                _widgetForm.RefreshLanguage();
                RefreshTrayLanguage();
            }
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
            string trayText = $"SysFloat\nCPU {FormatPercent(snapshot.CpuPercent)} · RAM {FormatPercent(snapshot.MemoryPercent)} · VRAM {GpuDisplay.Summary(snapshot)}";
            if (trayText.Length > 63) trayText = trayText.Substring(0, 62) + "…";
            if (!string.Equals(_notifyIcon.Text, trayText, StringComparison.Ordinal))
                _notifyIcon.Text = trayText;

            if (alert.JustActivated && _settings.AlertPopupEnabled &&
                (_lastPopupAt < 0 || now - _lastPopupAt >= 300000))
            {
                _lastPopupAt = now;
                _notifyIcon.ShowBalloonTip(5000, UIStrings.Get("alert.title"), alert.Message, ToolTipIcon.Warning);
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
