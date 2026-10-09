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
        private List<ProcessInfo> _topCpuProcesses = new List<ProcessInfo>();
        private List<ProcessInfo> _topMemoryProcesses = new List<ProcessInfo>();

        public event Action<MetricSnapshot> OnDataUpdated;

        public void Start()
        {
            lock (_lifecycleLock)
            {
                ThrowIfDisposed();
                if (_started)
                    return;

                _started = true;
                _timer = new Timer(OnTimerTick, null, _pollingIntervalMilliseconds, _pollingIntervalMilliseconds);
                if (_collectProcesses)
                    _processTimer = new Timer(OnProcessTimerTick, null, 0, 5000);
            }
        }

        public void ConfigurePolling(int intervalMilliseconds, bool collectProcesses)
        {
            int safeIntervalMilliseconds = intervalMilliseconds > 0 ? intervalMilliseconds : 1000;

            lock (_lifecycleLock)
            {
                ThrowIfDisposed();

                bool wasCollectingProcesses = _collectProcesses;
                _pollingIntervalMilliseconds = safeIntervalMilliseconds;
                _collectProcesses = collectProcesses;

                if (_started)
                {
                    _timer?.Change(_pollingIntervalMilliseconds, _pollingIntervalMilliseconds);

                    if (collectProcesses && !wasCollectingProcesses)
                        _processTimer = new Timer(OnProcessTimerTick, null, 0, 5000);
                    else if (!collectProcesses && _processTimer != null)
                    {
                        _processTimer.Dispose();
                        _processTimer = null;
                    }
                }

                if (!collectProcesses)
                    ClearProcessSnapshots();
            }
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                _started = false;
                _timer?.Dispose();
                _timer = null;
                _processTimer?.Dispose();
                _processTimer = null;
            }
        }

        private void OnProcessTimerTick(object state)
        {
            if (!_started || !_collectProcesses || _disposed ||
                Interlocked.Exchange(ref _processTickRunning, 1) != 0)
                return;

            try
            {
                if (!_started || !_collectProcesses || _disposed)
                    return;

                var processes = Process.GetProcesses();
                var memList = new List<ProcessInfo>();

                foreach (var p in processes)
                {
                    try
                    {
                        if (!_started || !_collectProcesses || _disposed)
                            continue;

                        if (FilteredProcesses.Contains(p.ProcessName)) continue;
                        var info = new ProcessInfo
                        {
                            Name = p.ProcessName,
                            MemoryBytes = (ulong)p.WorkingSet64
                        };
                        memList.Add(info);
                    }
                    catch { }
                    finally
                    {
                        p.Dispose();
                    }
                }

                var topMemoryProcesses = memList
                    .OrderByDescending(x => x.MemoryBytes)
                    .Take(7)
                    .ToList();

                lock (_processDataLock)
                {
                    if (!_started || !_collectProcesses || _disposed)
                        return;

                    _topMemoryProcesses = topMemoryProcesses;
                    _topCpuProcesses = CloneProcessList(topMemoryProcesses);
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _processTickRunning, 0);
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

                lock (_processDataLock)
                {
                    snapshot.TopCpuProcesses = CloneProcessList(_topCpuProcesses);
                    snapshot.TopMemoryProcesses = CloneProcessList(_topMemoryProcesses);
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
            }
        }

        private static List<ProcessInfo> CloneProcessList(IEnumerable<ProcessInfo> processes)
        {
            return processes.Select(process => new ProcessInfo
            {
                Name = process.Name,
                CpuPercent = process.CpuPercent,
                MemoryPercent = process.MemoryPercent,
                MemoryBytes = process.MemoryBytes
            }).ToList();
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
                _timer?.Dispose();
                _timer = null;
                _processTimer?.Dispose();
                _processTimer = null;
            }

            lock (_gpuLock)
            {
                _gpuMonitor.Dispose();
            }
        }
    }
}
