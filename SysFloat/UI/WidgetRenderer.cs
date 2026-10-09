using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using SysFloat.Configuration;
using SysFloat.Monitoring;

namespace SysFloat.UI
{
    public static class WidgetRenderer
    {
        private static readonly Color Background = Color.FromArgb(32, 38, 49);
        private static readonly Color Border = Color.FromArgb(52, 59, 73);
        private static readonly Color Text = Color.FromArgb(244, 246, 250);
        private static readonly Color Muted = Color.FromArgb(150, 159, 176);
        private static readonly Color Dim = Color.FromArgb(110, 119, 136);
        private static readonly Color Cpu = Color.FromArgb(116, 186, 255);
        private static readonly Color Ram = Color.FromArgb(194, 139, 255);
        private static readonly Color Vram = Color.FromArgb(127, 219, 163);
        private static readonly Color Alert = Color.FromArgb(255, 174, 91);
        private static readonly Font LabelFont = CreateLogicalFont(8.5f, FontStyle.Bold);
        private static readonly Font ValueFont = CreateLogicalFont(13.5f, FontStyle.Bold);
        private static readonly Font DetailFont = CreateLogicalFont(7.7f, FontStyle.Regular);
        private static readonly Font ClockFont = CreateLogicalFont(15.5f, FontStyle.Bold);
        private static readonly Font ClockSmallFont = CreateLogicalFont(7.5f, FontStyle.Regular);
        private static readonly Font TitleFont = CreateLogicalFont(9.5f, FontStyle.Bold);
        private static readonly Font ProcessFont = CreateLogicalFont(8.2f, FontStyle.Regular);
        private static readonly Font ProcessValueFont = CreateLogicalFont(8.2f, FontStyle.Bold);

        public static void DrawHorizontal(Graphics g, Rectangle bounds, MetricSnapshot snapshot, int alertThreshold = 90, bool alertsEnabled = true, int activeAlertMetrics = 7)
        {
            DrawPanel(g, bounds, 14);
            int clockWidth = 124;
            int metricsWidth = bounds.Width - clockWidth;
            int sectionWidth = metricsWidth / 3;
            string[] labels = { "CPU", "RAM", "VRAM" };
            float[] values = { snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.VramPercent };
            Color[] colors = { Cpu, Ram, Vram };
            for (int i = 0; i < 3; i++)
            {
                var area = new Rectangle(bounds.X + i * sectionWidth, bounds.Y, sectionWidth, bounds.Height);
                DrawCompactMetric(g, area, labels[i], values[i], colors[i], alertThreshold, alertsEnabled && (activeAlertMetrics & (1 << i)) != 0);
                if (i > 0) DrawDivider(g, area.X, bounds.Y + 16, bounds.Y + bounds.Height - 16);
            }
            int clockX = bounds.Right - clockWidth;
            DrawDivider(g, clockX, bounds.Y + 14, bounds.Bottom - 14);
            DrawClock(g, new Rectangle(clockX + 7, bounds.Y + 3, clockWidth - 12, bounds.Height - 6), false);
        }

        public static void DrawVertical(Graphics g, Rectangle bounds, MetricSnapshot snapshot, int alertThreshold = 90, bool alertsEnabled = true, int activeAlertMetrics = 7)
        {
            DrawPanel(g, bounds, 14);
            int clockHeight = 84;
            int metricAreaHeight = bounds.Height - clockHeight;
            int rowHeight = metricAreaHeight / 3;
            string[] labels = { "CPU", "RAM", "VRAM" };
            float[] values = { snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.VramPercent };
            Color[] colors = { Cpu, Ram, Vram };
            for (int i = 0; i < 3; i++)
            {
                var row = new Rectangle(bounds.X + 5, bounds.Y + i * rowHeight + 2, bounds.Width - 10, rowHeight - 4);
                DrawVerticalMetric(g, row, labels[i], values[i], colors[i], alertThreshold, alertsEnabled && (activeAlertMetrics & (1 << i)) != 0);
            }
            int clockY = bounds.Bottom - clockHeight;
            DrawHorizontalDivider(g, bounds.X + 14, clockY, bounds.Right - 14);
            DrawClock(g, new Rectangle(bounds.X + 7, clockY + 2, bounds.Width - 14, clockHeight - 5), true);
        }

