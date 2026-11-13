using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SysFloat.Configuration;
using SysFloat.Monitoring;
using SysFloat.UI;

internal static class UsageColorChecks
{
    private static readonly Color[] BaseColors = {
        Color.FromArgb(116, 186, 255), Color.FromArgb(194, 139, 255), Color.FromArgb(127, 219, 163) };

    internal static void Run(Action<bool, string> check, string output)
    {
        foreach (WidgetLayout layout in Enum.GetValues(typeof(WidgetLayout)))
        {
            Size size = WidgetSizeHelper.GetSize(layout, 1f);
            var bounds = new Rectangle(Point.Empty, size);
            Rectangle[] cells = WidgetRenderer.GetMetricHitRects(bounds, layout);
            using (var strip = new Bitmap(size.Width, size.Height * 4))
            using (var stripGraphics = Graphics.FromImage(strip))
            {
                float[] bands = { 35, 65, 85, 95 };
                for (int band = 0; band < bands.Length; band++)
                using (var bitmap = new Bitmap(size.Width, size.Height))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    var snapshot = new MetricSnapshot {
                        CpuPercent = bands[band], MemoryPercent = bands[band], VramPercent = bands[band], VramAvailable = true };
                    WidgetRenderer.DrawLayout(g, bounds, layout, snapshot, alertThreshold: 30, activeGpuIds: new[] { "left", "right" });
                    for (int metric = 0; metric < 3; metric++)
                    {
                        Color target = BaseColors[metric];
                        Rectangle valueBounds = ValueBounds(cells[metric], layout);
                        check(ContainsColor(bitmap, valueBounds, target), $"{layout} metric {metric} keeps identity color at {bands[band]}% with active alerts");
                        if (layout == WidgetLayout.Expanded) continue;
                        Point bar = BarPoint(cells[metric], layout);
                        check(bitmap.GetPixel(bar.X, bar.Y).ToArgb() == target.ToArgb(),
                            $"{layout} metric {metric} bar matches value color at {bands[band]}%");
                    }
                    stripGraphics.DrawImageUnscaled(bitmap, 0, band * size.Height);
                }
                strip.Save(Path.Combine(output, $"UsageFixed-{layout}.png"), ImageFormat.Png);
            }

            using (var bitmap = new Bitmap(size.Width, size.Height))
            using (var g = Graphics.FromImage(bitmap))
            {
                var snapshot = new MetricSnapshot {
                    Gpus = new[] {
                        new GpuMemoryInfo { Id = "left", Available = true, Percent = 65 },
                        new GpuMemoryInfo { Id = "right", Available = true, Percent = 95 } } };
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                WidgetRenderer.DrawLayout(g, bounds, layout, snapshot, alertsEnabled: false);
                Rectangle values = ValueBounds(cells[2], layout);
                check(ContainsColor(bitmap, values, BaseColors[2]), $"{layout} dual GPU values use fixed green at different usage levels");
                if (layout != WidgetLayout.Expanded)
                {
                    Point left = BarPoint(cells[2], layout, true);
                    int areaWidth = layout == WidgetLayout.Vertical ? cells[2].Width - 10 : cells[2].Width;
                    float barWidth = layout == WidgetLayout.Vertical ? areaWidth - 8 : areaWidth * .7f;
                    Point right = new Point(left.X + (int)((barWidth + 5) / 2), left.Y);
                    check(bitmap.GetPixel(left.X, left.Y).ToArgb() == BaseColors[2].ToArgb() &&
                        bitmap.GetPixel(right.X, right.Y).ToArgb() == BaseColors[2].ToArgb(),
                        $"{layout} dual GPU bars keep their fixed green color");
                }
                bitmap.Save(Path.Combine(output, $"UsageFixed-dual-{layout}.png"), ImageFormat.Png);
            }
        }
    }

    private static Rectangle ValueBounds(Rectangle cell, WidgetLayout layout)
    {
        int offset = layout == WidgetLayout.Vertical ? 27 : layout == WidgetLayout.Mini ? 14 : layout == WidgetLayout.Expanded ? 18 : 20;
        return new Rectangle(cell.X + 4, cell.Y + offset, cell.Width - 8, layout == WidgetLayout.Mini ? 16 : 24);
    }

    private static Point BarPoint(Rectangle cell, WidgetLayout layout, bool dual = false)
    {
        if (layout == WidgetLayout.Vertical) return new Point(cell.X + 11, cell.Bottom - 11);
        float inset = layout == WidgetLayout.Mini && !dual ? .2f : .15f;
        return new Point(cell.X + (int)Math.Ceiling(cell.Width * inset) + 2,
            cell.Bottom - (layout == WidgetLayout.Mini ? 5 : 6));
    }

    private static bool ContainsColor(Bitmap bitmap, Rectangle bounds, Color target)
    {
        for (int y = bounds.Top; y < bounds.Bottom; y++)
        for (int x = bounds.Left; x < bounds.Right; x++)
            if (bitmap.GetPixel(x, y).ToArgb() == target.ToArgb()) return true;
        return false;
    }
}
