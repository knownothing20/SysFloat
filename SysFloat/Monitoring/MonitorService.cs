using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace SysFloat.Monitoring
{
    public class MonitorService : IDisposable
    {
        private static readonly HashSet<string> FilteredProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Memory Compression",
            "Registry",
            "smss",
            "csrss",
            "wininit",
            "services",
            "lsass",
            "winlogon",
            "dwm",
            "ntoskrnl"
        };

        private readonly CpuMonitor _cpuMonitor = new CpuMonitor();
        private readonly MemoryMonitor _memoryMonitor = new MemoryMonitor();
        private readonly GpuMemoryMonitor _gpuMonitor = new GpuMemoryMonitor();
        private readonly ProcessCpuTracker _processCpuTracker = new ProcessCpuTracker();
        private readonly ProcessVramMonitor _processVramMonitor = new ProcessVramMonitor();
        private readonly object _lifecycleLock = new object();
        private readonly object _processDataLock = new object();
        private readonly object _gpuLock = new object();
        private Timer _timer;
        private Timer _processTimer;
        private volatile bool _started;
        private volatile bool _collectProcesses;
        private volatile bool _disposed;
        private int _pollingIntervalMilliseconds = 1000;
        private int _metricTickRunning;
        private int _processTickRunning;
        private int _processGeneration;
        private ProcessMetric _rankingMetric = ProcessMetric.Memory;
        private bool _cpuProcessesAvailable;
        private bool _vramProcessesAvailable;
        private string _vramProcessesStatus = ProcessVramMonitor.WaitingStatus;
        private List<ProcessInfo> _topCpuProcesses = new List<ProcessInfo>();
        private List<ProcessInfo> _topMemoryProcesses = new List<ProcessInfo>();
        private List<ProcessInfo> _topVramProcesses = new List<ProcessInfo>();

        public event Action<MetricSnapshot> OnDataUpdated;

        public void Start()
        {
            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                if (_started)
                    return;

                _started = true;
                _processVramMonitor.SetGeneration(Volatile.Read(ref _processGeneration),
                    _collectProcesses && _rankingMetric == ProcessMetric.Vram);
                _timer = new Timer(OnTimerTick, null, _pollingIntervalMilliseconds, _pollingIntervalMilliseconds);
                if (_collectProcesses)
                    _processTimer = new Timer(OnProcessTimerTick, null, 0, 5000);
            }
        }

        public void ConfigurePolling(int intervalMilliseconds, bool collectProcesses,
            ProcessMetric metric = ProcessMetric.Memory)
        {
            int safeIntervalMilliseconds = intervalMilliseconds > 0 ? intervalMilliseconds : 1000;
            if (!Enum.IsDefined(typeof(ProcessMetric), metric))
                metric = ProcessMetric.Memory;

            lock (_lifecycleLock)
            {
                ThrowIfDisposed();

                bool processConfigurationChanged = _collectProcesses != collectProcesses || _rankingMetric != metric;
                _pollingIntervalMilliseconds = safeIntervalMilliseconds;
                _collectProcesses = collectProcesses;
                _rankingMetric = metric;

                if (processConfigurationChanged)
                {
                    int generation = Interlocked.Increment(ref _processGeneration);
                    ClearProcessSnapshots();
                    _processCpuTracker.Reset(generation);
                    _processVramMonitor.SetGeneration(generation, collectProcesses && metric == ProcessMetric.Vram);
                }

                if (_started)
                {
                    _timer?.Change(_pollingIntervalMilliseconds, _pollingIntervalMilliseconds);

                    if (collectProcesses && _processTimer == null)
                        _processTimer = new Timer(OnProcessTimerTick, null, 0, 5000);
                    else if (collectProcesses && processConfigurationChanged)
                        _processTimer?.Change(0, 5000);
                    else if (!collectProcesses && _processTimer != null)
                    {
                        _processTimer.Dispose();
                        _processTimer = null;
                    }
                }
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                _started = false;
                int generation = Interlocked.Increment(ref _processGeneration);
                _timer?.Dispose();
                _timer = null;
                _processTimer?.Dispose();
                _processTimer = null;
                ClearProcessSnapshots();
                _processCpuTracker.Reset(generation);
                _processVramMonitor.SetGeneration(generation, false);
            }
        }

        private void OnProcessTimerTick(object state)
        {
            if (Interlocked.CompareExchange(ref _processTickRunning, 1, 0) != 0)
                return;

            int generation = -1;
            try
            {
                ProcessMetric metric;
                lock (_lifecycleLock)
                {
                    if (!_started || !_collectProcesses || _disposed)
                        return;

                    generation = Volatile.Read(ref _processGeneration);
                    metric = _rankingMetric;
                }

                ProcessCollectionResult result = CollectProcessMetric(metric, generation);
                if (!IsProcessGenerationCurrent(generation))
                    return;

                lock (_processDataLock)
                {
                    if (!IsProcessGenerationCurrent(generation))
                        return;

                    switch (metric)
                    {
                        case ProcessMetric.Cpu:
                            _topCpuProcesses = result.Processes;
                            _cpuProcessesAvailable = result.Available;
                            break;
                        case ProcessMetric.Memory:
                            _topMemoryProcesses = result.Processes;
                            break;
                        case ProcessMetric.Vram:
                            _topVramProcesses = result.Processes;
                            _vramProcessesAvailable = result.Available;
                            _vramProcessesStatus = result.Status;
                            break;
                    }
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _processTickRunning, 0);
                if (generation >= 0 && Volatile.Read(ref _processGeneration) != generation)
                    ScheduleImmediateProcessSample();
            }
        }

        private ProcessCollectionResult CollectProcessMetric(ProcessMetric metric, int generation)
        {
            switch (metric)
            {
                case ProcessMetric.Cpu:
                    var cpuUpdate = _processCpuTracker.Update(CollectCpuSamples(generation), generation,
                        Environment.ProcessorCount, Stopwatch.Frequency);
                    return new ProcessCollectionResult
                    {
                        Processes = cpuUpdate.Processes,
                        Available = cpuUpdate.Available
                    };

                case ProcessMetric.Vram:
                    return CollectVramProcesses(generation);

                default:
                    return new ProcessCollectionResult
                    {
                        Processes = CollectMemoryProcesses(generation),
                        Available = true
                    };
            }
        }

        private List<ProcessCpuSample> CollectCpuSamples(int generation)
        {
            var samples = new List<ProcessCpuSample>();
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return samples;
            }

            foreach (var process in processes)
            {
                try
                {
                    if (!IsProcessGenerationCurrent(generation))
                        continue;

                    string name = process.ProcessName;
                    if (FilteredProcesses.Contains(name))
                        continue;

                    DateTime startTime = process.StartTime.ToUniversalTime();
                    long processorTimeTicks = process.TotalProcessorTime.Ticks;
                    samples.Add(new ProcessCpuSample
                    {
                        ProcessId = process.Id,
                        Name = name,
                        StartTimeTicks = startTime.Ticks,
                        TotalProcessorTimeTicks = processorTimeTicks,
                        MonotonicTimestamp = Stopwatch.GetTimestamp()
                    });
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }

            return samples;
        }

        private List<ProcessInfo> CollectMemoryProcesses(int generation)
        {
            var processesByMemory = new List<ProcessInfo>();
            Process[] processes;
            try
            {
                processes = Process.GetProcesses();
            }
            catch
            {
                return processesByMemory;
            }

            foreach (var process in processes)
            {
                try
                {
                    if (!IsProcessGenerationCurrent(generation))
                        continue;

                    string name = process.ProcessName;
                    if (FilteredProcesses.Contains(name))
                        continue;

                    long workingSetBytes = process.WorkingSet64;
                    if (workingSetBytes < 0)
                        continue;

                    processesByMemory.Add(new ProcessInfo
                    {
                        ProcessId = process.Id,
                        Name = name,
                        MemoryBytes = (ulong)workingSetBytes
                    });
                }
                catch { }
                finally
                {
                    process.Dispose();
                }
            }

            return processesByMemory
                .OrderByDescending(process => process.MemoryBytes)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();
        }

        private ProcessCollectionResult CollectVramProcesses(int generation)
        {
            ProcessVramSample sample = _processVramMonitor.ReadUsageByProcess(generation);
            var processes = new List<ProcessInfo>();

            if (sample.Available)
            {
                foreach (var usage in sample.UsageByProcessId)
                {
                    if (!IsProcessGenerationCurrent(generation))
                        break;

                    try
                    {
                        using (Process process = Process.GetProcessById(usage.Key))
                        {
                            processes.Add(new ProcessInfo
                            {
                                ProcessId = usage.Key,
                                Name = process.ProcessName,
                                VramBytes = usage.Value
                            });
                        }
                    }
                    catch { }
                }
            }

            processes = processes
                .OrderByDescending(process => process.VramBytes)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToList();

            string status = sample.Status;
            if (sample.Available && processes.Count == 0)
                status = ProcessVramMonitor.EmptyStatus;

            return new ProcessCollectionResult
            {
                Processes = processes,
                Available = sample.Available,
                Status = status
            };
        }

        private bool IsProcessGenerationCurrent(int generation)
        {
            return generation >= 0 && _started && _collectProcesses && !_disposed &&
                Volatile.Read(ref _processGeneration) == generation;
        }

        private void ScheduleImmediateProcessSample()
        {
            lock (_lifecycleLock)
            {
                if (_started && _collectProcesses && !_disposed)
                    _processTimer?.Change(0, 5000);
            }
        }

        private void OnTimerTick(object state)
        {
            if (!_started || _disposed || Interlocked.Exchange(ref _metricTickRunning, 1) != 0)
                return;

            try
            {
                if (!_started || _disposed)
                    return;

                var snapshot = new MetricSnapshot();

                snapshot.CpuPercent = _cpuMonitor.GetCpuPercent();

                var mem = _memoryMonitor.GetMemoryInfo();
                snapshot.MemoryPercent = mem.percent;
                snapshot.MemoryUsedBytes = mem.used;
                snapshot.MemoryTotalBytes = mem.total;

                (float percent, ulong used, ulong total, bool available) vram;
                lock (_gpuLock)
                {
                    vram = _disposed ? (-1, 0, 0, false) : _gpuMonitor.GetVramInfo();
                }
                snapshot.VramPercent = vram.percent;
                snapshot.VramUsedBytes = vram.used;
                snapshot.VramTotalBytes = vram.total;
                snapshot.VramAvailable = vram.available;

                lock (_lifecycleLock)
                {
                    snapshot.RankingMetric = _rankingMetric;
                    lock (_processDataLock)
                    {
                        snapshot.TopCpuProcesses = CloneProcessList(_topCpuProcesses);
                        snapshot.TopMemoryProcesses = CloneProcessList(_topMemoryProcesses);
                        snapshot.TopVramProcesses = CloneProcessList(_topVramProcesses);
                        snapshot.CpuProcessesAvailable = _cpuProcessesAvailable;
                        snapshot.VramProcessesAvailable = _vramProcessesAvailable;
                        snapshot.VramProcessesStatus = _vramProcessesStatus;
                    }
                }

                if (!_started || _disposed)
                    return;

                var handler = OnDataUpdated;
                handler?.Invoke(snapshot);
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _metricTickRunning, 0);
            }
        }

        private void ClearProcessSnapshots()
        {
            lock (_processDataLock)
            {
                _topCpuProcesses = new List<ProcessInfo>();
                _topMemoryProcesses = new List<ProcessInfo>();
                _topVramProcesses = new List<ProcessInfo>();
                _cpuProcessesAvailable = false;
                _vramProcessesAvailable = false;
                _vramProcessesStatus = ProcessVramMonitor.WaitingStatus;
            }
        }

        private static List<ProcessInfo> CloneProcessList(IEnumerable<ProcessInfo> processes)
        {
            return processes.Select(process => new ProcessInfo
            {
                ProcessId = process.ProcessId,
                Name = process.Name,
                CpuPercent = process.CpuPercent,
                MemoryPercent = process.MemoryPercent,
                MemoryBytes = process.MemoryBytes,
                VramBytes = process.VramBytes
            }).ToList();
        }

        private sealed class ProcessCollectionResult
        {
            public List<ProcessInfo> Processes { get; set; } = new List<ProcessInfo>();
            public bool Available { get; set; }
            public string Status { get; set; } = string.Empty;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(MonitorService));
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _started = false;
                int generation = Interlocked.Increment(ref _processGeneration);
                _timer?.Dispose();
                _timer = null;
                _processTimer?.Dispose();
                _processTimer = null;
                ClearProcessSnapshots();
                _processCpuTracker.Reset(generation);
                _processVramMonitor.Dispose();
            }

            lock (_gpuLock)
            {
                _gpuMonitor.Dispose();
            }
        }
    }
}