        public static void DrawExpanded(Graphics g, Rectangle bounds, MetricSnapshot snapshot,
            float[] cpuHistory, float[] ramHistory, float[] vramHistory, bool isCloseHover,
            int alertThreshold = 90, bool alertsEnabled = true, int activeAlertMetrics = 7)
        {
            DrawPanel(g, bounds, 14);
            DrawExpandedHeader(g, bounds, isCloseHover);

            int leftWidth = (int)Math.Round(bounds.Width * 0.55f);
            int contentTop = bounds.Y + 42;
            int contentHeight = bounds.Bottom - contentTop - 8;
            int rowHeight = contentHeight / 3;
            string[] labels = { "CPU", "RAM", "VRAM" };
            float[] values = { snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.VramPercent };
            Color[] colors = { Cpu, Ram, Vram };
            float[][] histories = { cpuHistory, ramHistory, vramHistory };
            for (int i = 0; i < 3; i++)
            {
                int y = contentTop + i * rowHeight;
                DrawExpandedMetric(g, new Rectangle(bounds.X + 13, y + 1, leftWidth - 22, rowHeight - 2),
                    labels[i], values[i], colors[i], histories[i], snapshot, i, alertThreshold, alertsEnabled && (activeAlertMetrics & (1 << i)) != 0);
                if (i < 2) DrawHorizontalDivider(g, bounds.X + 13, y + rowHeight, bounds.X + leftWidth - 10);
            }

            int rightX = bounds.X + leftWidth;
            DrawDivider(g, rightX, contentTop + 4, bounds.Bottom - 12);
            DrawProcessList(g, new Rectangle(rightX + 10, contentTop + 2, bounds.Right - rightX - 20, contentHeight - 4), snapshot);
        }

        public static Rectangle GetCloseButtonRect(Rectangle bounds, WidgetLayout layout)
        {
            return layout == WidgetLayout.Expanded ? new Rectangle(bounds.Right - 32, bounds.Y + 10, 18, 18) : Rectangle.Empty;
        }

        public static Rectangle[] GetMetricHitRects(Rectangle bounds, WidgetLayout layout)
        {
            if (layout == WidgetLayout.Horizontal)
            {
                int width = (bounds.Width - 124) / 3;
                return new[] { new Rectangle(bounds.X, bounds.Y, width, bounds.Height), new Rectangle(bounds.X + width, bounds.Y, width, bounds.Height), new Rectangle(bounds.X + 2 * width, bounds.Y, bounds.Width - 124 - 2 * width, bounds.Height), new Rectangle(bounds.Right - 124, bounds.Y, 124, bounds.Height) };
            }
            if (layout == WidgetLayout.Vertical)
            {
                int height = (bounds.Height - 84) / 3;
                return new[] { new Rectangle(bounds.X, bounds.Y, bounds.Width, height), new Rectangle(bounds.X, bounds.Y + height, bounds.Width, height), new Rectangle(bounds.X, bounds.Y + 2 * height, bounds.Width, bounds.Height - 84 - 2 * height), new Rectangle(bounds.X, bounds.Bottom - 84, bounds.Width, 84) };
            }
            int leftWidth = (int)Math.Round(bounds.Width * 0.55f);
            int contentTop = bounds.Y + 42;
            int rowHeight = (bounds.Bottom - contentTop - 8) / 3;
            return new[] { new Rectangle(bounds.X, contentTop, leftWidth, rowHeight), new Rectangle(bounds.X, contentTop + rowHeight, leftWidth, rowHeight), new Rectangle(bounds.X, contentTop + 2 * rowHeight, leftWidth, bounds.Bottom - 8 - (contentTop + 2 * rowHeight)), new Rectangle(bounds.Right - 210, bounds.Y, 164, 38) };
        }

        public static void DrawLayout(Graphics g, Rectangle bounds, WidgetLayout layout, MetricSnapshot snapshot,
            float[] cpuHistory = null, float[] ramHistory = null, float[] vramHistory = null,
            bool isCloseHover = false, int alertThreshold = 90, bool alertsEnabled = true)
        {
            snapshot = snapshot ?? new MetricSnapshot();
            switch (layout)
            {
                case WidgetLayout.Vertical:
                    DrawVertical(g, bounds, snapshot, alertThreshold, alertsEnabled);
                    break;
                case WidgetLayout.Expanded:
                    DrawExpanded(g, bounds, snapshot, cpuHistory ?? CreateEmptyHistory(), ramHistory ?? CreateEmptyHistory(),
                        vramHistory ?? CreateEmptyHistory(), isCloseHover, alertThreshold, alertsEnabled);
                    break;
                default:
                    DrawHorizontal(g, bounds, snapshot, alertThreshold, alertsEnabled);
                    break;
            }
        }

