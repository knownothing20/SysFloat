using System;
using System.Globalization;
using System.Text;
using SysFloat.Configuration;
using SysFloat.Monitoring;

namespace SysFloat.UI
{
    public static class GpuDisplay
    {
        public static bool IsMultiple(MetricSnapshot snapshot) => snapshot.Gpus?.Count > 1;

        public static string PercentText(GpuMemoryInfo gpu) => gpu == null || !gpu.Available ||
            float.IsNaN(gpu.Percent) || float.IsInfinity(gpu.Percent) || gpu.Percent < 0 || gpu.Percent > 100
            ? "--" : gpu.Percent.ToString("0", CultureInfo.InvariantCulture) + "%";

        public static string Summary(MetricSnapshot snapshot)
        {
            if (snapshot.Gpus?.Count > 0)
            {
                var text = new StringBuilder();
                foreach (var gpu in snapshot.Gpus)
                {
                    if (text.Length > 0) text.Append(" | ");
                    text.Append(PercentText(gpu));
                }
                return text.ToString();
            }
            return PercentText(new GpuMemoryInfo { Available = snapshot.VramAvailable, Percent = snapshot.VramPercent });
        }

        public static string ToolTip(MetricSnapshot snapshot)
        {
            var text = new StringBuilder(UIStrings.Get("tip.gpus"));
            int index = 0;
            foreach (var gpu in snapshot.Gpus)
            {
                text.Append("\n\n").Append(UIStrings.Format("tip.gpuRow", ++index, gpu.Name, PercentText(gpu),
                    gpu.Available ? FormatBytes(gpu.UsedBytes) : UIStrings.Get("detail.unavailable"),
                    gpu.TotalBytes > 0 ? FormatBytes(gpu.TotalBytes) : UIStrings.Get("detail.unavailable")));
                if (gpu.SharedAvailable)
                    text.Append('\n').Append(UIStrings.Format("tip.gpuShared", FormatBytes(gpu.SharedUsedBytes)));
            }
            return text.ToString();
        }

        private static string FormatBytes(ulong bytes) => bytes >= 1024UL * 1024 * 1024
            ? (bytes / (1024d * 1024 * 1024)).ToString("0.00") + " GB"
            : (bytes / (1024d * 1024)).ToString("0") + " MB";
    }
}
