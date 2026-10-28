using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using SysFloat.Configuration;
using SysFloat.Monitoring;

namespace SysFloat.UI
{
    public static class WidgetRenderer
    {
        /// <summary>当前时钟显示时区；由 WidgetForm 绘制前按设置刷新，默认太平洋。</summary>
        public static ClockTimeZone ClockZone { get; set; } = ClockTimeZone.Pacific;

        private static readonly Color Background = Color.FromArgb(32, 38, 49);
        private static readonly Color Border = Color.FromArgb(52, 59, 73);
        private static readonly Color Text = Color.FromArgb(244, 246, 250);
        private static readonly Color Muted = Color.FromArgb(150, 159, 176);
        private static readonly Color Dim = Color.FromArgb(110, 119, 136);
        private static readonly Color LayeredMuted = Color.FromArgb(229, 233, 240);
        private static readonly Color LayeredDim = Color.FromArgb(211, 219, 231);
        private static readonly Color Cpu = Color.FromArgb(116, 186, 255);
        private static readonly Color Ram = Color.FromArgb(194, 139, 255);
        private static readonly Color Vram = Color.FromArgb(127, 219, 163);
        private static readonly Color Alert = Color.FromArgb(255, 174, 91);
        private static readonly Font LabelFont = CreateLogicalFont(8.5f, FontStyle.Bold);
        private static readonly Font ValueFont = CreateLogicalFont(13.5f, FontStyle.Bold);
        private static readonly Font DetailFont = CreateLogicalFont(8.5f, FontStyle.Regular);
        private static readonly Font ClockFont = CreateLogicalFont(15.5f, FontStyle.Bold);
        private static readonly Font ClockSmallFont = CreateLogicalFont(8.5f, FontStyle.Regular);
        private static readonly Font TitleFont = CreateLogicalFont(9.5f, FontStyle.Bold);
        private static readonly Font ProcessFont = CreateLogicalFont(8.2f, FontStyle.Regular);
        private static readonly Font ProcessValueFont = CreateLogicalFont(8.2f, FontStyle.Bold);
        private static readonly Font MiniLabelFont = CreateLogicalFont(7.5f, FontStyle.Bold);
        private static readonly Font MiniValueFont = CreateLogicalFont(10.5f, FontStyle.Bold);
        private static readonly Font SmallDualValueFont = CreateLogicalFont(8.5f, FontStyle.Bold);
        private static readonly Font TinyDualValueFont = CreateLogicalFont(7f, FontStyle.Bold);
        private static readonly Font[] GpuValueFonts = { ValueFont, MiniValueFont, SmallDualValueFont, TinyDualValueFont };

        public static void DrawHorizontal(Graphics g, Rectangle bounds, MetricSnapshot snapshot, int alertThreshold = 90, bool alertsEnabled = true, int activeAlertMetrics = 7, int backgroundAlpha = 255, IReadOnlyCollection<string> activeGpuIds = null)
        {
            DrawPanel(g, bounds, 14, backgroundAlpha);
            Color muted = GetMutedColor(backgroundAlpha);
            int clockWidth = 124;
            int metricsWidth = bounds.Width - clockWidth;
            int sectionWidth = metricsWidth / 3;
            string[] labels = { "CPU", "RAM", "VRAM" };
            float[] values = { snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.VramPercent };
            Color[] colors = { Cpu, Ram, Vram };
            for (int i = 0; i < 3; i++)
            {
                var area = new Rectangle(bounds.X + i * sectionWidth, bounds.Y, sectionWidth, bounds.Height);
                if (i == 2 && GpuDisplay.IsMultiple(snapshot))
                    DrawMultiGpuMetric(g, area, snapshot, false, false, alertThreshold, alertsEnabled, activeGpuIds, muted);
                else DrawCompactMetric(g, area, labels[i], values[i], colors[i], alertThreshold, alertsEnabled && (activeAlertMetrics & (1 << i)) != 0, muted);
                if (i > 0) DrawDivider(g, area.X, bounds.Y + 16, bounds.Y + bounds.Height - 16);
            }
            int clockX = bounds.Right - clockWidth;
            DrawDivider(g, clockX, bounds.Y + 14, bounds.Bottom - 14);
            DrawClock(g, new Rectangle(clockX + 7, bounds.Y + 3, clockWidth - 12, bounds.Height - 6), false, muted);
        }

