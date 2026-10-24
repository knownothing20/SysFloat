using System.Collections.Generic;

namespace SysFloat.Configuration
{
    /// <summary>
    /// 界面字符串表：当前仅中文与英文两种。默认语言由系统界面语言决定
    /// （AppSettings.DetectDefaultLanguage），可在监控设置中切换。
    /// </summary>
    public static class UIStrings
    {
        public static UILanguage Language { get; private set; } = AppSettings.DetectDefaultLanguage();

        public static void SetLanguage(UILanguage language)
        {
            if (language == UILanguage.Chinese || language == UILanguage.English)
                Language = language;
        }

        public static string Get(string key)
        {
            var table = Language == UILanguage.English ? English : Chinese;
            return table.TryGetValue(key, out string value) ? value : key;
        }

        // 常用格式串的便捷包装。
        public static string Format(string key, params object[] args) => string.Format(Get(key), args);

        private static readonly Dictionary<string, string> Chinese = new Dictionary<string, string>
        {
            // 右键菜单（悬浮窗与托盘共用键）
            { "menu.show", "显示悬浮窗" },
            { "menu.horizontal", "横版" },
            { "menu.vertical", "竖版" },
            { "menu.expanded", "展开版" },
            { "menu.mini", "迷你版" },
            { "menu.size", "大小" },
            { "menu.scale75", "75%" },
            { "menu.scale100", "100%" },
            { "menu.scale125", "125%" },
            { "menu.scale150", "150%" },
            { "menu.sizeHint", "提示：Ctrl+滚轮 微调大小" },
            { "menu.topMost", "始终置顶" },
            { "menu.lock", "锁定位置" },
            { "menu.snap", "边缘吸附" },
            { "menu.autoStart", "开机自启" },
            { "menu.edgeHide", "贴边隐藏" },
            { "menu.opacityOpaque", "背景不透明" },
            { "menu.opacity75", "背景 75%" },
            { "menu.opacity50", "背景 50%" },
            { "menu.monitorSettings", "监控设置…" },
            { "menu.about", "关于" },
            { "menu.exit", "退出" },

            // 时区
            { "zone.pacific", "太平洋" },
            { "zone.beijing", "北京" },
            { "zone.utc", "UTC" },
            { "zone.local", "本机" },

            // 悬停提示
            { "tip.zoneTime", "{0}时间：{1} {2}" },
            { "tip.sameAsBeijing", "与北京时间一致" },
            { "tip.behindBeijing", "比北京时间慢 {0} 小时" },
            { "tip.aheadBeijing", "比北京时间快 {0} 小时" },
            { "tip.cpuUnavailable", "CPU：不可用" },
            { "tip.cpuSummary", "CPU：{0:0.0}%\n最近区间：{1}" },
            { "tip.noHistory", "暂无历史" },
            { "tip.ramUnavailable", "RAM：不可用" },
            { "tip.ramSummary", "RAM：{0}\n已用 {1} / 共 {2}" },
            { "tip.vramUnavailable", "VRAM：不可用" },
            { "tip.vramSummary", "VRAM：{0}\n已用 {1} / 共 {2}" },
            { "tip.vramTotalUnknown", "VRAM：已用 {0}\n总容量暂不可用" },

            // 展开版详情与进程排行
            { "detail.unavailable", "不可用" },
            { "detail.range", "区间 {0:0}–{1:0}%" },
            { "detail.noHistory", "最近区间 --" },
            { "detail.usedPercent", "已用 {0:0}%" },
            { "rank.cpuTitle", "应用 CPU Top {0}" },
            { "rank.vramTitle", "应用显存 Top {0}" },
            { "rank.memoryTitle", "应用内存 Top {0}" },
            { "rank.cpuSubtitle", "应用合计 · 约 5 秒平均" },
            { "rank.vramSubtitle", "应用合计 · 专用显存" },
            { "rank.memorySubtitle", "应用合计 · 私有工作集" },
            { "rank.cpuEmpty", "暂无 CPU 占用进程" },
            { "rank.vramEmpty", "暂无显存占用进程" },
            { "rank.memoryEmpty", "暂无内存占用进程" },
            { "rank.switching", "正在切换排行…" },
            { "rank.samplingCpu", "正在采样 CPU…" },
            { "rank.vramUnsupported", "系统未提供进程显存数据" },
            { "rank.memoryUnsupported", "系统未提供私有工作集数据" },
            { "rank.unknownProcess", "未知进程" },

            // 监控设置对话框
            { "dlg.title", "监控设置" },
            { "dlg.subtitle", "SysFloat  ·  按需调整采样和提醒" },
            { "dlg.samplingCard", "采样与性能" },
            { "dlg.powerSaving", "省资源模式" },
            { "dlg.alertsToggle", "启用提醒" },
            { "dlg.samplingDesc1", "普通模式每秒采样；省资源模式按所选间隔采样。" },
            { "dlg.samplingDesc2", "展开版才刷新进程排行；隐藏时停止排行采集。" },
            { "dlg.interval", "采样间隔" },
            { "dlg.seconds", "{0} 秒" },
            { "dlg.alertsCard", "高占用提醒" },
            { "dlg.alertsDesc", "超过阈值并持续指定时间后提醒。" },
            { "dlg.threshold", "触发阈值" },
            { "dlg.duration", "持续时间" },
            { "dlg.secondsShort", "秒" },
            { "dlg.popup", "托盘通知：恢复后可再次提醒（至少间隔 5 分钟）" },
            { "dlg.clockCard", "时钟与语言" },
            { "dlg.clockDesc", "时钟显示的时区与界面语言；语言首次按系统自动选择。" },
            { "dlg.timeZone", "显示时区" },
            { "dlg.language", "界面语言" },
            { "dlg.save", "保存" },
            { "dlg.cancel", "取消" },

            // 其他
            { "about.body", "轻量级系统监控悬浮窗" },
            { "about.title", "关于 SysFloat" },
            { "alert.title", "SysFloat · 高占用提醒" },
            { "alert.metric.ram", "内存" },
            { "alert.metric.vram", "显存" },
            { "alert.suffix", "，持续至少 {0} 秒" },
        };

