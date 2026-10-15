using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SysFloat.Monitoring;

internal static class ApplicationGroupingChecks
{
    internal static void VerifyCpuProjection(MonitorService monitor, Action<bool, string> check)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var sampleType = typeof(MonitorService).Assembly.GetType("SysFloat.Monitoring.ApplicationProcessSample", true);
        var identity = Activator.CreateInstance(sampleType, true);
        void Set(string name, object value) => sampleType.GetProperty(name, flags).SetValue(identity, value);
        Set("ProcessId", int.MaxValue); Set("ExecutableName", "dwm.exe");
        Set("CreationTime", 12345UL); Set("CpuTimeTicks", 100000L);
        Set("CpuMonotonicTimestamp", 5000L); Set("CpuTimeAvailable", true);
        var input = Array.CreateInstance(sampleType, 1);
        input.SetValue(identity, 0);
        int generation = (int)typeof(MonitorService).GetField("_processGeneration", flags).GetValue(monitor);
        var samples = (System.Collections.IList)typeof(MonitorService).GetMethod("CollectCpuSamples", flags)
            .Invoke(monitor, new object[] { input, generation });
        check(samples.Count == 1 && (long)samples[0].GetType().GetProperty("TotalProcessorTimeTicks").GetValue(samples[0]) == 100000,
            "CPU projects native samples without reopening PIDs or filtering readable system processes by name");
    }

    internal static void Run(Action<bool, string> check)
    {
        var assembly = typeof(MonitorService).Assembly;
        var sampleType = assembly.GetType("SysFloat.Monitoring.ApplicationProcessSample", true);
        var grouperType = assembly.GetType("SysFloat.Monitoring.ApplicationProcessGrouper", true);
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var group = grouperType.GetMethod("GroupSamples", BindingFlags.Static | BindingFlags.NonPublic);
        object New(int pid, int parent, string path, ulong created, float cpu, ulong vram)
        {
            var sample = Activator.CreateInstance(sampleType, true);
            void Set(string key, object value) => sampleType.GetProperty(key, flags).SetValue(sample, value);
            Set("ProcessId", pid); Set("ParentProcessId", parent);
            Set("ExecutableName", path == null ? "unknown.exe" : Path.GetFileName(path));
            Set("ImagePath", path); Set("CreationTime", created);
            Set("CpuPercent", cpu); Set("VramBytes", vram);
            return sample;
        }
        List<ProcessInfo> Group(List<object> values, ProcessMetric metric, int[] visible)
        {
            var fixture = Array.CreateInstance(sampleType, values.Count);
            for (int i = 0; i < values.Count; i++) fixture.SetValue(values[i], i);
            return (List<ProcessInfo>)group.Invoke(null, new object[] { fixture, metric, visible, null });
        }
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var fixture = new List<object> {
            New(1, 0, shell, 1, 0, 10),
            New(10, 1, @"C:\Apps\A\app.exe", 10, 1, 100),
            New(11, 10, @"C:\Tools\node.exe", 11, 2, 200),
            New(12, 11, @"C:\Tools\rustc.exe", 12, 3, 300),
            New(20, 10, @"C:\Apps\A\viewer.exe", 20, 4, 400),
            New(30, 1, @"D:\Other\node.exe", 30, 5, 500),
            New(40, 1, @"C:\NewParent\parent.exe", 80, 6, 600),
            New(41, 40, @"C:\OldChild\child.exe", 79, 7, 700),
            New(50, 999, @"C:\Orphan\node.exe", 50, 8, 800),
            New(70, 1, @"C:\Daemon\daemon.exe", 70, 9, 900),
            New(71, 70, @"C:\Daemon\worker.exe", 71, 10, 1000),
            New(72, 1, @"C:\OtherDaemon\worker.exe", 72, 11, 1100),
            New(90, 0, null, 90, 12, 1200),
            New(91, 0, null, 91, 13, 1300),
            New(10, 1, @"C:\Apps\A\app.exe", 10, 99, 9900) // Duplicate PID is never counted twice.
        };
        int[] windows = { 1, 10, 20, 30, 40 };
        var cpuRows = Group(fixture, ProcessMetric.Cpu, windows);
        var app = cpuRows.Find(row => row.ProcessId == 10);
        check(app != null && app.ProcessCount == 3 && Math.Abs(app.CpuPercent - 6) < .001,
            "CPU application combines mixed-name descendants exactly once");
        check(cpuRows.Exists(row => row.ProcessId == 20 && row.ProcessCount == 1 && row.CpuPercent == 4),
            "same-directory independent GUI application stays separate");
        check(cpuRows.Exists(row => row.ProcessId == 30 && row.CpuPercent == 5),
            "identically named node from another application is not merged");
        check(cpuRows.Exists(row => row.ProcessId == 41 && row.CpuPercent == 7) &&
            cpuRows.Exists(row => row.ProcessId == 50 && row.CpuPercent == 8),
            "reused and missing parent PIDs cannot invent application ownership");
        check(cpuRows.Exists(row => row.ProcessId == 70 && row.ProcessCount == 2 && row.CpuPercent == 19) &&
            cpuRows.Exists(row => row.ProcessId == 72 && row.ProcessCount == 1),
            "safe same-directory background family combines without merging unrelated worker");
        check(cpuRows.Exists(row => row.ProcessId == 90) && cpuRows.Exists(row => row.ProcessId == 91),
            "unknown executable paths retain isolated PID identities");
        check(cpuRows.Sum(row => row.ProcessCount) == 14 && Math.Abs(cpuRows.Sum(row => row.CpuPercent) - 91) < .001,
            "each readable CPU PID contributes to exactly one application group");
        var vramRows = Group(fixture, ProcessMetric.Vram, windows);
        var gpuApp = vramRows.Find(row => row.ProcessId == 10);
        check(gpuApp != null && gpuApp.ProcessCount == 3 && gpuApp.VramBytes == 600 &&
            vramRows.Sum(row => (decimal)row.VramBytes) == 9110,
            "VRAM reuses application identities and sums each PID exactly once");

        var many = new List<object> { New(100, 0, @"C:\Big\app.exe", 100, -1, 0) };
        for (int i = 0; i < 8; i++) many.Add(New(101 + i, 100, @"C:\Tools\rustc.exe", (ulong)(101 + i), 2, 100));
        for (int i = 0; i < 6; i++) many.Add(New(200 + i, 0, $@"C:\Independent{i}\app.exe", (ulong)(200 + i), 3, 150));
        var ranked = Group(many, ProcessMetric.Cpu, new[] { 100 }).Take(5).ToList();
        check(ranked.Count == 5 && ranked[0].ProcessId == 100 && ranked[0].ProcessCount == 8 && ranked[0].CpuPercent == 16,
            "application of individually small CPU workers ranks first before Top 5 truncation");
        var gpuRanked = Group(many, ProcessMetric.Vram, new[] { 100 }).Take(5).ToList();
        check(gpuRanked[0].ProcessId == 100 && gpuRanked[0].ProcessCount == 8 && gpuRanked[0].VramBytes == 800,
            "VRAM aggregates all workers before Top 5; idle root remains identity, not counted member");
        var clamped = Group(new List<object> {
            New(300, 0, @"C:\Bound\app.exe", 300, 80, ulong.MaxValue),
            New(301, 300, @"C:\Bound\helper.exe", 301, 80, 1)
        }, ProcessMetric.Cpu, new[] { 300 });
        check(clamped.Count == 1 && clamped[0].CpuPercent == 100, "CPU application sum cannot exceed 100 percent");
        var overflow = Group(new List<object> {
            New(300, 0, @"C:\Bound\app.exe", 300, -1, ulong.MaxValue),
            New(301, 300, @"C:\Bound\helper.exe", 301, -1, 1)
        }, ProcessMetric.Vram, new[] { 300 });
        check(overflow.Count == 1 && overflow[0].VramBytes == ulong.MaxValue,
            "VRAM byte sum saturates instead of wrapping on overflow");
        check(Group(new List<object>(), ProcessMetric.Cpu, windows).Count == 0,
            "empty process identity sample produces no fabricated application groups");
        var cycle = Group(new List<object> {
            New(400, 401, @"C:\Cycle\a.exe", 400, 1, 10),
            New(401, 400, @"C:\Cycle\b.exe", 400, 2, 20)
        }, ProcessMetric.Cpu, Array.Empty<int>());
        check(cycle.Sum(row => row.ProcessCount) == 2 && Math.Abs(cycle.Sum(row => row.CpuPercent) - 3) < .001,
            "cyclic parent metadata terminates without losing or duplicating metric members");
        var invalid = Group(new List<object> {
            New(500, 0, @"C:\Invalid\a.exe", 500, float.NaN, 0),
            New(501, 0, @"C:\Invalid\b.exe", 501, float.PositiveInfinity, 0),
            New(502, 0, @"C:\Invalid\c.exe", 502, -1, 0)
        }, ProcessMetric.Cpu, Array.Empty<int>());
        check(invalid.Count == 0, "missing or nonfinite CPU deltas are never fabricated as valid application usage");
        var launched = Group(new List<object> {
            New(600, 0, @"C:\Editor\editor.exe", 600, 1, 0),
            New(601, 600, @"C:\Program Files\PowerShell\7\pwsh.exe", 601, 2, 0),
            New(602, 601, @"C:\Rust\rustc.exe", 602, 3, 0)
        }, ProcessMetric.Cpu, new[] { 600 });
        check(launched.Count == 1 && launched[0].ProcessId == 600 && launched[0].ProcessCount == 3 &&
            launched[0].CpuPercent == 6,
            "compiler launched through shell still belongs to its visible application ancestor");
        var lowerPid = Group(new List<object> {
            New(700, 0, @"C:\Identity\app.exe", 700, 1, 0),
            New(5, 700, @"C:\Identity\helper.exe", 701, 2, 0)
        }, ProcessMetric.Cpu, new[] { 700 });
        check(lowerPid.Count == 1 && lowerPid[0].ProcessId == 700 && lowerPid[0].ProcessCount == 2,
            "application identity stays at the root when a newer child receives a lower PID");
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemTools = Group(new List<object> {
            New(800, 0, Path.Combine(windowsDirectory, "System32", "wscript.exe"), 800, 1, 0),
            New(801, 800, Path.Combine(windowsDirectory, "System32", "cscript.exe"), 801, 2, 0)
        }, ProcessMetric.Cpu, Array.Empty<int>());
        check(systemTools.Count == 2, "shared Windows tools directory is not treated as one background application");
        var driveRoot = Group(new List<object> {
            New(900, 0, @"D:\launcher.exe", 900, 1, 0),
            New(901, 900, @"D:\OtherApp\worker.exe", 901, 2, 0)
        }, ProcessMetric.Cpu, Array.Empty<int>());
        check(driveRoot.Count == 2, "drive root is not treated as a shared application installation directory");
    }
}
