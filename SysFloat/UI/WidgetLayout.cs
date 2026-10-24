using System.Drawing;
using SysFloat.Configuration;

namespace SysFloat.UI
{
    public static class WidgetSizeHelper
    {
        public static Size GetSize(WidgetLayout layout, float dpiScale)
        {
            return GetSize(layout, dpiScale, 1f);
        }

        public static Size GetSize(WidgetLayout layout, float dpiScale, float userScale)
        {
            float scale = dpiScale * userScale;
            switch (layout)
            {
                case WidgetLayout.Horizontal:
                    return new Size((int)(448 * scale), (int)(60 * scale));
                case WidgetLayout.Vertical:
                    return new Size((int)(92 * scale), (int)(340 * scale));
                case WidgetLayout.Expanded:
                    return new Size((int)(540 * scale), (int)(320 * scale));
                case WidgetLayout.Mini:
                    return new Size((int)(204 * scale), (int)(44 * scale));
                default:
                    return new Size((int)(448 * scale), (int)(60 * scale));
            }
        }
    }
}
