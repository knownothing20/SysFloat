using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace SysFloat.Monitoring
{
    public class GpuMemoryMonitor : IDisposable
    {
        private Computer _computer;
        private bool _disposed;

        public GpuMemoryMonitor()
        {
            try
            {
                _computer = new Computer
                {
                    IsGpuEnabled = true
                };
                _computer.Open();
            }
            catch
            {
                _computer = null;
            }
        }

        public (float percent, ulong used, ulong total, bool available) GetVramInfo()
        {
            if (_computer == null)
                return (-1, 0, 0, false);

            try
            {
                foreach (var gpu in _computer.Hardware)
                {
                    if (gpu.HardwareType != HardwareType.GpuNvidia &&
                        gpu.HardwareType != HardwareType.GpuAmd &&
                        gpu.HardwareType != HardwareType.GpuIntel)
                        continue;

                    gpu.Update();

                    float? vramUsed = null;
                    float? vramTotal = null;

                    foreach (var sensor in gpu.Sensors)
                    {
                        if (sensor.Name.Contains("GPU Memory Used") && sensor.SensorType == SensorType.SmallData)
                            vramUsed = sensor.Value;
                        else if (sensor.Name.Contains("GPU Memory Total") && sensor.SensorType == SensorType.SmallData)
                            vramTotal = sensor.Value;
                        else if (sensor.Name.Contains("GPU Memory") && sensor.Name.Contains("Used") && sensor.SensorType == SensorType.SmallData)
                            vramUsed = sensor.Value;
                        else if (sensor.Name.Contains("GPU Memory") && sensor.Name.Contains("Total") && sensor.SensorType == SensorType.SmallData)
                            vramTotal = sensor.Value;
                    }

                    if (vramUsed.HasValue && vramTotal.HasValue && vramTotal.Value > 0)
                    {
                        if (!TryConvertMegabytesToBytes(vramUsed.Value, out ulong usedBytes) ||
                            !TryConvertMegabytesToBytes(vramTotal.Value, out ulong totalBytes))
                            continue;

                        float percent = (float)(vramUsed.Value / vramTotal.Value * 100.0);
                        return (percent, usedBytes, totalBytes, true);
                    }
                    else if (vramUsed.HasValue)
                    {
                        if (!TryConvertMegabytesToBytes(vramUsed.Value, out ulong usedBytes))
                            continue;

                        return (-1, usedBytes, 0, true);
                    }
                }
            }
            catch
            {
            }

            return (-1, 0, 0, false);
        }

        private static bool TryConvertMegabytesToBytes(float megabytes, out ulong bytes)
        {
            bytes = 0;
            if (float.IsNaN(megabytes) || float.IsInfinity(megabytes) || megabytes < 0)
                return false;

            double value = (double)megabytes * 1024d * 1024d;
            if (double.IsNaN(value) || double.IsInfinity(value) || value >= ulong.MaxValue)
                return false;

            bytes = (ulong)Math.Round(value);
            return true;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _computer?.Close();
                _disposed = true;
            }
        }
    }
}