        public static void DrawVertical(Graphics g, Rectangle bounds, MetricSnapshot snapshot, int alertThreshold = 90, bool alertsEnabled = true, int activeAlertMetrics = 7, int backgroundAlpha = 255, IReadOnlyCollection<string> activeGpuIds = null)
        {
            DrawPanel(g, bounds, 14, backgroundAlpha);
            Color muted = GetMutedColor(backgroundAlpha);
            int clockHeight = 84;
            int metricAreaHeight = bounds.Height - clockHeight;
            int rowHeight = metricAreaHeight / 3;
            string[] labels = { "CPU", "RAM", "VRAM" };
            float[] values = { snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.VramPercent };
            Color[] colors = { Cpu, Ram, Vram };
            for (int i = 0; i < 3; i++)
            {
                var row = new Rectangle(bounds.X + 5, bounds.Y + i * rowHeight + 2, bounds.Width - 10, rowHeight - 4);
                if (i == 2 && GpuDisplay.IsMultiple(snapshot))
                    DrawMultiGpuMetric(g, row, snapshot, true, false, alertThreshold, alertsEnabled, activeGpuIds, muted);
                else DrawVerticalMetric(g, row, labels[i], values[i], colors[i], alertThreshold, alertsEnabled && (activeAlertMetrics & (1 << i)) != 0, muted);
            }
            int clockY = bounds.Bottom - clockHeight;
            DrawHorizontalDivider(g, bounds.X + 14, clockY, bounds.Right - 14);
            DrawClock(g, new Rectangle(bounds.X + 7, clockY + 2, bounds.Width - 14, clockHeight - 5), true, muted);
        }

        public static void DrawExpanded(Graphics g, Rectangle bounds, MetricSnapshot snapshot,
            float[] cpuHistory, float[] ramHistory, float[] vramHistory, bool isCloseHover,
            int alertThreshold = 90, bool alertsEnabled = true, int activeAlertMetrics = 7,
            ProcessMetric selectedMetric = ProcessMetric.Memory, int backgroundAlpha = 255, IReadOnlyCollection<string> activeGpuIds = null)
        {
            if (!Enum.IsDefined(typeof(ProcessMetric), selectedMetric)) selectedMetric = ProcessMetric.Memory;
            DrawPanel(g, bounds, 14, backgroundAlpha);
            Color muted = GetMutedColor(backgroundAlpha);
            Color dim = GetDimColor(backgroundAlpha);
            DrawExpandedHeader(g, bounds, isCloseHover, muted);

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
                var rowBounds = new Rectangle(bounds.X + 7, y + 1, leftWidth - 18, rowHeight - 2);
                if (i == (int)selectedMetric) DrawSelectedMetric(g, rowBounds, colors[i]);
                DrawExpandedMetric(g, new Rectangle(bounds.X + 13, y + 1, leftWidth - 22, rowHeight - 2),
                    labels[i], values[i], colors[i], histories[i], snapshot, i, alertThreshold, alertsEnabled && (activeAlertMetrics & (1 << i)) != 0, muted, dim, activeGpuIds);
                if (i < 2) DrawHorizontalDivider(g, bounds.X + 13, y + rowHeight, bounds.X + leftWidth - 10);
            }

            int rightX = bounds.X + leftWidth;
            DrawDivider(g, rightX, contentTop + 4, bounds.Bottom - 12);
            int activeY = contentTop + ((int)selectedMetric * rowHeight) + rowHeight / 2;
            DrawProcessList(g, new Rectangle(rightX + 11, contentTop + 2, bounds.Right - rightX - 20, contentHeight - 4), snapshot, selectedMetric, muted, dim);
            DrawSelectionBridge(g, rightX, activeY, MetricColor(selectedMetric));
        }

