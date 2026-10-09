using System.Collections.Generic;

namespace SysFloat.Monitoring
{
    public class MetricSnapshot
    {
        public float CpuPercent { get; set; } = -1;
        public float MemoryPercent { get; set; } = -1;
        public ulong MemoryUsedBytes { get; set; }
        public ulong MemoryTotalBytes { get; set; }
        public float VramPercent { get; set; } = -1;
        public ulong VramUsedBytes { get; set; }
        public ulong VramTotalBytes { get; set; }
        public bool VramAvailable { get; set; }
        public List<ProcessInfo> TopCpuProcesses { get; set; } = new List<ProcessInfo>();
        public List<ProcessInfo> TopMemoryProcesses { get; set; } = new List<ProcessInfo>();
    }

    public class ProcessInfo
    {
        public string Name { get; set; }
        public float CpuPercent { get; set; }
        public float MemoryPercent { get; set; }
        public ulong MemoryBytes { get; set; }
    }
}