        private static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            { "menu.show", "Show Widget" },
            { "menu.horizontal", "Horizontal" },
            { "menu.vertical", "Vertical" },
            { "menu.expanded", "Expanded" },
            { "menu.mini", "Mini" },
            { "menu.size", "Size" },
            { "menu.scale75", "75%" },
            { "menu.scale100", "100%" },
            { "menu.scale125", "125%" },
            { "menu.scale150", "150%" },
            { "menu.sizeHint", "Tip: Ctrl+Wheel to resize" },
            { "menu.topMost", "Always on Top" },
            { "menu.lock", "Lock Position" },
            { "menu.snap", "Snap to Edges" },
            { "menu.autoStart", "Start with Windows" },
            { "menu.edgeHide", "Auto-hide at Edge" },
            { "menu.opacityOpaque", "Opaque Background" },
            { "menu.opacity75", "Background 75%" },
            { "menu.opacity50", "Background 50%" },
            { "menu.monitorSettings", "Monitoring Settings…" },
            { "menu.about", "About" },
            { "menu.exit", "Exit" },

            { "zone.pacific", "Pacific" },
            { "zone.beijing", "Beijing" },
            { "zone.utc", "UTC" },
            { "zone.local", "Local" },

            { "tip.zoneTime", "{0} time: {1} {2}" },
            { "tip.sameAsBeijing", "Same as Beijing time" },
            { "tip.behindBeijing", "{0} h behind Beijing" },
            { "tip.aheadBeijing", "{0} h ahead of Beijing" },
            { "tip.cpuUnavailable", "CPU: unavailable" },
            { "tip.cpuSummary", "CPU: {0:0.0}%\nRecent range: {1}" },
            { "tip.noHistory", "no history yet" },
            { "tip.ramUnavailable", "RAM: unavailable" },
            { "tip.ramSummary", "RAM: {0}\nUsed {1} / Total {2}" },
            { "tip.vramUnavailable", "VRAM: unavailable" },
            { "tip.vramSummary", "VRAM: {0}\nUsed {1} / Total {2}" },
            { "tip.vramTotalUnknown", "VRAM: used {0}\ntotal unavailable" },

            { "detail.unavailable", "N/A" },
            { "detail.range", "Range {0:0}–{1:0}%" },
            { "detail.noHistory", "Recent range --" },
            { "detail.usedPercent", "Used {0:0}%" },
            { "rank.cpuTitle", "App CPU Top {0}" },
            { "rank.vramTitle", "App VRAM Top {0}" },
            { "rank.memoryTitle", "App Memory Top {0}" },
            { "rank.cpuSubtitle", "App totals · ~5 s average" },
            { "rank.vramSubtitle", "App totals · dedicated VRAM" },
            { "rank.memorySubtitle", "App totals · private working set" },
            { "rank.cpuEmpty", "No CPU usage reported" },
            { "rank.vramEmpty", "No VRAM usage reported" },
            { "rank.memoryEmpty", "No memory usage reported" },
            { "rank.switching", "Switching ranking…" },
            { "rank.samplingCpu", "Sampling CPU…" },
            { "rank.vramUnsupported", "Process VRAM data not provided by the system" },
            { "rank.memoryUnsupported", "Private working set data not provided by the system" },
            { "rank.unknownProcess", "Unknown" },

            { "dlg.title", "Monitoring Settings" },
            { "dlg.subtitle", "SysFloat  ·  tune sampling and alerts" },
            { "dlg.samplingCard", "Sampling & Performance" },
            { "dlg.powerSaving", "Power Saving Mode" },
            { "dlg.alertsToggle", "Enable Alerts" },
            { "dlg.samplingDesc1", "Normal mode samples every second; power saving uses the selected interval." },
            { "dlg.samplingDesc2", "Rankings refresh only while expanded; hiding stops collection." },
            { "dlg.interval", "Sampling Interval" },
            { "dlg.seconds", "{0} s" },
            { "dlg.alertsCard", "High-Load Alerts" },
            { "dlg.alertsDesc", "Alerts after the threshold is exceeded for the set duration." },
            { "dlg.threshold", "Threshold" },
            { "dlg.duration", "Duration" },
            { "dlg.secondsShort", "s" },
            { "dlg.popup", "Tray notification: re-alert after recovery (5 min minimum)" },
            { "dlg.clockCard", "Clock & Language" },
            { "dlg.clockDesc", "Clock time zone and UI language; language auto-detected from the system on first run." },
            { "dlg.timeZone", "Time Zone" },
            { "dlg.language", "Language" },
            { "dlg.save", "Save" },
            { "dlg.cancel", "Cancel" },

            { "about.body", "Lightweight system monitor widget" },
            { "about.title", "About SysFloat" },
            { "alert.title", "SysFloat · High Load Alert" },
            { "alert.metric.ram", "RAM" },
            { "alert.metric.vram", "VRAM" },
            { "alert.suffix", ", sustained for at least {0} s" },
        };
    }
}