        public static void DrawMini(Graphics g, Rectangle bounds, MetricSnapshot snapshot, int alertThreshold = 90, bool alertsEnabled = true, int activeAlertMetrics = 7, int backgroundAlpha = 255, IReadOnlyCollection<string> activeGpuIds = null)
        {
            DrawPanel(g, bounds, 12, backgroundAlpha);
            Color muted = GetMutedColor(backgroundAlpha);
            int sectionWidth = bounds.Width / 3;
            string[] labels = { "CPU", "RAM", "VRAM" };
            float[] values = { snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.VramPercent };
            Color[] colors = { Cpu, Ram, Vram };
            for (int i = 0; i < 3; i++)
            {
                int cellX = bounds.X + i * sectionWidth;
                int cellWidth = i == 2 ? bounds.Right - cellX : sectionWidth;
                var area = new Rectangle(cellX, bounds.Y, cellWidth, bounds.Height);
                if (i == 2 && GpuDisplay.IsMultiple(snapshot))
                {
                    DrawMultiGpuMetric(g, area, snapshot, false, true, alertThreshold, alertsEnabled, activeGpuIds, muted);
                    DrawDivider(g, area.X, bounds.Y + 8, bounds.Bottom - 8);
                    continue;
                }
                var labelSize = g.MeasureString(labels[i], MiniLabelFont);
                using (var brush = new SolidBrush(muted))
                    g.DrawString(labels[i], MiniLabelFont, brush, area.X + (area.Width - labelSize.Width) / 2f, area.Y + 3);
                string valueText = HasSample(values[i]) ? string.Format("{0:0}%", values[i]) : "--";
                var valueSize = g.MeasureString(valueText, MiniValueFont);
                using (var brush = new SolidBrush(ValueColor(values[i], colors[i], alertThreshold, alertsEnabled && (activeAlertMetrics & (1 << i)) != 0)))
                    g.DrawString(valueText, MiniValueFont, brush, area.X + (area.Width - valueSize.Width) / 2f, area.Y + 14);
                DrawUsageBar(g, area.X + area.Width * 0.2f, area.Bottom - 6, area.Width * 0.6f, values[i], colors[i]);
                if (i > 0) DrawDivider(g, area.X, bounds.Y + 8, bounds.Bottom - 8);
            }
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
            if (layout == WidgetLayout.Mini)
            {
                int width = bounds.Width / 3;
                return new[] { new Rectangle(bounds.X, bounds.Y, width, bounds.Height), new Rectangle(bounds.X + width, bounds.Y, width, bounds.Height), new Rectangle(bounds.X + 2 * width, bounds.Y, bounds.Width - 2 * width, bounds.Height) };
            }
            int leftWidth = (int)Math.Round(bounds.Width * 0.55f);
            int contentTop = bounds.Y + 42;
            int rowHeight = (bounds.Bottom - contentTop - 8) / 3;
            return new[] { new Rectangle(bounds.X, contentTop, leftWidth, rowHeight), new Rectangle(bounds.X, contentTop + rowHeight, leftWidth, rowHeight), new Rectangle(bounds.X, contentTop + 2 * rowHeight, leftWidth, bounds.Bottom - 8 - (contentTop + 2 * rowHeight)), new Rectangle(bounds.Right - 224, bounds.Y + 3, 178, 34) };
        }

        public static void DrawLayout(Graphics g, Rectangle bounds, WidgetLayout layout, MetricSnapshot snapshot,
            float[] cpuHistory = null, float[] ramHistory = null, float[] vramHistory = null,
            bool isCloseHover = false, int alertThreshold = 90, bool alertsEnabled = true,
            ProcessMetric selectedMetric = ProcessMetric.Memory, int backgroundAlpha = 255, IReadOnlyCollection<string> activeGpuIds = null)
        {
            snapshot = snapshot ?? new MetricSnapshot();
            switch (layout)
            {
                case WidgetLayout.Vertical:
                    DrawVertical(g, bounds, snapshot, alertThreshold, alertsEnabled, 7, backgroundAlpha, activeGpuIds);
                    break;
                case WidgetLayout.Expanded:
                    DrawExpanded(g, bounds, snapshot, cpuHistory ?? CreateEmptyHistory(), ramHistory ?? CreateEmptyHistory(),
                        vramHistory ?? CreateEmptyHistory(), isCloseHover, alertThreshold, alertsEnabled, 7, selectedMetric, backgroundAlpha, activeGpuIds);
                    break;
                case WidgetLayout.Mini:
                    DrawMini(g, bounds, snapshot, alertThreshold, alertsEnabled, 7, backgroundAlpha, activeGpuIds);
                    break;
                default:
                    DrawHorizontal(g, bounds, snapshot, alertThreshold, alertsEnabled, 7, backgroundAlpha, activeGpuIds);
                    break;
            }
        }

