using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysFloat.Monitoring
{
    internal sealed class ProcessMemoryCollectionResult
    {
        internal bool Available { get; set; }
        internal bool Aborted { get; set; }
        internal string Status { get; set; } = string.Empty;
        internal List<ProcessInfo> Processes { get; set; } = new List<ProcessInfo>();
        internal IReadOnlyList<ProcessMemoryProcessSample> Samples { get; set; } =
            Array.Empty<ProcessMemoryProcessSample>();
    }

    internal sealed class ProcessMemoryCollector
    {
        private const int ErrorNoMoreFiles = 18;
        private const uint ToolhelpSnapshotProcesses = 0x00000002;
        private const int MaximumSnapshotRetries = 3;

        private readonly ProcessMemoryReader _memoryReader = new ProcessMemoryReader();
        private readonly ApplicationMemoryGrouper _applicationMemoryGrouper = new ApplicationMemoryGrouper();

        internal ProcessMemoryCollectionResult Collect(Func<bool> shouldContinue = null)
        {
            try
            {
                return CollectCore(shouldContinue);
            }
            catch
            {
                return new ProcessMemoryCollectionResult
                {
                    Available = false,
                    Status = "进程私有工作集采集失败"
                };
            }
        }

        private ProcessMemoryCollectionResult CollectCore(Func<bool> shouldContinue)
        {
            if (!ShouldContinue(shouldContinue))
                return AbortedResult();

            if (!_memoryReader.TryProbe(out string supportStatus))
            {
                return new ProcessMemoryCollectionResult
                {
                    Available = false,
                    Status = supportStatus
                };
            }

            if (!ShouldContinue(shouldContinue))
                return AbortedResult();

            if (!TryCaptureProcessList(out List<ProcessSnapshotEntry> processes, out bool snapshotComplete,
                out string snapshotStatus))
            {
                return new ProcessMemoryCollectionResult
                {
                    Available = false,
                    Status = snapshotStatus
                };
            }

            if (!ShouldContinue(shouldContinue))
                return AbortedResult();

            _memoryReader.BeginCollection();
            var activeProcessIds = new HashSet<int>();
            var samples = new List<ProcessMemoryProcessSample>(processes.Count);

            foreach (ProcessSnapshotEntry process in processes)
            {
                if (!ShouldContinue(shouldContinue))
                    return AbortedResult();

                activeProcessIds.Add(process.ProcessId);
                ProcessMemoryReadResult reading = _memoryReader.Read(process.ProcessId);
                samples.Add(new ProcessMemoryProcessSample(
                    process.ProcessId,
                    process.ParentProcessId,
                    process.ExecutableName,
                    reading.ImagePath,
                    reading.CreationTime,
                    reading.PrivateWorkingSetAvailable,
                    reading.PrivateWorkingSetBytes));
            }

            if (!ShouldContinue(shouldContinue))
                return AbortedResult();

            _memoryReader.PruneImagePathCache(activeProcessIds);

            if (samples.Count == 0)
            {
                return new ProcessMemoryCollectionResult
                {
                    Available = true,
                    Status = "暂无应用进程",
                    Samples = samples.AsReadOnly()
                };
            }

            if (!samples.Any(sample => sample.PrivateWorkingSetAvailable))
            {
                return new ProcessMemoryCollectionResult
                {
                    Available = false,
                    Status = "无法读取任何进程私有工作集数据",
                    Samples = samples.AsReadOnly()
                };
            }

            HashSet<int> visibleWindowProcessIds;
            bool windowEnumerationSucceeded;
            try
            {
                visibleWindowProcessIds = ApplicationMemoryGrouper
                    .FindVisibleOwnerlessWindowProcessIds(out windowEnumerationSucceeded);
            }
            catch
            {
                visibleWindowProcessIds = new HashSet<int>();
                windowEnumerationSucceeded = false;
            }

            if (!ShouldContinue(shouldContinue))
                return AbortedResult();

            List<ProcessInfo> groups = _applicationMemoryGrouper.Group(samples, visibleWindowProcessIds);
            List<ProcessInfo> topProcesses = groups.Take(5).ToList();
            var statusParts = new List<string>();

            if (!snapshotComplete)
            {
                foreach (ProcessInfo process in topProcesses)
                    process.MemoryIsPartial = true;
                statusParts.Add(snapshotStatus);
            }

            if (!windowEnumerationSucceeded)
            {
                foreach (ProcessInfo process in topProcesses)
                    process.MemoryIsPartial = true;
                statusParts.Add("窗口枚举不可用，应用分组可能不完整");
            }

            if (samples.Any(sample => !sample.PrivateWorkingSetAvailable))
                statusParts.Add("部分进程无法读取，应用私有工作集合计可能偏低");
            else if (groups.Any(process => process.MemoryIsPartial))
                statusParts.Add("部分进程路径不可用，应用分组可能不完整");

            if (groups.Count == 0)
                statusParts.Add("暂无可汇总的应用进程");

            return new ProcessMemoryCollectionResult
            {
                Available = true,
                Status = string.Join("；", statusParts),
                Processes = topProcesses,
                Samples = samples.AsReadOnly()
            };
        }

        private static bool TryCaptureProcessList(out List<ProcessSnapshotEntry> processes,
            out bool complete, out string status)
        {
            processes = new List<ProcessSnapshotEntry>();
            complete = true;
            status = "";

            IntPtr snapshotValue = IntPtr.Zero;
            int lastError = 0;
            for (int attempt = 0; attempt < MaximumSnapshotRetries; attempt++)
            {
                snapshotValue = CreateToolhelp32Snapshot(ToolhelpSnapshotProcesses, 0);
                if (snapshotValue != IntPtr.Zero && snapshotValue != new IntPtr(-1))
                    break;

                lastError = Marshal.GetLastWin32Error();
                if (attempt + 1 < MaximumSnapshotRetries && lastError == 24) // ERROR_BAD_LENGTH
                    continue;
                status = "无法读取进程列表";
                return false;
            }

            using (var snapshot = new SafeSnapshotHandle(snapshotValue))
            {
                ProcessEntry32 entry = CreateProcessEntry();
                if (!Process32FirstW(snapshot, ref entry))
                {
                    lastError = Marshal.GetLastWin32Error();
                    if (lastError == ErrorNoMoreFiles)
                        return true;

                    status = "无法读取进程列表";
                    return false;
                }

                var seenProcessIds = new HashSet<int>();
                do
                {
                    int processId = entry.ProcessId <= int.MaxValue ? (int)entry.ProcessId : 0;
                    int parentProcessId = entry.ParentProcessId <= int.MaxValue
                        ? (int)entry.ParentProcessId
                        : 0;

                    if (processId > 0 && seenProcessIds.Add(processId))
                    {
                        processes.Add(new ProcessSnapshotEntry(
                            processId,
                            parentProcessId,
                            entry.ExecutableName ?? string.Empty));
                    }

                    entry = CreateProcessEntry();
                }
                while (Process32NextW(snapshot, ref entry));

                lastError = Marshal.GetLastWin32Error();
                if (lastError != ErrorNoMoreFiles)
                {
                    complete = false;
                    status = "进程列表读取不完整";
                }
            }

            return true;
        }

        private static ProcessEntry32 CreateProcessEntry()
        {
            return new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32))
            };
        }

        private static bool ShouldContinue(Func<bool> shouldContinue)
        {
            if (shouldContinue == null)
                return true;

            try
            {
                return shouldContinue();
            }
            catch
            {
                return false;
            }
        }

        private static ProcessMemoryCollectionResult AbortedResult()
        {
            return new ProcessMemoryCollectionResult { Aborted = true };
        }

        private sealed class ProcessSnapshotEntry
        {
            public ProcessSnapshotEntry(int processId, int parentProcessId, string executableName)
            {
                ProcessId = processId;
                ParentProcessId = parentProcessId;
                ExecutableName = executableName;
            }

            public int ProcessId { get; }
            public int ParentProcessId { get; }
            public string ExecutableName { get; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public UIntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableName;
        }

        private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeSnapshotHandle(IntPtr handle)
                : base(true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateToolhelp32Snapshot", SetLastError = true,
            ExactSpelling = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32FirstW(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32NextW(SafeSnapshotHandle snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
