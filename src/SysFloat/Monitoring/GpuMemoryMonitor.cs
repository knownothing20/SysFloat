using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace SysFloat.Monitoring
{
    public class GpuMemoryMonitor : IDisposable
    {
        private Computer _computer;
        private GpuDevice[] _devices = Array.Empty<GpuDevice>();
        private bool _disposed;

        public GpuMemoryMonitor()
        {
            try
            {
                _computer = new Computer { IsGpuEnabled = true };
                _computer.Open();
                // Cache sensor handles and stable order, rather than sorting by changing usage.
                _devices = _computer.Hardware
                    .Where(gpu => gpu.HardwareType == HardwareType.GpuNvidia ||
                        gpu.HardwareType == HardwareType.GpuAmd || gpu.HardwareType == HardwareType.GpuIntel)
                    .OrderBy(gpu => AdapterOrder(gpu.HardwareType)).ThenBy(gpu => gpu.Identifier.ToString(), StringComparer.Ordinal)
                    .Select(gpu => new GpuDevice(gpu)).ToArray();
            }
            catch
            {
                try { _computer?.Close(); } catch { }
                _computer = null;
            }
        }

        public IReadOnlyList<GpuMemoryInfo> GetGpuMemoryInfo()
        {
            if (_disposed || _computer == null) return Array.Empty<GpuMemoryInfo>();
            var readings = new GpuMemoryInfo[_devices.Length];
            for (int i = 0; i < _devices.Length; i++)
            {
                GpuDevice device = _devices[i];
                var reading = new GpuMemoryInfo { Id = device.Hardware.Identifier.ToString(), Name = device.Hardware.Name };
                readings[i] = reading;
                try
                {
                    device.Hardware.Update();
                    if (device.Used?.Value is float used && TryConvertMegabytesToBytes(used, out ulong usedBytes))
                    {
                        reading.UsedBytes = usedBytes;
                        reading.Available = true;
                        if (device.Total?.Value is float total && total > 0 &&
                            TryConvertMegabytesToBytes(total, out ulong totalBytes))
                        {
                            reading.TotalBytes = totalBytes;
                            reading.Percent = CalculatePercent(usedBytes, totalBytes);
                        }
                    }
                    if (device.Shared?.Value is float shared && TryConvertMegabytesToBytes(shared, out ulong sharedBytes))
                    {
                        reading.SharedAvailable = true;
                        reading.SharedUsedBytes = sharedBytes;
                    }
                }
                catch { /* Keep the unavailable card's slot; still update other cards. */ }
            }
            return readings;
        }

        public (float percent, ulong used, ulong total, bool available) GetVramInfo()
        {
            var cards = GetGpuMemoryInfo();
            if (cards.Count == 0) return (-1, 0, 0, false);
            var first = cards[0];
            return (first.Percent, first.UsedBytes, first.TotalBytes, first.Available);
        }

        internal static float CalculatePercent(ulong used, ulong total) =>
            total == 0 || used > total ? -1 : (float)(used / (double)total * 100d);

        internal static int AdapterOrder(HardwareType type) =>
            type == HardwareType.GpuNvidia ? 0 : type == HardwareType.GpuAmd ? 1 : 2;

        private static bool TryConvertMegabytesToBytes(float megabytes, out ulong bytes)
        {
            bytes = 0;
            if (float.IsNaN(megabytes) || float.IsInfinity(megabytes) || megabytes < 0) return false;
            double value = (double)megabytes * 1024d * 1024d;
            if (double.IsNaN(value) || double.IsInfinity(value) || value >= ulong.MaxValue) return false;
            bytes = (ulong)Math.Round(value);
            return true;
        }

        private sealed class GpuDevice
        {
            internal readonly IHardware Hardware;
            internal readonly ISensor Used;
            internal readonly ISensor Total;
            internal readonly ISensor Shared;
            internal GpuDevice(IHardware hardware)
            {
                Hardware = hardware;
                Used = Find(hardware, "GPU Memory Used") ?? Find(hardware, "D3D Dedicated Memory Used");
                Total = Find(hardware, "GPU Memory Total") ?? Find(hardware, "D3D Dedicated Memory Total");
                Shared = Find(hardware, "D3D Shared Memory Used");
            }
            private static ISensor Find(IHardware hardware, string name) => hardware.Sensors.FirstOrDefault(
                sensor => sensor.SensorType == SensorType.SmallData && string.Equals(sensor.Name, name, StringComparison.Ordinal));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _computer?.Close();
            _devices = Array.Empty<GpuDevice>();
        }
    }
}