        private static float[] CreateEmptyHistory()
        {
            var values = new float[60];
            for (int i = 0; i < values.Length; i++) values[i] = -1;
            return values;
        }

        private static void DrawPanel(Graphics g, Rectangle bounds, int radius)
        {
            using (var path = CreateRoundedRectPath(bounds, radius))
            using (var fill = new SolidBrush(Background))
            using (var outline = new Pen(Border, 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(outline, path);
            }
        }

        private static Font CreateLogicalFont(float pointSize, FontStyle style)
        {
            return new Font("Segoe UI", pointSize * 96f / 72f, style, GraphicsUnit.Pixel);
        }

        private static void DrawCompactMetric(Graphics g, Rectangle area, string label, float value, Color color, int threshold, bool alertsEnabled)
        {
            string valueText = HasSample(value) ? string.Format("{0:0}%", value) : "--";
            var labelSize = g.MeasureString(label, LabelFont);
            var valueSize = g.MeasureString(valueText, ValueFont);
            using (var brush = new SolidBrush(Muted)) g.DrawString(label, LabelFont, brush, area.X + (area.Width - labelSize.Width) / 2f, area.Y + 5);
            using (var brush = new SolidBrush(ValueColor(value, color, threshold, alertsEnabled))) g.DrawString(valueText, ValueFont, brush, area.X + (area.Width - valueSize.Width) / 2f, area.Y + 20);
            DrawUsageBar(g, area.X + area.Width * 0.25f, area.Bottom - 7, area.Width * 0.5f, value, color);
        }

        private static void DrawVerticalMetric(Graphics g, Rectangle area, string label, float value, Color color, int threshold, bool alertsEnabled)
        {
            string valueText = HasSample(value) ? string.Format("{0:0}%", value) : "--";
            var ls = g.MeasureString(label, LabelFont);
            var vs = g.MeasureString(valueText, ValueFont);
            float cx = area.X + area.Width / 2f;
            using (var brush = new SolidBrush(Muted)) g.DrawString(label, LabelFont, brush, cx - ls.Width / 2f, area.Y + 8);
            using (var brush = new SolidBrush(ValueColor(value, color, threshold, alertsEnabled))) g.DrawString(valueText, ValueFont, brush, cx - vs.Width / 2f, area.Y + 25);
            DrawUsageBar(g, area.X + area.Width * 0.25f, area.Bottom - 10, area.Width * 0.5f, value, color);
        }

        private static void DrawClock(Graphics g, Rectangle area, bool vertical)
        {
            DateTime utc = DateTime.UtcNow;
            DateTime local = PacificClock.GetPacificTime(utc);
            string zone = PacificClock.GetAbbreviation(utc);
            string time = local.ToString("HH:mm");
            string date = local.ToString("MM/dd");
            if (vertical)
            {
                var ts = g.MeasureString(time, ValueFont);
                using (var brush = new SolidBrush(Text)) g.DrawString(time, ValueFont, brush, area.X + (area.Width - ts.Width) / 2f, area.Y + 4);
                string label = "太平洋";
                var ls = g.MeasureString(label, ClockSmallFont);
                using (var brush = new SolidBrush(Muted)) g.DrawString(label, ClockSmallFont, brush, area.X + (area.Width - ls.Width) / 2f, area.Y + 39);
                string subtitle = zone + "  " + date;
                var ss = g.MeasureString(subtitle, ClockSmallFont);
                using (var brush = new SolidBrush(Muted)) g.DrawString(subtitle, ClockSmallFont, brush, area.X + (area.Width - ss.Width) / 2f, area.Y + 56);
            }
            else
            {
                using (var brush = new SolidBrush(Text)) g.DrawString(time, ClockFont, brush, area.X, area.Y + 1);
                string subtitle = "太平洋  " + zone + "  " + date;
                using (var brush = new SolidBrush(Muted)) g.DrawString(subtitle, ClockSmallFont, brush, area.X + 1, area.Bottom - 13);
            }
        }

        private static void DrawExpandedHeader(Graphics g, Rectangle bounds, bool closeHover)
        {
            using (var brush = new SolidBrush(Text)) g.DrawString("SysFloat", TitleFont, brush, bounds.X + 14, bounds.Y + 10);
            DateTime utc = DateTime.UtcNow;
            DateTime local = PacificClock.GetPacificTime(utc);
            string time = local.ToString("HH:mm");
            float clockX = bounds.Right - 206;
            using (var brush = new SolidBrush(Text)) g.DrawString(time, ClockFont, brush, clockX, bounds.Y + 2);
            string subtitle = "太平洋  " + PacificClock.GetAbbreviation(utc) + "  " + local.ToString("MM/dd");
            var size = g.MeasureString(subtitle, DetailFont);
            using (var brush = new SolidBrush(Muted)) g.DrawString(subtitle, DetailFont, brush, bounds.Right - 46 - size.Width, bounds.Y + 25);
            Color close = closeHover ? Color.FromArgb(255, 110, 110) : Muted;
            using (var pen = new Pen(close, 1.6f))
            {
                int x = bounds.Right - 25, y = bounds.Y + 14;
                g.DrawLine(pen, x, y, x + 8, y + 8);
                g.DrawLine(pen, x + 8, y, x, y + 8);
            }
            DrawHorizontalDivider(g, bounds.X + 12, bounds.Y + 37, bounds.Right - 12);
        }

        private static void DrawExpandedMetric(Graphics g, Rectangle area, string label, float value, Color color,
            float[] history, MetricSnapshot snapshot, int index, int threshold, bool alertsEnabled)
        {
            using (var brush = new SolidBrush(Muted)) g.DrawString(label, LabelFont, brush, area.X, area.Y + 2);
            string valueText = HasSample(value) ? string.Format("{0:0}%", value) : "N/A";
            using (var brush = new SolidBrush(ValueColor(value, color, threshold, alertsEnabled))) g.DrawString(valueText, ValueFont, brush, area.X, area.Y + 17);
            string detail = GetDetailText(snapshot, index, history);
            using (var brush = new SolidBrush(Dim)) g.DrawString(detail, DetailFont, brush, area.X + 54, area.Y + 23);
            int chartY = area.Y + 49;
            int chartHeight = Math.Max(8, area.Bottom - chartY - 5);
            DrawSparkline(g, new Rectangle(area.X, chartY, area.Width, chartHeight), history, color);
        }

        private static void DrawSparkline(Graphics g, Rectangle bounds, float[] history, Color color)
        {
            if (bounds.Width < 2 || bounds.Height < 2 || history == null || history.Length < 2) return;
            using (var pen = new Pen(Color.FromArgb(70, color), 1f)) g.DrawLine(pen, bounds.X, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
            PointF? previous = null;
            int valid = 0;
            for (int i = 0; i < history.Length; i++) if (!float.IsNaN(history[i]) && !float.IsInfinity(history[i]) && history[i] >= 0) valid++;
            if (valid == 0) return;
            float step = (float)(bounds.Width - 1) / Math.Max(1, history.Length - 1);
            using (var pen = new Pen(color, 1.5f))
            {
                for (int i = 0; i < history.Length; i++)
                {
                    float value = history[i];
                    if (float.IsNaN(value) || float.IsInfinity(value) || value < 0) { previous = null; continue; }
                    float clamped = Math.Max(0, Math.Min(100, value));
                    var point = new PointF(bounds.X + i * step, bounds.Bottom - 1 - (bounds.Height - 2) * clamped / 100f);
                    if (previous.HasValue) g.DrawLine(pen, previous.Value, point);
                    previous = point;
                }
            }
        }

        private static void DrawProcessList(Graphics g, Rectangle bounds, MetricSnapshot snapshot)
        {
            using (var brush = new SolidBrush(Muted)) g.DrawString("内存占用 Top 5", LabelFont, brush, bounds.X, bounds.Y + 4);
            int top = bounds.Y + 26;
            int rowHeight = Math.Max(24, (bounds.Height - 30) / 5);
            var processes = snapshot.TopMemoryProcesses;
            if (processes == null || processes.Count == 0)
            {
                using (var brush = new SolidBrush(Dim)) g.DrawString("等待数据…", ProcessFont, brush, bounds.X, top + 4);
                return;
            }
            int count = Math.Min(5, processes.Count);
            for (int i = 0; i < count; i++)
            {
                var proc = processes[i];
                int y = top + i * rowHeight;
                string name = string.IsNullOrWhiteSpace(proc.Name) ? "未知进程" : proc.Name;
                var value = FormatBytes(proc.MemoryBytes);
                float valueWidth = g.MeasureString(value, ProcessValueFont).Width;
                int nameWidth = Math.Max(24, bounds.Width - (int)valueWidth - 19);
                name = Ellipsize(g, name, ProcessFont, nameWidth);
                using (var brush = new SolidBrush(i == 0 ? Text : Muted)) g.DrawString(name, ProcessFont, brush, bounds.X, y + 2);
                using (var brush = new SolidBrush(i == 0 ? Vram : Muted)) g.DrawString(value, ProcessValueFont, brush, bounds.Right - valueWidth, y + 2);
                if (i < count - 1) DrawHorizontalDivider(g, bounds.X, y + rowHeight - 2, bounds.Right);
            }
        }

        private static string GetDetailText(MetricSnapshot snapshot, int index, float[] history)
        {
            if (index == 0)
            {
                float min = 100, max = 0; bool found = false;
                if (history != null) foreach (float value in history) if (!float.IsNaN(value) && !float.IsInfinity(value) && value >= 0) { min = Math.Min(min, value); max = Math.Max(max, value); found = true; }
                return found ? string.Format("区间 {0:0}–{1:0}%", min, max) : "最近区间 --";
            }
            if (index == 1) return snapshot.MemoryTotalBytes > 0 ? FormatBytes(snapshot.MemoryUsedBytes) + " / " + FormatBytes(snapshot.MemoryTotalBytes) : (!HasSample(snapshot.MemoryPercent) ? "不可用" : string.Format("已用 {0:0}%", snapshot.MemoryPercent));
            if (!snapshot.VramAvailable) return "不可用";
            return snapshot.VramTotalBytes > 0 ? FormatBytes(snapshot.VramUsedBytes) + " / " + FormatBytes(snapshot.VramTotalBytes) : (!HasSample(snapshot.VramPercent) ? "不可用" : string.Format("已用 {0:0}%", snapshot.VramPercent));
        }

        private static string Ellipsize(Graphics g, string value, Font font, int maxWidth)
        {
            if (g.MeasureString(value, font).Width <= maxWidth) return value;
            while (value.Length > 1 && g.MeasureString(value + "…", font).Width > maxWidth) value = value.Substring(0, value.Length - 1);
            return value + "…";
        }

        private static bool HasSample(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;

        private static Color ValueColor(float value, Color baseColor, int threshold, bool alertsEnabled) => alertsEnabled && HasSample(value) && value >= threshold ? Alert : baseColor;

        private static void DrawUsageBar(Graphics g, float x, float y, float width, float value, Color color)
        {
            var bounds = new RectangleF(x, y, Math.Max(12f, width), 2f);
            using (var background = new SolidBrush(Color.FromArgb(65, 75, 91))) g.FillRectangle(background, bounds);
            if (HasSample(value))
            {
                float fillWidth = bounds.Width * Math.Max(0, Math.Min(100, value)) / 100f;
                if (fillWidth > 0)
                    using (var foreground = new SolidBrush(color)) g.FillRectangle(foreground, bounds.X, bounds.Y, fillWidth, bounds.Height);
            }
        }

        private static string FormatBytes(ulong bytes)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            return gb >= 1 ? gb.ToString("0.00") + " GB" : (bytes / (1024.0 * 1024.0)).ToString("0") + " MB";
        }

        private static void DrawDivider(Graphics g, int x, int y1, int y2)
        {
            using (var pen = new Pen(Border, 1f)) g.DrawLine(pen, x, y1, x, y2);
        }

        private static void DrawHorizontalDivider(Graphics g, int x1, int y, int x2)
        {
            using (var pen = new Pen(Border, 1f)) g.DrawLine(pen, x1, y, x2, y);
        }

        public static GraphicsPath CreateRoundedRectPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            int r = Math.Max(1, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
            int diameter = r * 2;
            path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
            path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
            path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
