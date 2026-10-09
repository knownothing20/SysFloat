using System;
using System.Collections.Generic;
using System.Linq;

namespace SysFloat.Monitoring
{
    internal sealed class ProcessCpuSample
    {
        public int ProcessId { get; set; }
        public string Name { get; set; }
        public long StartTimeTicks { get; set; }
        public long TotalProcessorTimeTicks { get; set; }
        public long MonotonicTimestamp { get; set; }
    }

    internal sealed class ProcessCpuUpdate
    {
        public bool Available { get; set; }
        public List<ProcessInfo> Processes { get; set; } = new List<ProcessInfo>();
    }

    internal sealed class ProcessCpuTracker
    {
        private const double MaximumSampleGapSeconds = 60d;
        private readonly object _sync = new object();
        private Dictionary<int, ProcessCpuSample> _previous = new Dictionary<int, ProcessCpuSample>();
        private int _generation;

        public ProcessCpuUpdate Update(IEnumerable<ProcessCpuSample> samples, int generation, int processorCount,
            long timestampFrequency)
        {
            lock (_sync)
            {
                if (generation != _generation)
                    return new ProcessCpuUpdate();

                var next = new Dictionary<int, ProcessCpuSample>();
                var ranked = new List<ProcessInfo>();

                if (processorCount <= 0 || timestampFrequency <= 0)
                {
                    _previous = next;
                    return new ProcessCpuUpdate();
                }

                if (samples != null)
                {
                    foreach (var sample in samples)
                    {
                        if (!IsUsable(sample) || next.ContainsKey(sample.ProcessId))
                            continue;

                        next.Add(sample.ProcessId, sample);
                        if (!_previous.TryGetValue(sample.ProcessId, out var previous) ||
                            previous.StartTimeTicks != sample.StartTimeTicks ||
                            sample.TotalProcessorTimeTicks < previous.TotalProcessorTimeTicks ||
                            sample.MonotonicTimestamp <= previous.MonotonicTimestamp)
                            continue;

                        long elapsedTicks = sample.MonotonicTimestamp - previous.MonotonicTimestamp;
                        double elapsedSeconds = elapsedTicks / (double)timestampFrequency;
                        if (elapsedSeconds <= 0 || elapsedSeconds > MaximumSampleGapSeconds)
                            continue;

                        long processorTicks = sample.TotalProcessorTimeTicks - previous.TotalProcessorTimeTicks;
                        double cpuPercent = processorTicks / (double)TimeSpan.TicksPerSecond / elapsedSeconds /
                            processorCount * 100d;
                        if (double.IsNaN(cpuPercent) || double.IsInfinity(cpuPercent) || cpuPercent < 0)
                            continue;

                        ranked.Add(new ProcessInfo
                        {
                            ProcessId = sample.ProcessId,
                            Name = sample.Name,
                            CpuPercent = (float)Math.Min(100d, cpuPercent)
                        });
                    }
                }

                _previous = next;
                var top = ranked
                    .OrderByDescending(process => process.CpuPercent)
                    .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();

                return new ProcessCpuUpdate
                {
                    Available = ranked.Count > 0,
                    Processes = top
                };
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                _generation++;
                _previous.Clear();
            }
        }

        public void Reset(int generation)
        {
            lock (_sync)
            {
                _generation = generation;
                _previous.Clear();
            }
        }

        private static bool IsUsable(ProcessCpuSample sample)
        {
            return sample != null &&
                sample.ProcessId > 0 &&
                !string.IsNullOrWhiteSpace(sample.Name) &&
                sample.StartTimeTicks > 0 &&
                sample.TotalProcessorTimeTicks >= 0 &&
                sample.MonotonicTimestamp > 0;
        }
    }
}
