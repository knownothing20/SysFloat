using System;
using System.Globalization;

namespace SysFloat.UI
{
    public static class PacificClock
    {
        private static readonly TimeZoneInfo PacificTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

        public static DateTime GetPacificTime(DateTime utc)
        {
            DateTime utcValue = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            return TimeZoneInfo.ConvertTimeFromUtc(utcValue, PacificTimeZone);
        }

        public static string GetAbbreviation(DateTime utc)
        {
            DateTime utcValue = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            DateTimeOffset pacific = TimeZoneInfo.ConvertTime(new DateTimeOffset(utcValue), PacificTimeZone);
            return PacificTimeZone.IsDaylightSavingTime(pacific) ? "PDT" : "PST";
        }

        public static string FormatTime(DateTime utc) => GetPacificTime(utc).ToString("HH:mm", CultureInfo.InvariantCulture);
        public static string FormatDate(DateTime utc) => GetPacificTime(utc).ToString("MM/dd", CultureInfo.InvariantCulture);
        public static string FormatFullDate(DateTime utc) => GetPacificTime(utc).ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    }
}
