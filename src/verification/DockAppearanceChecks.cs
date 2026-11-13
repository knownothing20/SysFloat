using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using SysFloat.Configuration;
using SysFloat.Monitoring;
using SysFloat.UI;

internal static class DockAppearanceChecks
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> check, string output)
    {
        foreach (WidgetLayout layout in Enum.GetValues(typeof(WidgetLayout)))
        foreach (DockEdge edge in Enum.GetValues(typeof(DockEdge)))
        foreach (float scale in new[] { 1f, 1.5f, 2f })
        {
            Size logical = WidgetSizeHelper.GetSize(layout, 1f);
            bool[] square = {
                edge == DockEdge.Top || edge == DockEdge.Left,
                edge == DockEdge.Top || edge == DockEdge.Right,
                edge == DockEdge.Bottom || edge == DockEdge.Right,
                edge == DockEdge.Bottom || edge == DockEdge.Left };
            Point[] corners = {
                new Point(1, 1), new Point(logical.Width - 2, 1),
                new Point(logical.Width - 2, logical.Height - 2), new Point(1, logical.Height - 2) };
            using (var path = WidgetRenderer.CreateRoundedRectPath(new Rectangle(Point.Empty, logical), 14, edge))
            using (var bitmap = new Bitmap((int)(logical.Width * scale), (int)(logical.Height * scale)))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.ScaleTransform(scale, scale);
                WidgetRenderer.DrawLayout(g, new Rectangle(Point.Empty, logical), layout, new MetricSnapshot(),
                    backgroundAlpha: 191, dockEdge: edge);
                bool correct = true;
                for (int i = 0; i < corners.Length; i++)
                {
                    Point corner = corners[i];
                    correct &= path.IsVisible(corner) == square[i];
                    Color pixel = bitmap.GetPixel((int)(corner.X * scale), (int)(corner.Y * scale));
                    correct &= (pixel.A > 100) == square[i];
                }
                check(correct, $"{layout} {edge} {scale * 100:0}% keeps screen-side corners square and free corners rounded");
                if (scale == 2 && (layout == WidgetLayout.Horizontal || layout == WidgetLayout.Vertical))
                    bitmap.Save(Path.Combine(output, $"DockShape-{layout}-{edge}.png"), ImageFormat.Png);
            }
        }

        var store = new SettingsStore(Path.Combine(output, "dock-shape-settings.json"));
        using (var monitor = new MonitorService())
        using (var widget = new WidgetForm(monitor, store, new AppSettings { AlwaysOnTop = false, Visible = false }))
        {
            IntPtr handle = widget.Handle;
            var workingArea = Screen.PrimaryScreen.WorkingArea;
            widget.Bounds = new Rectangle(workingArea.Left + 100, workingArea.Top + 30, 448, 60);
            Set(widget, "_dockEdge", DockEdge.Top);
            var visualEdge = typeof(WidgetForm).GetMethod("GetVisualDockEdge", Hidden);
            var updateRegion = typeof(WidgetForm).GetMethod("SetRoundedRegion", Hidden);
            check((DockEdge)visualEdge.Invoke(widget, null) == DockEdge.Top,
                "automatic reveal retains the dock shape while between screen edge and final position");
            updateRegion.Invoke(widget, null);
            check(widget.Region.IsVisible(1, 1), "native window region keeps top dock corners square during automatic reveal");
            Set(widget, "_isDragging", true);
            check((DockEdge)visualEdge.Invoke(widget, null) == DockEdge.None,
                "manual dragging completely away from the edge restores four rounded corners immediately");
            updateRegion.Invoke(widget, null);
            check(!widget.Region.IsVisible(1, 1), "native window region restores rounded corners while manually dragging away");
            widget.Top = workingArea.Top;
            check((DockEdge)visualEdge.Invoke(widget, null) == DockEdge.Top,
                "manual dragging along the top keeps its screen-side corners square");
            Set(widget, "_isDragging", false);
            Set(widget, "_dockEdge", DockEdge.None);
            check((DockEdge)visualEdge.Invoke(widget, null) == DockEdge.None,
                "floating shape remains rounded after undocking");
        }

        RenderDocumentationScreenshots(output);
    }

    private static void RenderDocumentationScreenshots(string output)
    {
        var snapshot = new MetricSnapshot {
            CpuPercent = 20, MemoryPercent = 43,
            MemoryTotalBytes = 16UL * 1024 * 1024 * 1024,
            MemoryUsedBytes = 16UL * 1024 * 1024 * 1024 * 43 / 100,
            VramAvailable = true, VramPercent = 5,
            VramTotalBytes = 16UL * 1024 * 1024 * 1024,
            VramUsedBytes = 16UL * 1024 * 1024 * 1024 * 5 / 100,
            TopMemoryProcesses = new List<ProcessInfo> {
                new ProcessInfo { Name = "Example Browser", MemoryBytes = 1536UL * 1024 * 1024, ProcessCount = 8 },
                new ProcessInfo { Name = "Example Editor", MemoryBytes = 640UL * 1024 * 1024, ProcessCount = 4 },
                new ProcessInfo { Name = "Example Image App", MemoryBytes = 384UL * 1024 * 1024, ProcessCount = 2 } },
            Gpus = new[] {
                new GpuMemoryInfo { Name = "Example discrete GPU", Available = true, Percent = 5 },
                new GpuMemoryInfo { Name = "Example integrated GPU", Available = true, Percent = 75 } } };
        UILanguage language = UIStrings.Language;
        UIStrings.SetLanguage(UILanguage.Chinese);
        foreach (WidgetLayout layout in Enum.GetValues(typeof(WidgetLayout)))
        {
            Size size = WidgetSizeHelper.GetSize(layout, 1f);
            using (var bitmap = new Bitmap(size.Width * 2, size.Height * 2))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.ScaleTransform(2, 2);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                WidgetRenderer.DrawLayout(g, new Rectangle(Point.Empty, size), layout, snapshot);
                bitmap.Save(Path.Combine(output, $"Documentation-{layout}.png"), ImageFormat.Png);
                if (layout == WidgetLayout.Horizontal)
                {
                    g.Clear(Color.Transparent);
                    WidgetRenderer.DrawLayout(g, new Rectangle(Point.Empty, size), layout, snapshot, dockEdge: DockEdge.Top);
                    bitmap.Save(Path.Combine(output, "Documentation-top-docked.png"), ImageFormat.Png);
                }
            }
        }
        UIStrings.SetLanguage(language);
        RenderSliverPreview(output, snapshot);
    }

    private static void RenderSliverPreview(string output, MetricSnapshot snapshot)
    {
        var bounds = new Rectangle(0, 0, 450, 60);
        var strip = new RectangleF(0, 52, 450, 8);
        using (var bitmap = new Bitmap(900, 120))
        using (var g = Graphics.FromImage(bitmap))
        using (var preview = new Bitmap(900, 16))
        using (var previewGraphics = Graphics.FromImage(preview))
        {
            g.ScaleTransform(2, 2);
            g.SetClip(strip);
            typeof(WidgetRenderer).GetMethod("DrawPanel", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { g, bounds, 14, 191, DockEdge.Top });
            typeof(WidgetRenderer).GetMethod("DrawUsageSliver", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { g, strip, false, snapshot, 215 });
            previewGraphics.Clear(Color.White);
            previewGraphics.DrawImageUnscaled(bitmap, 0, -104);
            preview.Save(Path.Combine(output, "SliverSeparated-horizontal.png"), ImageFormat.Png);
        }
    }

    private static void Set(WidgetForm widget, string name, object value) =>
        typeof(WidgetForm).GetField(name, Hidden).SetValue(widget, value);
}
