using System;
using System.Collections.Generic;
using SysFloat.Configuration;

namespace SysFloat.Monitoring
{
    public sealed class AlertEvaluation
    {
        public bool IsHighLoad { get; }
        public bool JustActivated { get; }
        public string Message { get; }
        public int ActiveMetricsMask { get; }
        public IReadOnlyCollection<string> ActiveGpuIds { get; }

        public AlertEvaluation(bool isHighLoad, bool justActivated, string message, int activeMetricsMask = 0,
            IReadOnlyCollection<string> activeGpuIds = null)
        {
            IsHighLoad = isHighLoad;
            JustActivated = justActivated;
            Message = message;
            ActiveMetricsMask = activeMetricsMask;
            ActiveGpuIds = activeGpuIds ?? Array.Empty<string>();
        }
    }

    // Each metric must stay above the threshold independently. Use monotonic time
    // so changing the system clock or sampling interval does not alter duration.
    public sealed class HighLoadAlertTracker
    {
        private readonly long[] _startedAt = { -1, -1, -1 };
        private bool _wasActive;
        private readonly Dictionary<string, long> _gpuStartedAt = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly HashSet<string> _previousActiveGpus = new HashSet<string>(StringComparer.Ordinal);

        public void Reset()
        {
            for (int i = 0; i < _startedAt.Length; i++) _startedAt[i] = -1;
            _wasActive = false;
            _gpuStartedAt.Clear();
            _previousActiveGpus.Clear();
        }

        public AlertEvaluation Evaluate(MetricSnapshot snapshot, AppSettings settings,
            long timestampMilliseconds)
        {
            if (!settings.HighLoadAlertsEnabled)
            {
                Reset();
                return new AlertEvaluation(false, false, string.Empty);
            }

            float[] values = { snapshot.CpuPercent, snapshot.MemoryPercent,
                snapshot.Gpus?.Count > 0 ? -1 : snapshot.VramAvailable ? snapshot.VramPercent : -1 };
            string[] names = { "CPU", UIStrings.Get("alert.metric.ram"), UIStrings.Get("alert.metric.vram") };
            var active = new List<string>();
            int mask = 0;
            long duration = settings.HighLoadDurationSeconds * 1000L;

            for (int i = 0; i < values.Length; i++)
            {
                float value = values[i];
                if (float.IsNaN(value) || float.IsInfinity(value) ||
                    value < settings.HighLoadThresholdPercent || value > 100)
                {
                    _startedAt[i] = -1;
                    continue;
                }

                if (_startedAt[i] < 0 || timestampMilliseconds < _startedAt[i])
                    _startedAt[i] = timestampMilliseconds;
                if (timestampMilliseconds - _startedAt[i] >= duration)
                {
                    active.Add($"{names[i]} {Math.Round(value):0}%");
                    mask |= 1 << i;
                }
            }

            var seenGpus = new HashSet<string>(StringComparer.Ordinal);
            var activeGpuIds = new HashSet<string>(StringComparer.Ordinal);
            if (snapshot.Gpus != null)
            {
                foreach (var gpu in snapshot.Gpus)
                {
                    if (gpu == null || string.IsNullOrEmpty(gpu.Id) || !seenGpus.Add(gpu.Id)) continue;
                    if (!gpu.Available || float.IsNaN(gpu.Percent) || float.IsInfinity(gpu.Percent) ||
                        gpu.Percent < settings.HighLoadThresholdPercent || gpu.Percent > 100)
                    {
                        _gpuStartedAt.Remove(gpu.Id);
                        continue;
                    }
                    if (!_gpuStartedAt.TryGetValue(gpu.Id, out long started) || timestampMilliseconds < started)
                        _gpuStartedAt[gpu.Id] = started = timestampMilliseconds;
                    if (timestampMilliseconds - started >= duration)
                    {
                        activeGpuIds.Add(gpu.Id);
                        active.Add(UIStrings.Format("alert.gpu", gpu.Name, gpu.Percent));
                        mask |= 4;
                    }
                }
            }
            foreach (var id in new List<string>(_gpuStartedAt.Keys))
                if (!seenGpus.Contains(id)) _gpuStartedAt.Remove(id);
            bool newGpuAlert = false;
            foreach (string id in activeGpuIds)
                if (!_previousActiveGpus.Contains(id)) newGpuAlert = true;
            bool isActive = active.Count > 0;
            bool justActivated = isActive && (!_wasActive || newGpuAlert);
            _previousActiveGpus.Clear();
            _previousActiveGpus.UnionWith(activeGpuIds);
            _wasActive = isActive;
            string message = isActive
                ? string.Join(" / ", active) + UIStrings.Format("alert.suffix", settings.HighLoadDurationSeconds)
                : string.Empty;
            return new AlertEvaluation(isActive, justActivated, message, mask, activeGpuIds);
        }
    }
}
