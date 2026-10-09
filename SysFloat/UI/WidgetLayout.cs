using System.Drawing;
using SysFloat.Configuration;

namespace SysFloat.UI
{
    public static class WidgetSizeHelper
    {
        public static Size GetSize(WidgetLayout layout, float dpiScale)
        {
            switch (layout)
            {
                case WidgetLayout.Horizontal:
                    return new Size((int)(448 * dpiScale), (int)(60 * dpiScale));
                case WidgetLayout.Vertical:
                    return new Size((int)(92 * dpiScale), (int)(340 * dpiScale));
                case WidgetLayout.Expanded:
                    return new Size((int)(540 * dpiScale), (int)(320 * dpiScale));
                default:
                    return new Size((int)(448 * dpiScale), (int)(60 * dpiScale));
            }
        }
    }
}