        private static float[] CreateEmptyHistory()
        {
            var values = new float[60];
            for (int i = 0; i < values.Length; i++) values[i] = -1;
            return values;
        }

        private static void DrawPanel(Graphics g, Rectangle bounds, int radius, int backgroundAlpha = 255)
        {
            backgroundAlpha = Math.Max(0, Math.Min(255, backgroundAlpha));
            using (var path = CreateRoundedRectPath(bounds, radius))
            {
                if (backgroundAlpha == 255)
                {
                    using (var fill = new SolidBrush(Background))
                    using (var outline = new Pen(Border, 1f))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(outline, path);
                    }
                    return;
                }

                var top = Color.FromArgb(backgroundAlpha, 27, 39, 59);
                var bottom = Color.FromArgb(backgroundAlpha, 16, 23, 36);
                using (var fill = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical))
                using (var outline = new Pen(Color.FromArgb(150, 82, 94, 116), 1f))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(outline, path);
                }
            }
        }

        private static Color GetMutedColor(int backgroundAlpha) => backgroundAlpha < 255 ? LayeredMuted : Muted;

        private static Color GetDimColor(int backgroundAlpha) => backgroundAlpha < 255 ? LayeredDim : Dim;

        private static Font CreateLogicalFont(float pointSize, FontStyle style)
        {
            return new Font("Segoe UI", pointSize * 96f / 72f, style, GraphicsUnit.Pixel);
        }

        private static bool IsGpuAlerted(GpuMemoryInfo gpu, IReadOnlyCollection<string> activeGpuIds)
        {
            if (activeGpuIds != null)
                foreach (string id in activeGpuIds)
                    if (string.Equals(id, gpu.Id, StringComparison.Ordinal)) return true;
            return false;
        }

        private static Color GpuColor(GpuMemoryInfo gpu, int threshold, bool alertsEnabled, IReadOnlyCollection<string> activeGpuIds) =>
            ValueColor(gpu.Available ? gpu.Percent : -1, Vram, threshold, alertsEnabled && IsGpuAlerted(gpu, activeGpuIds));

        private static Font SelectGpuValueFont(Graphics g, MetricSnapshot snapshot, Font maximum, float available, out float width)
        {
            using (var format = StringFormat.GenericTypographic)
            {
                Font selected = TinyDualValueFont;
                width = 0;
                foreach (Font font in GpuValueFonts)
                {
                    if (font.Size > maximum.Size) continue;
                    selected = font;
                    width = 0;
                    for (int i = 0; i < snapshot.Gpus.Count; i++)
                    {
                        if (i > 0) width += g.MeasureString(" | ", font, PointF.Empty, format).Width;
                        width += g.MeasureString(GpuDisplay.PercentText(snapshot.Gpus[i]), font, PointF.Empty, format).Width;
                    }
                    if (width <= available) break;
                }
                return selected;
            }
        }

        internal static float MeasureGpuValues(Graphics g, MetricSnapshot snapshot, float availableWidth, bool mini)
        {
            SelectGpuValueFont(g, snapshot, mini ? MiniValueFont : ValueFont, availableWidth, out float width);
            return width;
        }

        private static void DrawGpuValues(Graphics g, Rectangle area, MetricSnapshot snapshot, Font maximum,
            int threshold, bool alertsEnabled, IReadOnlyCollection<string> activeGpuIds, Color muted, bool centered)
        {
            Font font = SelectGpuValueFont(g, snapshot, maximum, area.Width - 8, out float width);
            float x = centered ? area.X + (area.Width - width) / 2f : area.X;
            using (var format = StringFormat.GenericTypographic)
            {
                for (int i = 0; i < snapshot.Gpus.Count; i++)
                {
                    if (i > 0)
                    {
                        using (var brush = new SolidBrush(muted)) g.DrawString(" | ", font, brush, x, area.Y, format);
                        x += g.MeasureString(" | ", font, PointF.Empty, format).Width;
                    }
                    GpuMemoryInfo gpu = snapshot.Gpus[i];
                    string text = GpuDisplay.PercentText(gpu);
                    using (var brush = new SolidBrush(GpuColor(gpu, threshold, alertsEnabled, activeGpuIds)))
                        g.DrawString(text, font, brush, x, area.Y, format);
                    x += g.MeasureString(text, font, PointF.Empty, format).Width;
                }
            }
        }

        private static void DrawMultiGpuMetric(Graphics g, Rectangle area, MetricSnapshot snapshot, bool vertical,
            bool mini, int threshold, bool alertsEnabled, IReadOnlyCollection<string> activeGpuIds, Color muted)
        {
            Font labelFont = mini ? MiniLabelFont : LabelFont;
            var labelSize = g.MeasureString("VRAM", labelFont);
            using (var brush = new SolidBrush(muted)) g.DrawString("VRAM", labelFont, brush,
                area.X + (area.Width - labelSize.Width) / 2f, area.Y + (vertical ? 8 : mini ? 3 : 5));
            DrawGpuValues(g, new Rectangle(area.X, area.Y + (vertical ? 25 : mini ? 14 : 20), area.Width, 25),
                snapshot, mini ? MiniValueFont : ValueFont, threshold, alertsEnabled, activeGpuIds, muted, true);
            float barX = vertical ? area.X + 4 : area.X + area.Width * .15f;
            float barWidth = vertical ? area.Width - 8 : area.Width * .7f;
            float segment = (barWidth - (snapshot.Gpus.Count - 1) * 5) / snapshot.Gpus.Count;
            for (int i = 0; i < snapshot.Gpus.Count; i++)
            {
                var gpu = snapshot.Gpus[i];
                DrawUsageBar(g, barX + i * (segment + 5), area.Bottom - (vertical ? 10 : mini ? 6 : 7), segment,
                    gpu.Available ? gpu.Percent : -1, GpuColor(gpu, threshold, alertsEnabled, activeGpuIds));
            }
        }

        private static void DrawCompactMetric(Graphics g, Rectangle area, string label, float value, Color color, int threshold, bool alertsEnabled, Color muted)
        {
            string valueText = HasSample(value) ? string.Format("{0:0}%", value) : "--";
            var labelSize = g.MeasureString(label, LabelFont);
            var valueSize = g.MeasureString(valueText, ValueFont);
            using (var brush = new SolidBrush(muted)) g.DrawString(label, LabelFont, brush, area.X + (area.Width - labelSize.Width) / 2f, area.Y + 5);
            using (var brush = new SolidBrush(ValueColor(value, color, threshold, alertsEnabled))) g.DrawString(valueText, ValueFont, brush, area.X + (area.Width - valueSize.Width) / 2f, area.Y + 20);
            DrawUsageBar(g, area.X + area.Width * 0.15f, area.Bottom - 7, area.Width * 0.7f, value, color);
        }

        private static void DrawVerticalMetric(Graphics g, Rectangle area, string label, float value, Color color, int threshold, bool alertsEnabled, Color muted)
        {
            string valueText = HasSample(value) ? string.Format("{0:0}%", value) : "--";
            var ls = g.MeasureString(label, LabelFont);
            var vs = g.MeasureString(valueText, ValueFont);
            float cx = area.X + area.Width / 2f;
            using (var brush = new SolidBrush(muted)) g.DrawString(label, LabelFont, brush, cx - ls.Width / 2f, area.Y + 8);
            using (var brush = new SolidBrush(ValueColor(value, color, threshold, alertsEnabled))) g.DrawString(valueText, ValueFont, brush, cx - vs.Width / 2f, area.Y + 25);
            DrawUsageBar(g, area.X + 4, area.Bottom - 10, area.Width - 8, value, color);
        }

        private static void DrawClock(Graphics g, Rectangle area, bool vertical, Color muted)
        {
            DateTime utc = DateTime.UtcNow;
            var zone = ClockZone;
            string time = ClockDisplay.FormatTime(utc, zone);
            if (vertical)
            {
                var ts = g.MeasureString(time, ValueFont);
                using (var brush = new SolidBrush(Text)) g.DrawString(time, ValueFont, brush, area.X + (area.Width - ts.Width) / 2f, area.Y + 6);
                // 时区与日期合并为一行；竖版 92px 窄面板放不下完整副标题时退化为紧凑版。
                string subtitle = ClockDisplay.GetSubtitleFull(utc, zone);
                var ss = g.MeasureString(subtitle, ClockSmallFont);
                if (ss.Width > area.Width)
                {
                    subtitle = ClockDisplay.GetSubtitleCompact(utc, zone);
                    ss = g.MeasureString(subtitle, ClockSmallFont);
                }
                using (var brush = new SolidBrush(muted)) g.DrawString(subtitle, ClockSmallFont, brush, area.X + (area.Width - ss.Width) / 2f, area.Y + 54);
            }
            else
            {
                using (var brush = new SolidBrush(Text)) g.DrawString(time, ClockFont, brush, area.X, area.Y + 1);
                string subtitle = ClockDisplay.GetSubtitleWide(utc, zone);
                using (var brush = new SolidBrush(muted)) g.DrawString(subtitle, ClockSmallFont, brush, area.X + 1, area.Bottom - 13);
            }
        }

        private static void DrawExpandedHeader(Graphics g, Rectangle bounds, bool closeHover, Color muted)
        {
            using (var brush = new SolidBrush(Text)) g.DrawString("SysFloat", TitleFont, brush, bounds.X + 14, bounds.Y + 10);
            DateTime utc = DateTime.UtcNow;
            DateTime local = ClockDisplay.GetTime(utc, ClockZone);
            string location = ClockDisplay.GetHeaderLabel(utc, ClockZone);
            string time = local.ToString("HH:mm");
            string date = local.ToString("MM/dd");
            float locationWidth = g.MeasureString(location, DetailFont).Width;
            float timeWidth = g.MeasureString(time, ClockFont).Width;
            float dateWidth = g.MeasureString(date, DetailFont).Width;
            float gap = 8f;
            float totalWidth = locationWidth + gap + timeWidth + gap + dateWidth;
            float startX = bounds.Right - 46 - totalWidth;
            float baseline = bounds.Y + 30;
            using (var brush = new SolidBrush(muted)) g.DrawString(location, DetailFont, brush, startX, baseline - DetailFont.Height);
            using (var brush = new SolidBrush(Text)) g.DrawString(time, ClockFont, brush, startX + locationWidth + gap, baseline - ClockFont.Height);
            using (var brush = new SolidBrush(muted)) g.DrawString(date, DetailFont, brush, startX + locationWidth + gap + timeWidth + gap, baseline - DetailFont.Height);
            Color close = closeHover ? Color.FromArgb(255, 110, 110) : muted;
            using (var pen = new Pen(close, 1.6f))
            {
                int x = bounds.Right - 25, y = bounds.Y + 14;
                g.DrawLine(pen, x, y, x + 8, y + 8);
                g.DrawLine(pen, x + 8, y, x, y + 8);
            }
            DrawHorizontalDivider(g, bounds.X + 12, bounds.Y + 37, bounds.Right - 12);
        }

        private static void DrawExpandedMetric(Graphics g, Rectangle area, string label, float value, Color color,
            float[] history, MetricSnapshot snapshot, int index, int threshold, bool alertsEnabled, Color muted, Color dim, IReadOnlyCollection<string> activeGpuIds = null)
        {
            using (var brush = new SolidBrush(muted)) g.DrawString(label, LabelFont, brush, area.X, area.Y + 2);
            if (index == 2 && GpuDisplay.IsMultiple(snapshot))
            {
                DrawGpuValues(g, new Rectangle(area.X, area.Y + 17, area.Width, 24), snapshot, ValueFont,
                    threshold, alertsEnabled, activeGpuIds, muted, false);
                string caption = Ellipsize(g, UIStrings.Get("detail.gpuHistory"), DetailFont, area.Width);
                using (var brush = new SolidBrush(dim)) g.DrawString(caption, DetailFont, brush, area.X, area.Y + 41);
                DrawSparkline(g, new Rectangle(area.X, area.Y + 58, area.Width, Math.Max(8, area.Height - 63)), history, color);
                return;
            }
            string valueText = HasSample(value) ? string.Format("{0:0}%", value) : "N/A";
            using (var brush = new SolidBrush(ValueColor(value, color, threshold, alertsEnabled))) g.DrawString(valueText, ValueFont, brush, area.X, area.Y + 17);
            string detail = GetDetailText(snapshot, index, history);
            using (var brush = new SolidBrush(dim)) g.DrawString(detail, DetailFont, brush, area.X + 54, area.Y + 23);
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

        private static void DrawSelectedMetric(Graphics g, Rectangle bounds, Color color)
        {
            using (var path = CreateRoundedRectPath(bounds, 8))
            using (var fill = new SolidBrush(Color.FromArgb(30, color)))
            using (var edge = new Pen(Color.FromArgb(70, color), 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
            using (var brush = new SolidBrush(color)) g.FillRectangle(brush, bounds.X, bounds.Y + 8, 2, Math.Max(8, bounds.Height - 16));
        }

        private static void DrawSelectionBridge(Graphics g, int seamX, int centerY, Color color)
        {
            using (var pen = new Pen(Color.FromArgb(170, color), 2f)) g.DrawLine(pen, seamX - 8, centerY, seamX + 14, centerY);
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, seamX - 2, centerY - 2, 4, 4);
        }

        private static Color MetricColor(ProcessMetric metric)
        {
            switch (metric)
            {
                case ProcessMetric.Cpu: return Cpu;
                case ProcessMetric.Vram: return Vram;
                default: return Ram;
            }
        }

        private static void DrawProcessList(Graphics g, Rectangle bounds, MetricSnapshot snapshot, ProcessMetric selectedMetric, Color muted, Color dim)
        {
            Color accent = MetricColor(selectedMetric);
            using (var path = CreateRoundedRectPath(bounds, 10))
            using (var fill = new SolidBrush(Color.FromArgb(20, accent)))
            using (var border = new Pen(Color.FromArgb(72, accent), 1f))
            {
                g.FillPath(fill, path);
                g.DrawPath(border, path);
            }

            string title;
            string subtitle;
            List<ProcessInfo> processes;
            string emptyMessage;
            if (selectedMetric == ProcessMetric.Cpu)
            {
                title = UIStrings.Get("rank.cpuTitle");
                subtitle = UIStrings.Get("rank.cpuSubtitle");
                processes = snapshot.TopCpuProcesses;
                emptyMessage = UIStrings.Get("rank.cpuEmpty");
            }
            else if (selectedMetric == ProcessMetric.Vram)
            {
                title = UIStrings.Get("rank.vramTitle");
                subtitle = UIStrings.Get(GpuDisplay.IsMultiple(snapshot) ? "rank.allGpus" : "rank.vramSubtitle");
                processes = snapshot.TopVramProcesses;
                emptyMessage = UIStrings.Get("rank.vramEmpty");
            }
            else
            {
                title = UIStrings.Get("rank.memoryTitle");
                subtitle = UIStrings.Get("rank.memorySubtitle");
                processes = snapshot.TopMemoryProcesses;
                emptyMessage = UIStrings.Get("rank.memoryEmpty");
            }

            int topCount = Math.Min(5, processes == null ? 0 : processes.Count);
            if (topCount > 0) title = title.Replace("Top 5", "Top " + topCount);
            using (var brush = new SolidBrush(accent)) g.DrawString(title, LabelFont, brush, bounds.X + 10, bounds.Y + 7);
            using (var brush = new SolidBrush(muted)) g.DrawString(subtitle, DetailFont, brush, bounds.X + 10, bounds.Y + 23);
            using (var pen = new Pen(Color.FromArgb(90, 150, 159, 176), 1f)) g.DrawLine(pen, bounds.X + 10, bounds.Y + 39, bounds.Right - 10, bounds.Y + 39);

            string unavailableMessage = null;
            if (snapshot.RankingMetric != selectedMetric)
                unavailableMessage = UIStrings.Get("rank.switching");
            else if (selectedMetric == ProcessMetric.Cpu && !snapshot.CpuProcessesAvailable)
                unavailableMessage = UIStrings.Get("rank.samplingCpu");
            else if (selectedMetric == ProcessMetric.Vram && !snapshot.VramProcessesAvailable)
            {
                string status = snapshot.VramProcessesStatus;
                unavailableMessage = string.IsNullOrWhiteSpace(status) || status == "等待显存数据"
                    ? UIStrings.Get("rank.vramUnsupported") : status;
            }
            else if (selectedMetric == ProcessMetric.Memory && !snapshot.MemoryProcessesAvailable)
                unavailableMessage = string.IsNullOrWhiteSpace(snapshot.MemoryProcessesStatus)
                    ? UIStrings.Get("rank.memoryUnsupported") : snapshot.MemoryProcessesStatus;

            int listTop = bounds.Y + 46;
            if (unavailableMessage != null)
            {
                string visibleMessage = Ellipsize(g, unavailableMessage, ProcessFont, bounds.Width - 24);
                using (var brush = new SolidBrush(dim)) g.DrawString(visibleMessage, ProcessFont, brush, bounds.X + 10, listTop + 8);
                return;
            }
            if (processes == null || processes.Count == 0)
            {
                using (var brush = new SolidBrush(dim)) g.DrawString(emptyMessage, ProcessFont, brush, bounds.X + 10, listTop + 8);
                return;
            }

            int count = Math.Min(5, processes.Count);
            // 行高按实际行数分配，进程不足 5 个时列表撑满卡片，不再留大片空白。
            int rowHeight = Math.Max(30, Math.Min(56, (bounds.Bottom - listTop - 5) / Math.Max(1, count)));
            for (int i = 0; i < count; i++)
            {
                ProcessInfo process = processes[i];
                int y = listTop + i * rowHeight;
                string name = string.IsNullOrWhiteSpace(process.Name) ? UIStrings.Get("rank.unknownProcess") : process.Name;
                string countSuffix = process.ProcessCount > 1 ? " (" + process.ProcessCount + ")" : "";
                string metricText;
                if (selectedMetric == ProcessMetric.Cpu)
                    metricText = HasSample(process.CpuPercent) ? process.CpuPercent.ToString("0.0") + "%" : "--";
                else if (selectedMetric == ProcessMetric.Vram)
                    metricText = FormatBytes(process.VramBytes);
                else
                    metricText = (process.MemoryIsPartial ? "≥ " : "") + FormatBytes(process.MemoryBytes);

                float valueWidth = g.MeasureString(metricText, ProcessValueFont).Width;
                int nameWidth = Math.Max(20, bounds.Width - (int)valueWidth - 32);
                int suffixWidth = (int)Math.Ceiling(g.MeasureString(countSuffix, ProcessFont).Width);
                name = Ellipsize(g, name, ProcessFont, Math.Max(10, nameWidth - suffixWidth)) + countSuffix;
                using (var brush = new SolidBrush(i == 0 ? Text : muted))
                    g.DrawString(name, ProcessFont, brush, bounds.X + 10, y + 5);
                using (var brush = new SolidBrush(i == 0 ? accent : muted))
                    g.DrawString(metricText, ProcessValueFont, brush, bounds.Right - 10 - valueWidth, y + 5);
                if (i < count - 1)
                {
                    using (var pen = new Pen(Color.FromArgb(45, accent), 1f))
                        g.DrawLine(pen, bounds.X + 10, y + rowHeight - 1, bounds.Right - 10, y + rowHeight - 1);
                }
            }
        }

        private static string GetDetailText(MetricSnapshot snapshot, int index, float[] history)
        {
            if (index == 0)
            {
                float min = 100, max = 0; bool found = false;
                if (history != null) foreach (float value in history) if (!float.IsNaN(value) && !float.IsInfinity(value) && value >= 0) { min = Math.Min(min, value); max = Math.Max(max, value); found = true; }
                return found ? UIStrings.Format("detail.range", min, max) : UIStrings.Get("detail.noHistory");
            }
            if (index == 1) return snapshot.MemoryTotalBytes > 0 ? FormatBytes(snapshot.MemoryUsedBytes) + " / " + FormatBytes(snapshot.MemoryTotalBytes) : (!HasSample(snapshot.MemoryPercent) ? UIStrings.Get("detail.unavailable") : UIStrings.Format("detail.usedPercent", snapshot.MemoryPercent));
            if (!snapshot.VramAvailable) return UIStrings.Get("detail.unavailable");
            return snapshot.VramTotalBytes > 0 ? FormatBytes(snapshot.VramUsedBytes) + " / " + FormatBytes(snapshot.VramTotalBytes) : (!HasSample(snapshot.VramPercent) ? UIStrings.Get("detail.unavailable") : UIStrings.Format("detail.usedPercent", snapshot.VramPercent));
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
            var bounds = new RectangleF(x, y, Math.Max(12f, width), 3f);
            // 轨道用半透明叠印而非固定灰：明暗背景与不同透明档下都能与面板拉开层次。
            using (var background = new SolidBrush(Color.FromArgb(60, 0, 0, 0))) g.FillRectangle(background, bounds);
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
            using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawLine(pen, x, y1, x, y2);
        }

        private static void DrawHorizontalDivider(Graphics g, int x1, int y, int x2)
        {
            using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawLine(pen, x1, y, x2, y);
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
