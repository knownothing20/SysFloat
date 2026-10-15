using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace SysFloat.Monitoring
{
    public class MonitorService : IDisposable
    {
        private readonly CpuMonitor _cpuMonitor = new CpuMonitor();
        private readonly MemoryMonitor _memoryMonitor = new MemoryMonitor();
        private readonly GpuMemoryMonitor _gpuMonitor = new GpuMemoryMonitor();
        private readonly ProcessMemoryCollector _processMemoryCollector = new ProcessMemoryCollector();
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
        private bool _memoryProcessesAvailable = true;
        private string _memoryProcessesStatus = string.Empty;
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
                            _memoryProcessesAvailable = result.Available;
                            _memoryProcessesStatus = result.Status;
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
                    return CollectCpuProcesses(generation);

                case ProcessMetric.Vram:
                    return CollectVramProcesses(generation);

                case ProcessMetric.Memory:
                default:
                    return CollectMemoryProcesses(generation);
            }
        }

        private ProcessCollectionResult CollectCpuProcesses(int generation)
        {
            ProcessIdentityCollectionResult identities = _processMemoryCollector.CollectApplicationIdentities(
                () => IsProcessGenerationCurrent(generation));
            if (identities.Aborted || !IsProcessGenerationCurrent(generation))
                return new ProcessCollectionResult();

            if (!identities.Available)
            {
                return new ProcessCollectionResult
                {
                    Available = false,
                    Status = identities.Status
                };
            }

            var cpuSamples = CollectCpuSamples(identities.Samples, generation);
            ProcessCpuUpdate cpuUpdate = _processCpuTracker.Update(cpuSamples, generation,
                Environment.ProcessorCount, Stopwatch.Frequency);
            if (!IsProcessGenerationCurrent(generation))
                return new ProcessCollectionResult();

            Dictionary<int, float> cpuByProcessId = cpuUpdate.Processes
                .Where(process => process != null && process.CpuPercent >= 0 &&
                    !float.IsNaN(process.CpuPercent) && !float.IsInfinity(process.CpuPercent))
                .GroupBy(process => process.ProcessId)
                .ToDictionary(group => group.Key, group => group.First().CpuPercent);

            foreach (ApplicationProcessSample process in identities.Samples)
            {
                if (cpuByProcessId.TryGetValue(process.ProcessId, out float cpuPercent))
                    process.CpuPercent = cpuPercent;
            }

            List<ProcessInfo> groups = _processMemoryCollector.GroupApplicationSamples(identities.Samples,
                ProcessMetric.Cpu, identities.VisibleOwnerlessWindowProcessIds);
            return new ProcessCollectionResult
            {
                Processes = groups.Take(5).ToList(),
                Available = cpuUpdate.Available && groups.Count > 0
            };
        }

        private List<ProcessCpuSample> CollectCpuSamples(IReadOnlyList<ApplicationProcessSample> identities,
            int generation)
        {
            var samples = new List<ProcessCpuSample>();
            if (identities == null)
                return samples;

            foreach (ApplicationProcessSample identity in identities)
            {
                if (!IsProcessGenerationCurrent(generation))
                    break;

                string name = Path.GetFileNameWithoutExtension(identity.ExecutableName ?? string.Empty);
                if (string.IsNullOrWhiteSpace(name) ||
                    identity.CreationTime == 0 || !identity.CpuTimeAvailable ||
                    identity.CpuMonotonicTimestamp <= 0 || identity.CpuTimeTicks < 0)
                    continue;

                samples.Add(new ProcessCpuSample
                {
                    ProcessId = identity.ProcessId,
                    Name = name,
                    StartTimeTicks = unchecked((long)identity.CreationTime),
                    TotalProcessorTimeTicks = identity.CpuTimeTicks,
                    MonotonicTimestamp = identity.CpuMonotonicTimestamp
                });
            }

            return samples;
        }

        private ProcessCollectionResult CollectMemoryProcesses(int generation)
        {
            ProcessMemoryCollectionResult collection = _processMemoryCollector.Collect(
                () => IsProcessGenerationCurrent(generation));
            return new ProcessCollectionResult
            {
                Processes = collection.Processes,
                Available = collection.Available,
                Status = collection.Status
            };
        }

        private ProcessCollectionResult CollectVramProcesses(int generation)
        {
            ProcessVramSample sample = _processVramMonitor.ReadUsageByProcess(generation);
            if (!sample.Available)
                return new ProcessCollectionResult { Available = false, Status = sample.Status };

            ProcessIdentityCollectionResult identities = _processMemoryCollector.CollectApplicationIdentities(
                () => IsProcessGenerationCurrent(generation));
            if (identities.Aborted || !IsProcessGenerationCurrent(generation))
                return new ProcessCollectionResult();
            if (!identities.Available)
                return new ProcessCollectionResult { Available = false, Status = identities.Status };

            foreach (ApplicationProcessSample process in identities.Samples)
            {
                if (sample.UsageByProcessId.TryGetValue(process.ProcessId, out ulong bytes))
                    process.VramBytes = bytes;
            }

            List<ProcessInfo> groups = _processMemoryCollector.GroupApplicationSamples(identities.Samples,
                ProcessMetric.Vram, identities.VisibleOwnerlessWindowProcessIds);
            string status = groups.Count == 0 ? ProcessVramMonitor.EmptyStatus : sample.Status;
            return new ProcessCollectionResult
            {
                Processes = groups.Take(5).ToList(),
                Available = true,
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
                        snapshot.MemoryProcessesAvailable = _memoryProcessesAvailable;
                        snapshot.MemoryProcessesStatus = _memoryProcessesStatus;
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
                _memoryProcessesAvailable = true;
                _memoryProcessesStatus = string.Empty;
                _vramProcessesAvailable = false;
                _vramProcessesStatus = ProcessVramMonitor.WaitingStatus;
            }
        }

        private static List<ProcessInfo> CloneProcessList(IEnumerable<ProcessInfo> processes)
        {
            return processes.Select(process => new ProcessInfo
            {
                ProcessId = process.ProcessId,
                ProcessCount = process.ProcessCount,
                MemoryIsPartial = process.MemoryIsPartial,
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
