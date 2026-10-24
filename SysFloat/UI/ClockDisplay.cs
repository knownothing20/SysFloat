using System;
using System.Globalization;
using SysFloat.Configuration;

namespace SysFloat.UI
{
    /// <summary>按时区设置换算悬浮窗时钟显示用的本地时间、缩写与副标题。</summary>
    public static class ClockDisplay
    {
        private static readonly TimeZoneInfo BeijingZone = FindZone("China Standard Time");

        private static TimeZoneInfo FindZone(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { return null; }
        }

        public static DateTime GetTime(DateTime utc, ClockTimeZone zone)
        {
            DateTime utcValue = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            switch (zone)
            {
                case ClockTimeZone.Beijing:
                    return BeijingZone != null
                        ? TimeZoneInfo.ConvertTimeFromUtc(utcValue, BeijingZone)
                        : utcValue.AddHours(8);
                case ClockTimeZone.Utc:
                    return utcValue;
                case ClockTimeZone.Local:
                    return TimeZoneInfo.ConvertTimeFromUtc(utcValue, TimeZoneInfo.Local);
                default:
                    return PacificClock.GetPacificTime(utcValue);
            }
        }

        public static string GetZoneName(ClockTimeZone zone)
        {
            switch (zone)
            {
                case ClockTimeZone.Beijing: return UIStrings.Get("zone.beijing");
                case ClockTimeZone.Utc: return UIStrings.Get("zone.utc");
                case ClockTimeZone.Local: return UIStrings.Get("zone.local");
                default: return UIStrings.Get("zone.pacific");
            }
        }

        public static string GetShortZone(DateTime utc, ClockTimeZone zone)
        {
            switch (zone)
            {
                case ClockTimeZone.Beijing: return "UTC+8";
                case ClockTimeZone.Utc: return "UTC";
                case ClockTimeZone.Local: return OffsetLabel(TimeZoneInfo.Local.GetUtcOffset(utc));
                default: return PacificClock.GetAbbreviation(utc);
            }
        }

        private static string OffsetLabel(TimeSpan offset)
        {
            if (offset == TimeSpan.Zero) return "UTC";
            string sign = offset < TimeSpan.Zero ? "-" : "+";
            double hours = Math.Abs(offset.TotalHours);
            return "UTC" + sign + (hours == Math.Floor(hours)
                ? ((int)hours).ToString(CultureInfo.InvariantCulture)
                : hours.ToString("0.#", CultureInfo.InvariantCulture));
        }

        public static string FormatTime(DateTime utc, ClockTimeZone zone) =>
            GetTime(utc, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

        public static string FormatDate(DateTime utc, ClockTimeZone zone) =>
            GetTime(utc, zone).ToString("MM/dd", CultureInfo.InvariantCulture);

        public static string FormatFullDate(DateTime utc, ClockTimeZone zone)
        {
            DateTime time = GetTime(utc, zone);
            return UIStrings.Language == UILanguage.English
                ? time.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)
                : time.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
        }

        /// <summary>竖版时钟完整副标题（含时区名）；窄面板放不下时用紧凑版。</summary>
        public static string GetSubtitleFull(DateTime utc, ClockTimeZone zone) =>
            GetZoneName(zone) + " " + GetShortZone(utc, zone) + " " + FormatDate(utc, zone);

        /// <summary>竖版时钟紧凑副标题；太平洋不带地名前缀（窄面板放不下）。</summary>
        public static string GetSubtitleCompact(DateTime utc, ClockTimeZone zone) =>
            zone == ClockTimeZone.Pacific
                ? PacificClock.GetAbbreviation(utc) + " " + FormatDate(utc, zone)
                : GetZoneName(zone) + " " + FormatDate(utc, zone);

        /// <summary>横版时钟副标题。</summary>
        public static string GetSubtitleWide(DateTime utc, ClockTimeZone zone) =>
            GetZoneName(zone) + "  " + GetShortZone(utc, zone) + "  " + FormatDate(utc, zone);

        /// <summary>展开版头部的位置标签。</summary>
        public static string GetHeaderLabel(DateTime utc, ClockTimeZone zone) =>
            zone == ClockTimeZone.Utc ? "UTC" : GetZoneName(zone) + " · " + GetShortZone(utc, zone);

        /// <summary>与北京时间的时差（小时，可带小数）：正数表示比北京慢。</summary>
        public static double HoursBehindBeijing(DateTime utc, ClockTimeZone zone)
        {
            var beijing = GetTime(utc, ClockTimeZone.Beijing);
            var shown = GetTime(utc, zone);
            return Math.Round((beijing - shown).TotalHours, 1);
        }
    }
}
