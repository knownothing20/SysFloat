using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SysFloat.Monitoring
{
    internal sealed class ProcessMemoryReadResult
    {
        public int ProcessId { get; set; }
        public ulong CreationTime { get; set; }
        public string ImagePath { get; set; }
        public bool PrivateWorkingSetAvailable { get; set; }
        public ulong PrivateWorkingSetBytes { get; set; }
    }

    internal sealed class ProcessMemoryReader
    {
        internal const string UnsupportedStatus = "当前 Windows 版本不支持进程私有工作集读取";

        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmRead = 0x0010;
        private const int ErrorInvalidParameter = 87;
        private const int ErrorInvalidData = 13;
        private const int ErrorNotSupported = 50;
        private const int ErrorCallNotImplemented = 120;
        private const int ErrorProcNotFound = 127;
        private const int ErrorInsufficientBuffer = 122;
        private const int MaximumCachedProcessPaths = 4096;
        private const int InitialImagePathBufferSize = 1024;
        private const int MaximumImagePathBufferSize = 32768;

        private readonly Dictionary<int, CachedProcessPath> _imagePathCache =
            new Dictionary<int, CachedProcessPath>();
        private long _collectionSequence;
        private MemoryCounterSupport _support;

        internal void BeginCollection()
        {
            _collectionSequence++;
        }

        internal bool TryProbe(out string status)
        {
            if (_support == MemoryCounterSupport.Supported)
            {
                status = "";
                return true;
            }

            if (_support == MemoryCounterSupport.Unsupported)
            {
                status = UnsupportedStatus;
                return false;
            }

            try
            {
                using (SafeProcessHandle process = OpenProcessForQuery(GetCurrentProcessId(), out _, out _))
                {
                    if (process == null || process.IsInvalid)
                    {
                        status = "无法打开进程私有工作集计数器";
                        return false;
                    }

                    if (!TryGetCounters(process, out _, out int error))
                    {
                        if (IsUnsupportedError(error))
                        {
                            _support = MemoryCounterSupport.Unsupported;
                            status = UnsupportedStatus;
                        }
                        else
                        {
                            status = error == ErrorInvalidData
                                ? "进程私有工作集计数器数据异常"
                                : "无法读取进程私有工作集计数器";
                        }

                        return false;
                    }
                }

                _support = MemoryCounterSupport.Supported;
                status = "";
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                _support = MemoryCounterSupport.Unsupported;
                status = UnsupportedStatus;
                return false;
            }
            catch (DllNotFoundException)
            {
                _support = MemoryCounterSupport.Unsupported;
                status = UnsupportedStatus;
                return false;
            }
            catch
            {
                status = "无法读取进程私有工作集计数器";
                return false;
            }
        }

        internal ProcessMemoryReadResult Read(int processId)
        {
            var result = new ProcessMemoryReadResult { ProcessId = processId };
            if (processId <= 0)
                return result;

            try
            {
                using (SafeProcessHandle process = OpenProcessForQuery(unchecked((uint)processId),
                    out bool usedFallbackAccess, out _))
                {
                    if (process == null || process.IsInvalid)
                        return result;

                    if (TryGetCreationTime(process, out ulong creationTime))
                        result.CreationTime = creationTime;

                    result.ImagePath = ResolveImagePath(process, processId, result.CreationTime);

                    if (TryGetCounters(process, out ProcessMemoryCountersEx2 counters, out int error))
                    {
                        result.PrivateWorkingSetBytes = counters.PrivateWorkingSetSize.ToUInt64();
                        result.PrivateWorkingSetAvailable = true;
                        return result;
                    }

                    if (!usedFallbackAccess && !IsUnsupportedError(error))
                    {
                        using (SafeProcessHandle fallback = OpenProcess(
                            ProcessQueryInformation | ProcessVmRead, false, unchecked((uint)processId)))
                        {
                            if (fallback != null && !fallback.IsInvalid &&
                                TryGetCounters(fallback, out counters, out _))
                            {
                                result.PrivateWorkingSetBytes = counters.PrivateWorkingSetSize.ToUInt64();
                                result.PrivateWorkingSetAvailable = true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // A process can exit or become protected between enumeration and sampling.
            }

            return result;
        }

        internal void PruneImagePathCache(ISet<int> activeProcessIds)
        {
            var removed = new List<int>();
            foreach (var entry in _imagePathCache)
            {
                if (activeProcessIds == null || !activeProcessIds.Contains(entry.Key))
                    removed.Add(entry.Key);
            }

            foreach (int processId in removed)
                _imagePathCache.Remove(processId);

            if (_imagePathCache.Count <= MaximumCachedProcessPaths)
                return;

            int excess = _imagePathCache.Count - MaximumCachedProcessPaths;
            foreach (int processId in _imagePathCache
                .OrderBy(entry => entry.Value.LastSeenSequence)
                .Take(excess)
                .Select(entry => entry.Key)
                .ToArray())
            {
                _imagePathCache.Remove(processId);
            }
        }

        private string ResolveImagePath(SafeProcessHandle process, int processId, ulong creationTime)
        {
            if (creationTime != 0 && _imagePathCache.TryGetValue(processId, out CachedProcessPath cached) &&
                cached.CreationTime == creationTime)
            {
                cached.LastSeenSequence = _collectionSequence;
                return cached.ImagePath;
            }

            string imagePath = TryGetImagePath(process);
            if (creationTime != 0 && !string.IsNullOrWhiteSpace(imagePath))
            {
                _imagePathCache[processId] = new CachedProcessPath
                {
                    CreationTime = creationTime,
                    ImagePath = imagePath,
                    LastSeenSequence = _collectionSequence
                };
            }
            else if (_imagePathCache.ContainsKey(processId))
            {
                _imagePathCache.Remove(processId);
            }

            return imagePath;
        }

        private static SafeProcessHandle OpenProcessForQuery(uint processId, out bool usedFallbackAccess,
            out int lastError)
        {
            usedFallbackAccess = false;
            SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process != null && !process.IsInvalid)
            {
                lastError = 0;
                return process;
            }

            lastError = Marshal.GetLastWin32Error();
            process?.Dispose();

            usedFallbackAccess = true;
            process = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
            if (process != null && !process.IsInvalid)
            {
                lastError = 0;
                return process;
            }

            lastError = Marshal.GetLastWin32Error();
            return process;
        }

        private static bool TryGetCounters(SafeProcessHandle process, out ProcessMemoryCountersEx2 counters,
            out int lastError)
        {
            counters = new ProcessMemoryCountersEx2
            {
                Size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx2)),
                PrivateWorkingSetSize = UninitializedSizeValue
            };

            if (!GetProcessMemoryInfo(process, ref counters, counters.Size))
            {
                lastError = Marshal.GetLastWin32Error();
                return false;
            }

            ulong privateWorkingSetBytes = counters.PrivateWorkingSetSize.ToUInt64();
            ulong workingSetBytes = counters.WorkingSetSize.ToUInt64();
            if (privateWorkingSetBytes == UninitializedSizeValue.ToUInt64())
            {
                // Older systems may accept the larger cb while only filling the EX fields.
                lastError = ErrorInvalidParameter;
                return false;
            }

            if (privateWorkingSetBytes > workingSetBytes)
            {
                lastError = ErrorInvalidData;
                return false;
            }

            lastError = 0;
            return true;
        }

        private static bool TryGetCreationTime(SafeProcessHandle process, out ulong creationTime)
        {
            creationTime = 0;
            if (!GetProcessTimes(process, out FileTime created, out _, out _, out _))
                return false;

            creationTime = ((ulong)created.HighDateTime << 32) | created.LowDateTime;
            return creationTime != 0;
        }

        private static string TryGetImagePath(SafeProcessHandle process)
        {
            int capacity = InitialImagePathBufferSize;
            while (capacity <= MaximumImagePathBufferSize)
            {
                var path = new StringBuilder(capacity);
                uint size = (uint)path.Capacity;
                if (QueryFullProcessImageNameW(process, 0, path, ref size))
                    return size == 0 ? null : path.ToString();

                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer ||
                    capacity == MaximumImagePathBufferSize)
                    return null;

                capacity = Math.Min(capacity * 2, MaximumImagePathBufferSize);
            }

            return null;
        }

        private static bool IsUnsupportedError(int error)
        {
            return error == ErrorInvalidParameter || error == ErrorNotSupported ||
                error == ErrorCallNotImplemented || error == ErrorProcNotFound;
        }

        private static UIntPtr UninitializedSizeValue
        {
            get
            {
                return UIntPtr.Size == 8
                    ? new UIntPtr(ulong.MaxValue)
                    : new UIntPtr(uint.MaxValue);
            }
        }

        private sealed class CachedProcessPath
        {
            public ulong CreationTime { get; set; }
            public string ImagePath { get; set; }
            public long LastSeenSequence { get; set; }
        }

        private enum MemoryCounterSupport
        {
            Unknown,
            Supported,
            Unsupported
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx2
        {
            public uint Size;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
            public UIntPtr PrivateWorkingSetSize;
            public ulong SharedCommitUsage;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime creationTime,
            out FileTime exitTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode,
            SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags,
            StringBuilder imageFileName, ref uint size);

        [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(SafeProcessHandle process,
            ref ProcessMemoryCountersEx2 counters, uint size);
    }
}
