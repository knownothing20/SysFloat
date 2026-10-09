using System;

namespace SysFloat.Configuration
{
    public enum WidgetLayout
    {
        Horizontal,
        Vertical,
        Expanded
    }

    public enum WidgetOpacity
    {
        Opaque = 100,
        Percent75 = 75,
        Percent50 = 50
    }

    public class AppSettings
    {
        public WidgetLayout Layout { get; set; } = WidgetLayout.Horizontal;
        public int Left { get; set; } = -1;
        public int Top { get; set; } = -1;
        public bool Visible { get; set; } = true;
        public bool AlwaysOnTop { get; set; } = true;
        public bool Locked { get; set; }
        public WidgetOpacity Opacity { get; set; } = WidgetOpacity.Opaque;

        public bool PowerSavingMode { get; set; } = false;
        public int SamplingIntervalSeconds { get; set; } = 3;
        public bool HighLoadAlertsEnabled { get; set; } = true;
        public int HighLoadThresholdPercent { get; set; } = 90;
        public int HighLoadDurationSeconds { get; set; } = 30;
        public bool AlertPopupEnabled { get; set; } = false;

        public void Normalize()
        {
            if (!Enum.IsDefined(typeof(WidgetLayout), Layout))
                Layout = WidgetLayout.Horizontal;

            if (!Enum.IsDefined(typeof(WidgetOpacity), Opacity))
                Opacity = WidgetOpacity.Opaque;

            if (SamplingIntervalSeconds <= 1)
                SamplingIntervalSeconds = 1;
            else if (SamplingIntervalSeconds <= 3)
                SamplingIntervalSeconds = 3;
            else
                SamplingIntervalSeconds = 5;

            HighLoadThresholdPercent = Math.Max(1, Math.Min(100, HighLoadThresholdPercent));
            HighLoadDurationSeconds = Math.Max(1, Math.Min(300, HighLoadDurationSeconds));
        }
    }
}
