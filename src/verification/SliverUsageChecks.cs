using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using SysFloat.Configuration;
using SysFloat.Monitoring;
using SysFloat.UI;

internal static class SliverUsageChecks
{
    private static readonly MethodInfo Draw = typeof(WidgetRenderer).GetMethod("DrawUsageSliver", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo DrawPanel = typeof(WidgetRenderer).GetMethod("DrawPanel", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly Color Blue = Color.FromArgb(116, 186, 255);
    private static readonly Color Purple = Color.FromArgb(194, 139, 255);
    private static readonly Color Green = Color.FromArgb(127, 219, 163);
    private static readonly Color Track = Color.FromArgb(32, 38, 49);

    internal static void Run(Action<bool, string> check, string output)
    {
        var snapshot = new MetricSnapshot {
            CpuPercent = 35, MemoryPercent = 65,
            Gpus = new[] {
                new GpuMemoryInfo { Id = "left", Available = true, Percent = 85 },
                new GpuMemoryInfo { Id = "right", Available = true, Percent = 95 } } };
        foreach (bool vertical in new[] { false, true })
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        foreach (int alpha in new[] { 120, 215 })
        foreach (Color backdrop in new[] { Color.Black, Color.White })
        {
            var strip = vertical ? new RectangleF(0, 0, 16, 360) : new RectangleF(0, 0, 450, 16);
            using (var bitmap = new Bitmap((int)(strip.Width * scale), (int)(strip.Height * scale)))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(backdrop);
                g.ScaleTransform(scale, scale);
                DrawPanel.Invoke(null, new object[] { g, Rectangle.Ceiling(strip), 0, 255, DockEdge.None });
                Draw.Invoke(null, new object[] { g, strip, vertical, snapshot, alpha });
                float third = (vertical ? strip.Height : strip.Width) / 3;
                float[] positions = { third * .1f, third * 1.1f, third * 2.05f, third * 2.55f };
                Color[] colors = { Blue, Purple, Green, Green };
                for (int i = 0; i < positions.Length; i++)
                    check(MatchesFill(Pixel(bitmap, vertical, positions[i], scale), colors[i], alpha),
                        $"sliver vertical={vertical} scale={scale} alpha={alpha} backdrop={backdrop.Name} region {i} keeps its fixed color");
                check(Pixel(bitmap, vertical, third * .8f, scale).ToArgb() == Track.ToArgb(),
                    "CPU unused sliver uses the panel background on both desktop colors");
                check(Pixel(bitmap, vertical, third * 1.8f, scale).ToArgb() == Track.ToArgb(),
                    "RAM unused sliver uses the same panel background");
                Color mainDivider = Pixel(bitmap, vertical, third, scale);
                Color gpuDivider = Pixel(bitmap, vertical, third * 2.5f, scale);
                check(mainDivider.R > 150 && mainDivider.G > 160 && mainDivider.B > 180,
                    "CPU/RAM boundary has a high-contrast full-thickness divider");
                check(mainDivider.R > gpuDivider.R + 30 && gpuDivider.B > 90,
                    "GPU internal divider is visibly secondary to metric boundaries");
                if (alpha == 215 && scale == 2)
                    bitmap.Save(Path.Combine(output, $"SliverFixed-{(vertical ? "vertical" : "horizontal")}.png"), ImageFormat.Png);
            }
        }
        using (var bitmap = new Bitmap(450, 16))
        using (var g = Graphics.FromImage(bitmap))
        {
            var strip = new RectangleF(0, 0, 450, 16);
            snapshot.CpuPercent = 35;
            snapshot.MemoryPercent = 35;
            snapshot.Gpus[0].Percent = 35;
            snapshot.Gpus[1].Percent = 35;
            g.Clear(Color.Black);
            DrawPanel.Invoke(null, new object[] { g, Rectangle.Ceiling(strip), 0, 255, DockEdge.None });
            Draw.Invoke(null, new object[] { g, strip, false, snapshot, 215 });
            check(MatchesFill(bitmap.GetPixel(10, 8), Blue, 215) && MatchesFill(bitmap.GetPixel(160, 8), Purple, 215),
                "CPU and RAM slivers keep their distinct fixed colors");
            check(MatchesFill(bitmap.GetPixel(310, 8), Green, 215) && MatchesFill(bitmap.GetPixel(385, 8), Green, 215),
                "dual GPU slivers use the same fixed green");
            snapshot.Gpus[1].Available = false;
            snapshot.CpuPercent = float.NaN;
            snapshot.MemoryPercent = 0;
            g.Clear(Color.Black);
            DrawPanel.Invoke(null, new object[] { g, Rectangle.Ceiling(strip), 0, 255, DockEdge.None });
            Draw.Invoke(null, new object[] { g, strip, false, snapshot, 215 });
            check(bitmap.GetPixel(10, 8).ToArgb() == Track.ToArgb() && bitmap.GetPixel(385, 8).ToArgb() == Track.ToArgb(),
                "invalid CPU and unavailable GPU show the same dark track");
            check(bitmap.GetPixel(160, 8).ToArgb() == Track.ToArgb(), "zero usage shows a dark track");
            snapshot.Gpus = Array.Empty<GpuMemoryInfo>();
            snapshot.VramAvailable = true;
            snapshot.VramPercent = 95;
            g.Clear(Color.Black);
            DrawPanel.Invoke(null, new object[] { g, Rectangle.Ceiling(strip), 0, 255, DockEdge.None });
            Draw.Invoke(null, new object[] { g, strip, false, snapshot, 215 });
            check(MatchesFill(bitmap.GetPixel(310, 8), Green, 215) && MatchesFill(bitmap.GetPixel(400, 8), Green, 215),
                "legacy single GPU keeps green at high usage across the full final third");
        }
    }

    private static Color Pixel(Bitmap bitmap, bool vertical, float along, float scale) => vertical
        ? bitmap.GetPixel((int)(8 * scale), (int)(along * scale))
        : bitmap.GetPixel((int)(along * scale), (int)(8 * scale));

    private static bool MatchesFill(Color actual, Color expected, int alpha) =>
        Math.Abs(actual.R - (expected.R * alpha + Track.R * (255 - alpha)) / 255f) <= 1 &&
        Math.Abs(actual.G - (expected.G * alpha + Track.G * (255 - alpha)) / 255f) <= 1 &&
        Math.Abs(actual.B - (expected.B * alpha + Track.B * (255 - alpha)) / 255f) <= 1;
}
