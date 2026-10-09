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

        public AlertEvaluation(bool isHighLoad, bool justActivated, string message, int activeMetricsMask = 0)
        {
            IsHighLoad = isHighLoad;
            JustActivated = justActivated;
            Message = message;
            ActiveMetricsMask = activeMetricsMask;
        }
    }

    // Each metric must stay above the threshold independently. Use monotonic time
    // so changing the system clock or sampling interval does not alter duration.
    public sealed class HighLoadAlertTracker
    {
        private readonly long[] _startedAt = { -1, -1, -1 };
        private bool _wasActive;

        public void Reset()
        {
            for (int i = 0; i < _startedAt.Length; i++) _startedAt[i] = -1;
            _wasActive = false;
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
                snapshot.VramAvailable ? snapshot.VramPercent : -1 };
            string[] names = { "CPU", "内存", "显存" };
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

            bool isActive = active.Count > 0;
            bool justActivated = isActive && !_wasActive;
            _wasActive = isActive;
            string message = isActive
                ? string.Join(" / ", active) + $"，持续至少 {settings.HighLoadDurationSeconds} 秒"
                : string.Empty;
            return new AlertEvaluation(isActive, justActivated, message, mask);
        }
    }
}
