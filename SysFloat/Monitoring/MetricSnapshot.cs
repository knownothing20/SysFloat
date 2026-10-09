using System.Collections.Generic;

namespace SysFloat.Monitoring
{
    public enum ProcessMetric
    {
        Cpu = 0,
        Memory = 1,
        Vram = 2
    }

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
        public ProcessMetric RankingMetric { get; set; } = ProcessMetric.Memory;
        public bool CpuProcessesAvailable { get; set; }
        public bool VramProcessesAvailable { get; set; }
        public string VramProcessesStatus { get; set; } = "等待显存数据";
        public List<ProcessInfo> TopCpuProcesses { get; set; } = new List<ProcessInfo>();
        public List<ProcessInfo> TopMemoryProcesses { get; set; } = new List<ProcessInfo>();
        public List<ProcessInfo> TopVramProcesses { get; set; } = new List<ProcessInfo>();
    }

    public class ProcessInfo
    {
        public int ProcessId { get; set; }
        public string Name { get; set; }
        public float CpuPercent { get; set; }
        public float MemoryPercent { get; set; }
        public ulong MemoryBytes { get; set; }
        public ulong VramBytes { get; set; }
    }
}
