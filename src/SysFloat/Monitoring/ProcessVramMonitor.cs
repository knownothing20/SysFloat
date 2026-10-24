using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SysFloat.Monitoring
{
    internal sealed class ProcessVramSample
    {
        public bool Available { get; set; }
        public string Status { get; set; }
        public Dictionary<int, ulong> UsageByProcessId { get; set; } = new Dictionary<int, ulong>();
    }

    internal sealed class ProcessVramMonitor : IDisposable
    {
        internal const string WaitingStatus = "等待显存数据";
        internal const string EmptyStatus = "暂无显存占用进程";

        private const uint ErrorSuccess = 0;
        private const uint PdhMoreData = 0x800007D2;
        private const uint PdhCStatusValidData = 0;
        private const uint PdhCStatusNewData = 1;
        private const uint PdhFmtLarge = 0x00000400;
        private const uint PdhFmtNoScale = 0x00001000;
        private const uint MaximumBufferBytes = 64 * 1024 * 1024;
        private const int MaximumArrayGrowthRetries = 3;
        private const string CounterPath = @"\GPU Process Memory(*)\Dedicated Usage";

        private readonly object _sync = new object();
        private IntPtr _query;
        private IntPtr _counter;
        private bool _disposed;
        private bool _active;
        private int _generation;

        public ProcessVramSample ReadUsageByProcess(int generation)
        {
            lock (_sync)
            {
                if (_disposed)
                    return Unavailable("显存进程计数器已关闭");
                if (!_active || generation != _generation)
                    return Unavailable(WaitingStatus);

                try
                {
                    if (!EnsureCounter())
                        return Unavailable("显存进程计数器不可用");

                    if (PdhCollectQueryData(_query) != ErrorSuccess)
                    {
                        CloseQuery();
                        return Unavailable("显存进程计数器读取失败");
                    }

                    return ReadCounterArray();
                }
                catch (DllNotFoundException)
                {
                    CloseQuery();
                    return Unavailable("系统缺少显存进程计数器支持");
                }
                catch (EntryPointNotFoundException)
                {
                    CloseQuery();
                    return Unavailable("系统缺少显存进程计数器支持");
                }
                catch (BadImageFormatException)
                {
                    CloseQuery();
                    return Unavailable("显存进程计数器架构不兼容");
                }
                catch
                {
                    CloseQuery();
                    return Unavailable("显存进程计数器读取失败");
                }
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                CloseQuery();
                _active = false;
            }
        }

        public void SetGeneration(int generation, bool active)
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                if (_generation != generation || _active != active || !active)
                    CloseQuery();

                _generation = generation;
                _active = active;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                CloseQuery();
                _disposed = true;
            }
        }

        internal static bool TryParseProcessId(string instanceName, out int processId)
        {
            processId = 0;
            if (string.IsNullOrEmpty(instanceName))
                return false;

            int searchFrom = 0;
            while (searchFrom < instanceName.Length)
            {
                int marker = instanceName.IndexOf("pid_", searchFrom, StringComparison.OrdinalIgnoreCase);
                if (marker < 0)
                    return false;

                int digitStart = marker + 4;
                int digitEnd = digitStart;
                while (digitEnd < instanceName.Length && instanceName[digitEnd] >= '0' && instanceName[digitEnd] <= '9')
                    digitEnd++;

                if (digitEnd > digitStart &&
                    (digitEnd == instanceName.Length || instanceName[digitEnd] == '_') &&
                    int.TryParse(instanceName.Substring(digitStart, digitEnd - digitStart), NumberStyles.None,
                        CultureInfo.InvariantCulture, out processId) && processId > 0)
                    return true;

                searchFrom = marker + 4;
            }

            processId = 0;
            return false;
        }

        private bool EnsureCounter()
        {
            if (_query != IntPtr.Zero && _counter != IntPtr.Zero)
                return true;

            CloseQuery();
            if (PdhOpenQueryW(IntPtr.Zero, UIntPtr.Zero, out _query) != ErrorSuccess)
            {
                CloseQuery();
                return false;
            }

            if (PdhAddEnglishCounterW(_query, CounterPath, UIntPtr.Zero, out _counter) != ErrorSuccess)
            {
                CloseQuery();
                return false;
            }

            return true;
        }

        private ProcessVramSample ReadCounterArray()
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                for (int attempt = 0; attempt < MaximumArrayGrowthRetries; attempt++)
                {
                    uint bufferBytes = 0;
                    uint itemCount;
                    uint status = PdhGetFormattedCounterArrayW(_counter, PdhFmtLarge | PdhFmtNoScale,
                        ref bufferBytes, out itemCount, IntPtr.Zero);

                    if (status == ErrorSuccess && bufferBytes == 0)
                        return AvailableEmpty();
                    if (status != PdhMoreData)
                    {
                        CloseQuery();
                        return Unavailable("显存进程数据暂不可用");
                    }

                    if (bufferBytes == 0 || bufferBytes > MaximumBufferBytes)
                    {
                        CloseQuery();
                        return Unavailable("显存进程数据大小异常");
                    }

                    if (buffer != IntPtr.Zero)
                        Marshal.FreeHGlobal(buffer);
                    buffer = Marshal.AllocHGlobal((int)bufferBytes);

                    uint actualBytes = bufferBytes;
                    status = PdhGetFormattedCounterArrayW(_counter, PdhFmtLarge | PdhFmtNoScale,
                        ref actualBytes, out itemCount, buffer);
                    if (status == ErrorSuccess)
                        return ParseCounterItems(buffer, itemCount, actualBytes);
                    if (status != PdhMoreData)
                    {
                        CloseQuery();
                        return Unavailable("显存进程数据读取失败");
                    }
                }

                CloseQuery();
                return Unavailable("显存进程列表变化过快");
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
            }
        }

        private static ProcessVramSample ParseCounterItems(IntPtr buffer, uint itemCount, uint bufferBytes)
        {
            if (itemCount == 0)
                return AvailableEmpty();

            var usageByProcessId = new Dictionary<int, ulong>();
            var seenInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int validCounterCount = 0;
            int recognizedProcessCount = 0;
            int itemSize = Marshal.SizeOf(typeof(PdhFormattedCounterValueItem));
            if ((ulong)itemCount * (uint)itemSize > bufferBytes)
                return Unavailable("显存进程数据格式异常");

            for (uint index = 0; index < itemCount; index++)
            {
                IntPtr itemAddress = IntPtr.Add(buffer, checked((int)index * itemSize));
                var item = (PdhFormattedCounterValueItem)Marshal.PtrToStructure(itemAddress,
                    typeof(PdhFormattedCounterValueItem));
                string instanceName = item.InstanceName == IntPtr.Zero
                    ? null
                    : Marshal.PtrToStringUni(item.InstanceName);

                if (!string.IsNullOrEmpty(instanceName) && !seenInstances.Add(instanceName))
                    continue;

                if (item.Value.Status != PdhCStatusValidData && item.Value.Status != PdhCStatusNewData)
                    continue;

                validCounterCount++;
                if (!TryParseProcessId(instanceName, out int processId))
                    continue;

                recognizedProcessCount++;
                long signedBytes = item.Value.LargeValue;
                if (signedBytes <= 0)
                    continue;

                ulong bytes = (ulong)signedBytes;
                usageByProcessId.TryGetValue(processId, out ulong previousBytes);
                usageByProcessId[processId] = ulong.MaxValue - previousBytes < bytes
                    ? ulong.MaxValue
                    : previousBytes + bytes;
            }

            if (validCounterCount == 0)
                return Unavailable("显存进程数据暂不可用");
            if (recognizedProcessCount == 0)
                return Unavailable("显存进程实例格式不兼容");

            return new ProcessVramSample
            {
                Available = true,
                Status = usageByProcessId.Count == 0 ? EmptyStatus : string.Empty,
                UsageByProcessId = usageByProcessId
            };
        }

        private static ProcessVramSample AvailableEmpty()
        {
            return new ProcessVramSample
            {
                Available = true,
                Status = EmptyStatus
            };
        }

        private static ProcessVramSample Unavailable(string status)
        {
            return new ProcessVramSample
            {
                Available = false,
                Status = status
            };
        }

        private void CloseQuery()
        {
            if (_query != IntPtr.Zero)
            {
                PdhCloseQuery(_query);
                _query = IntPtr.Zero;
            }

            _counter = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFormattedCounterValueItem
        {
            public IntPtr InstanceName;
            public PdhFormattedCounterValue Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PdhFormattedCounterValue
        {
            public uint Status;
            public long LargeValue;
        }

        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", ExactSpelling = true)]
        private static extern uint PdhOpenQueryW(IntPtr dataSource, UIntPtr userData, out IntPtr query);

        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string counterPath, UIntPtr userData,
            out IntPtr counter);

        [DllImport("pdh.dll", EntryPoint = "PdhCollectQueryData", ExactSpelling = true)]
        private static extern uint PdhCollectQueryData(IntPtr query);

        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", ExactSpelling = true)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferBytes,
            out uint itemCount, IntPtr itemBuffer);

        [DllImport("pdh.dll", EntryPoint = "PdhCloseQuery", ExactSpelling = true)]
        private static extern uint PdhCloseQuery(IntPtr query);
    }
}
