using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SysFloat.Configuration;

namespace SysFloat.UI
{
    public sealed class MonitoringOptionsDialog : Form
    {
        private const int LogicalWidth = 520;
        private const int LogicalHeight = 510;
        private static readonly Color WindowColor = Color.FromArgb(32, 38, 49);
        private static readonly Color CardColor = Color.FromArgb(39, 46, 59);
        private static readonly Color CardBorder = Color.FromArgb(53, 62, 78);
        private static readonly Color MainText = Color.FromArgb(241, 244, 249);
        private static readonly Color MutedText = Color.FromArgb(166, 176, 193);
        private static readonly Color Accent = Color.FromArgb(72, 145, 239);

        private readonly AppSettings _settings;
        private readonly ToggleControl _powerSaving;
        private readonly IntervalSegmentControl _interval;
        private readonly ToggleControl _alerts;
        private readonly NumberField _threshold;
        private readonly NumberField _duration;
        private readonly ToggleControl _popup;
        private readonly Button _save;
        private readonly Button _cancel;
        private readonly Button _close;
        private readonly List<Font> _ownedFonts = new List<Font>();
        private float _scale = 1f;

        public MonitoringOptionsDialog(AppSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            Text = "监控设置";
            FormBorderStyle = FormBorderStyle.None;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = WindowColor;
            ForeColor = MainText;
            DoubleBuffered = true;
            KeyPreview = true;

            _powerSaving = new ToggleControl("省资源模式", _settings.PowerSavingMode);
            _interval = new IntervalSegmentControl(_settings.SamplingIntervalSeconds);
            _alerts = new ToggleControl("启用提醒", _settings.HighLoadAlertsEnabled);
            _threshold = new NumberField(1, 100, _settings.HighLoadThresholdPercent, "触发阈值");
            _duration = new NumberField(1, 300, _settings.HighLoadDurationSeconds, "持续时间");
            _popup = new ToggleControl("托盘通知：恢复后可再次提醒（至少间隔 5 分钟）", _settings.AlertPopupEnabled);

            _save = new Button { Text = "保存", DialogResult = DialogResult.None, FlatStyle = FlatStyle.Flat,
                BackColor = Accent, ForeColor = Color.White, UseVisualStyleBackColor = false, TabIndex = 6 };
            _save.FlatAppearance.BorderSize = 0;
            _save.FlatAppearance.MouseOverBackColor = Color.FromArgb(91, 160, 248);
            _save.Click += (s, e) =>
            {
                SaveOptions();
                DialogResult = DialogResult.OK;
                Close();
            };

            _cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 53, 67), ForeColor = MainText, UseVisualStyleBackColor = false, TabIndex = 7 };
            _cancel.FlatAppearance.BorderColor = CardBorder;
            _cancel.FlatAppearance.BorderSize = 1;
            _cancel.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 65, 81);

            _close = new Button { Text = "×", DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat,
                BackColor = WindowColor, ForeColor = MutedText, UseVisualStyleBackColor = false, TabStop = false };
            _close.FlatAppearance.BorderSize = 0;
            _close.FlatAppearance.MouseOverBackColor = Color.FromArgb(57, 65, 80);

            Controls.AddRange(new Control[] { _powerSaving, _interval, _alerts, _threshold, _duration, _popup, _save, _cancel, _close });
            foreach (var control in new Control[] { _powerSaving, _interval, _alerts, _threshold, _duration, _popup })
                control.BackColor = CardColor;
            AcceptButton = _save;
            CancelButton = _cancel;
            _powerSaving.CheckedChanged += (s, e) => UpdateEnabledState();
            _alerts.CheckedChanged += (s, e) => UpdateEnabledState();

            using (var graphics = CreateGraphics()) _scale = Math.Max(1f, graphics.DpiX / 96f);
            ApplyScaleAndLayout();
            UpdateEnabledState();
        }

        private void ApplyScaleAndLayout()
        {
            _scale = Math.Max(1f, _scale);
            foreach (var previousFont in _ownedFonts) previousFont.Dispose();
            _ownedFonts.Clear();
            ClientSize = ScaledSize(LogicalWidth, LogicalHeight);

            SetControl(_powerSaving, 360, 86, 124, 30, 12f, FontStyle.Regular, 0);
            SetControl(_interval, 142, 150, 222, 34, 12.5f, FontStyle.Bold, 1);
            SetControl(_alerts, 377, 243, 106, 30, 12f, FontStyle.Regular, 2);
            SetControl(_threshold, 372, 306, 92, 34, 14f, FontStyle.Bold, 3);
            SetControl(_duration, 372, 350, 92, 34, 14f, FontStyle.Bold, 4);
            SetControl(_popup, 34, 397, 452, 38, 11.5f, FontStyle.Regular, 5);
            SetControl(_save, 336, 461, 82, 32, 13.5f, FontStyle.Bold, 6);
            SetControl(_cancel, 428, 461, 64, 32, 13f, FontStyle.Regular, 7);
            SetControl(_close, 474, 13, 32, 30, 18f, FontStyle.Regular, -1);

            using (var path = CreateRoundPath(new Rectangle(0, 0, Width, Height), Scaled(14)))
            {
                var previous = Region;
                Region = new Region(path);
                previous?.Dispose();
            }
            Invalidate();
        }

        private void SetControl(Control control, int x, int y, int width, int height, float fontPixels, FontStyle style, int tabIndex)
        {
            control.SetBounds(Scaled(x), Scaled(y), Scaled(width), Scaled(height));
            if (tabIndex >= 0) control.TabIndex = tabIndex;
            var font = new Font("Microsoft YaHei UI", fontPixels * _scale, style, GraphicsUnit.Pixel);
            _ownedFonts.Add(font);
            control.Font = font;
        }

        private int Scaled(int value) => (int)Math.Round(value * _scale);
        private Size ScaledSize(int width, int height) => new Size(Scaled(width), Scaled(height));

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            DrawCard(e.Graphics, new Rectangle(18, 74, LogicalWidth - 36, 146));
            DrawCard(e.Graphics, new Rectangle(18, 232, LogicalWidth - 36, 214));

            DrawText(e.Graphics, "监控设置", 22, 12, 170, 28, 19f, FontStyle.Bold, MainText);
            DrawText(e.Graphics, "SysFloat  ·  按需调整采样和提醒", 23, 42, 330, 18, 11.5f, FontStyle.Regular, MutedText);

            DrawText(e.Graphics, "采样与性能", 36, 87, 180, 20, 14.5f, FontStyle.Bold, MainText);
            DrawText(e.Graphics, "普通模式每秒采样；省资源模式按所选间隔采样。", 36, 116, 442, 20, 11.5f, FontStyle.Regular, MutedText);
            DrawText(e.Graphics, "展开版才刷新进程排行；隐藏时停止排行采集。", 36, 132, 442, 18, 11.5f, FontStyle.Regular, MutedText);
            DrawText(e.Graphics, "采样间隔", 36, 156, 92, 22, 12f, FontStyle.Regular, MutedText);

            DrawText(e.Graphics, "高占用提醒", 36, 245, 180, 20, 14.5f, FontStyle.Bold, MainText);
            DrawText(e.Graphics, "超过阈值并持续指定时间后提醒。", 36, 273, 442, 20, 11.5f, FontStyle.Regular, MutedText);
            DrawText(e.Graphics, "触发阈值", 36, 312, 150, 22, 12f, FontStyle.Regular, MutedText);
            DrawText(e.Graphics, "%", 472, 312, 24, 22, 12f, FontStyle.Regular, MutedText);
            DrawText(e.Graphics, "持续时间", 36, 356, 150, 22, 12f, FontStyle.Regular, MutedText);
            DrawText(e.Graphics, "秒", 472, 356, 24, 22, 12f, FontStyle.Regular, MutedText);
        }

        private void DrawCard(Graphics graphics, Rectangle logicalBounds)
        {
            Rectangle bounds = ScaleRect(logicalBounds);
            using (var path = CreateRoundPath(bounds, Scaled(12)))
            using (var fill = new SolidBrush(CardColor))
            using (var border = new Pen(CardBorder, Math.Max(1f, _scale)))
            {
                graphics.FillPath(fill, path);
                graphics.DrawPath(border, path);
            }
        }

        private void DrawText(Graphics graphics, string text, int x, int y, int width, int height,
            float fontPixels, FontStyle style, Color color)
        {
            using (var font = new Font("Microsoft YaHei UI", fontPixels * _scale, style, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
                graphics.DrawString(text, font, brush, new RectangleF(Scaled(x), Scaled(y), Scaled(width), Scaled(height)));
        }

        private Rectangle ScaleRect(Rectangle rect) => new Rectangle(Scaled(rect.X), Scaled(rect.Y), Scaled(rect.Width), Scaled(rect.Height));

        private static GraphicsPath CreateRoundPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void UpdateEnabledState()
        {
            _interval.Enabled = _powerSaving.Checked;
            _threshold.Enabled = _alerts.Checked;
            _duration.Enabled = _alerts.Checked;
            _popup.Enabled = _alerts.Checked;
        }

        private void SaveOptions()
        {
            _settings.PowerSavingMode = _powerSaving.Checked;
            _settings.SamplingIntervalSeconds = _interval.SelectedSeconds;
            _settings.HighLoadAlertsEnabled = _alerts.Checked;
            _settings.HighLoadThresholdPercent = _threshold.Value;
            _settings.HighLoadDurationSeconds = _duration.Value;
            _settings.AlertPopupEnabled = _popup.Checked;
            _settings.Normalize();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            _scale = Math.Max(1f, e.DeviceDpiNew / 96f);
            ApplyScaleAndLayout();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var font in _ownedFonts) font.Dispose();
                _ownedFonts.Clear();
            }
            base.Dispose(disposing);
        }

        private sealed class ToggleControl : Control
        {
            private bool _checked;
            public event EventHandler CheckedChanged;
            public bool Checked
            {
                get => _checked;
                set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
            }

            public ToggleControl(string text, bool isChecked)
            {
                Text = text;
                _checked = isChecked;
                TabStop = true;
                AccessibleRole = AccessibleRole.CheckButton;
                AccessibleName = text;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                float scale = Font.Size / 12f;
                int trackW = Math.Max(34, (int)Math.Round(36 * scale));
                int trackH = Math.Max(18, (int)Math.Round(20 * scale));
                int top = (Height - trackH) / 2;
                int left = 0;
                Color track = !Enabled ? Color.FromArgb(59, 66, 78) : _checked ? Accent : Color.FromArgb(75, 85, 101);
                using (var path = CreateRoundPath(new Rectangle(left, top, trackW, trackH), trackH / 2))
                using (var brush = new SolidBrush(track)) e.Graphics.FillPath(brush, path);
                int knob = trackH - 4;
                int knobX = _checked ? trackW - knob - 2 : 2;
                using (var brush = new SolidBrush(Enabled ? Color.White : Color.FromArgb(177, 184, 195)))
                    e.Graphics.FillEllipse(brush, left + knobX, top + 2, knob, knob);
                using (var brush = new SolidBrush(Enabled ? MainText : MutedText))
                    e.Graphics.DrawString(Text, Font, brush, trackW + (int)Math.Round(9 * scale), (Height - Font.Height) / 2f);
                if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
            }

            protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; }
            protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Enabled && e.Button == MouseButtons.Left) Focus(); }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
                base.OnKeyDown(e);
            }
        }

        private sealed class IntervalSegmentControl : Control
        {
            private readonly int[] _options = { 1, 3, 5 };
            public int SelectedSeconds { get; private set; }

            public IntervalSegmentControl(int selectedSeconds)
            {
                SelectedSeconds = Array.IndexOf(_options, selectedSeconds) >= 0 ? selectedSeconds : 3;
                TabStop = true;
                AccessibleRole = AccessibleRole.List;
                AccessibleName = "采样间隔";
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = CreateRoundPath(bounds, Math.Max(6, Height / 4)))
                using (var brush = new SolidBrush(Enabled ? Color.FromArgb(31, 37, 48) : Color.FromArgb(34, 39, 48)))
                using (var pen = new Pen(CardBorder))
                {
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(pen, path);
                }
                int segmentWidth = Width / _options.Length;
                for (int i = 0; i < _options.Length; i++)
                {
                    var rect = new Rectangle(i * segmentWidth + 2, 2, segmentWidth - 3, Height - 4);
                    bool selected = _options[i] == SelectedSeconds;
                    if (selected && Enabled)
                    {
                        using (var path = CreateRoundPath(rect, Math.Max(5, Height / 5)))
                        using (var brush = new SolidBrush(Accent)) e.Graphics.FillPath(brush, path);
                    }
                    using (var brush = new SolidBrush(!Enabled ? Color.FromArgb(105, 113, 128) : selected ? Color.White : MutedText))
                    {
                        string text = _options[i] + " 秒";
                        var size = e.Graphics.MeasureString(text, Font);
                        e.Graphics.DrawString(text, Font, brush, rect.X + (rect.Width - size.Width) / 2f, rect.Y + (rect.Height - size.Height) / 2f);
                    }
                }
                if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (!Enabled || e.Button != MouseButtons.Left) return;
                Focus();
                int index = Math.Max(0, Math.Min(_options.Length - 1, e.X * _options.Length / Math.Max(1, Width)));
                SelectedSeconds = _options[index];
                Invalidate();
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                int index = Array.IndexOf(_options, SelectedSeconds);
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
                {
                    index = Math.Max(0, Math.Min(_options.Length - 1, index + (e.KeyCode == Keys.Right ? 1 : -1)));
                    SelectedSeconds = _options[index];
                    Invalidate();
                    e.Handled = true;
                }
                base.OnKeyDown(e);
            }
        }

        private sealed class NumberField : Control
        {
            private int _value;
            private bool _hoverMinus;
            private bool _hoverPlus;
            private string _typedValue;
            public int Minimum { get; }
            public int Maximum { get; }
            public int Value
            {
                get => _value;
                set { int next = Math.Max(Minimum, Math.Min(Maximum, value)); if (_value == next) return; _value = next; Invalidate(); }
            }

            public NumberField(int minimum, int maximum, int value, string accessibleName)
            {
                Minimum = minimum;
                Maximum = maximum;
                _value = Math.Max(minimum, Math.Min(maximum, value));
                TabStop = true;
                AccessibleRole = AccessibleRole.SpinButton;
                AccessibleName = accessibleName;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
                float scale = Font.Size / 14f;
                using (var path = CreateRoundPath(bounds, Math.Max(5, (int)Math.Round(6 * scale))))
                using (var brush = new SolidBrush(Enabled ? Color.FromArgb(31, 37, 48) : Color.FromArgb(34, 39, 48)))
                using (var pen = new Pen(Focused ? Accent : CardBorder, Focused ? 1.5f : 1f))
                {
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(pen, path);
                }
                int actionWidth = Math.Min(Math.Max(23, (int)Math.Round(23 * scale)), Width / 4);
                int dividerX = Width - actionWidth;
                using (var pen = new Pen(CardBorder)) e.Graphics.DrawLine(pen, dividerX, 1, dividerX, Height - 1);
                int half = Height / 2;
                using (var pen = new Pen(Enabled ? MutedText : Color.FromArgb(100, 107, 120), 1.4f))
                {
                    if (_hoverPlus && Enabled) DrawActionHighlight(e.Graphics, dividerX + 1, 1, actionWidth - 2, half - 1);
                    if (_hoverMinus && Enabled) DrawActionHighlight(e.Graphics, dividerX + 1, half, actionWidth - 2, Height - half - 1);
                    e.Graphics.DrawLine(pen, dividerX + actionWidth / 2 - 3, half / 2, dividerX + actionWidth / 2 + 3, half / 2);
                    e.Graphics.DrawLine(pen, dividerX + actionWidth / 2 - 3, half + half / 2, dividerX + actionWidth / 2 + 3, half + half / 2);
                    e.Graphics.DrawLine(pen, dividerX + actionWidth / 2, half / 2 - 3, dividerX + actionWidth / 2, half / 2 + 3);
                }
                string text = _typedValue ?? _value.ToString();
                var size = e.Graphics.MeasureString(text, Font);
                using (var brush = new SolidBrush(Enabled ? MainText : MutedText))
                    e.Graphics.DrawString(text, Font, brush, 11, (Height - size.Height) / 2f);
                if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(2, 2, dividerX - 4, Height - 4));
            }

            private static void DrawActionHighlight(Graphics graphics, int x, int y, int width, int height)
            {
                using (var brush = new SolidBrush(Color.FromArgb(53, 62, 78))) graphics.FillRectangle(brush, x, y, width, height);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int actionWidth = Math.Min(Math.Max(23, (int)Math.Round(23 * Font.Size / 14f)), Width / 4);
                int dividerX = Width - actionWidth;
                bool plus = e.X >= dividerX && e.Y < Height / 2;
                bool minus = e.X >= dividerX && e.Y >= Height / 2;
                if (plus != _hoverPlus || minus != _hoverMinus) { _hoverPlus = plus; _hoverMinus = minus; Invalidate(); }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hoverPlus = _hoverMinus = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (!Enabled || e.Button != MouseButtons.Left) return;
                Focus();
                int actionWidth = Math.Min(Math.Max(23, (int)Math.Round(23 * Font.Size / 14f)), Width / 4);
                int dividerX = Width - actionWidth;
                if (e.X >= dividerX) { _typedValue = null; Value += e.Y < Height / 2 ? 1 : -1; }
                else { _typedValue = string.Empty; Invalidate(); }
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (Enabled) { _typedValue = null; Value += e.Delta > 0 ? 1 : -1; }
                base.OnMouseWheel(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Right) { _typedValue = null; Value++; e.Handled = true; }
                else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Left) { _typedValue = null; Value--; e.Handled = true; }
                else if (e.KeyCode == Keys.Home) { _typedValue = null; Value = Minimum; e.Handled = true; }
                else if (e.KeyCode == Keys.End) { _typedValue = null; Value = Maximum; e.Handled = true; }
                base.OnKeyDown(e);
            }

            protected override void OnKeyPress(KeyPressEventArgs e)
            {
                if (char.IsDigit(e.KeyChar))
                {
                    string next = (_typedValue ?? string.Empty) + e.KeyChar;
                    if (int.TryParse(next, out int parsed))
                    {
                        Value = parsed;
                        _typedValue = Value.ToString();
                    }
                    e.Handled = true;
                    Invalidate();
                }
                else if (e.KeyChar == '\b')
                {
                    string current = _typedValue ?? _value.ToString();
                    string next = current.Length > 1 ? current.Substring(0, current.Length - 1) : string.Empty;
                    _typedValue = next;
                    if (int.TryParse(next, out int remainingValue)) Value = remainingValue;
                    Invalidate();
                    e.Handled = true;
                }
                base.OnKeyPress(e);
            }

            protected override void OnLostFocus(EventArgs e)
            {
                _typedValue = null;
                Invalidate();
                base.OnLostFocus(e);
            }
        }
    }
}
