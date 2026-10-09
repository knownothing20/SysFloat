using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SysFloat.Monitoring
{
    internal sealed class ProcessMemoryProcessSample
    {
        internal ProcessMemoryProcessSample(int processId, int parentProcessId, string executableName,
            string imagePath, ulong creationTime, bool privateWorkingSetAvailable, ulong privateWorkingSetBytes)
        {
            ProcessId = processId;
            ParentProcessId = parentProcessId;
            ExecutableName = executableName ?? string.Empty;
            ImagePath = imagePath;
            CreationTime = creationTime;
            PrivateWorkingSetAvailable = privateWorkingSetAvailable;
            PrivateWorkingSetBytes = privateWorkingSetBytes;
        }

        internal int ProcessId { get; }
        internal int ParentProcessId { get; }
        internal string ExecutableName { get; }
        internal string ImagePath { get; }
        internal ulong CreationTime { get; }
        internal bool PrivateWorkingSetAvailable { get; }
        internal ulong PrivateWorkingSetBytes { get; }
    }

    internal sealed class ApplicationMemoryGrouper
    {
        private const int MaximumCachedApplicationNames = 256;
        private const int GwOwner = 4;
        private static readonly string SystemExplorerPath = GetSystemExplorerPath();
        private readonly Dictionary<string, string> _applicationNameCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _applicationNameCacheOrder = new Queue<string>();

        internal List<ProcessInfo> Group(IReadOnlyList<ProcessMemoryProcessSample> samples,
            IReadOnlyCollection<int> visibleOwnerlessWindowProcessIds)
        {
            return GroupSamples(samples, visibleOwnerlessWindowProcessIds, ResolveApplicationName);
        }

        internal static List<ProcessInfo> GroupSamples(IReadOnlyList<ProcessMemoryProcessSample> samples,
            IReadOnlyCollection<int> visibleOwnerlessWindowProcessIds,
            Func<ProcessMemoryProcessSample, string> applicationNameResolver)
        {
            if (samples == null || samples.Count == 0)
                return new List<ProcessInfo>();

            var processById = new Dictionary<int, ProcessMemoryProcessSample>();
            foreach (ProcessMemoryProcessSample sample in samples)
            {
                if (sample != null && sample.ProcessId > 0 && !processById.ContainsKey(sample.ProcessId))
                    processById.Add(sample.ProcessId, sample);
            }

            var visibleIds = visibleOwnerlessWindowProcessIds == null
                ? new HashSet<int>()
                : new HashSet<int>(visibleOwnerlessWindowProcessIds);
            var groups = new Dictionary<string, GroupAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (ProcessMemoryProcessSample sample in processById.Values.OrderBy(item => item.ProcessId))
            {
                ProcessMemoryProcessSample applicationRoot = FindNearestVisibleApplicationRoot(
                    sample, processById, visibleIds);
                ProcessMemoryProcessSample identitySource = applicationRoot ?? sample;
                string normalizedPath = NormalizeExecutablePath(identitySource.ImagePath);
                string groupKey = normalizedPath != null
                    ? "path:" + normalizedPath
                    : "pid:" + identitySource.ProcessId + ":" + identitySource.CreationTime;

                if (!groups.TryGetValue(groupKey, out GroupAccumulator group))
                {
                    string name = applicationNameResolver?.Invoke(identitySource);
                    if (string.IsNullOrWhiteSpace(name))
                        name = GetExecutableBaseName(identitySource.ExecutableName, identitySource.ImagePath);

                    group = new GroupAccumulator(name, identitySource.ProcessId);
                    groups.Add(groupKey, group);
                }

                bool identityPartial = normalizedPath == null;
                group.Add(sample, identitySource.ProcessId, identityPartial);
            }

            return groups.Values
                .Where(group => group.HasReadableData)
                .Select(group => group.ToProcessInfo())
                .OrderByDescending(process => process.MemoryBytes)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(process => process.ProcessId)
                .ToList();
        }

        internal static HashSet<int> FindVisibleOwnerlessWindowProcessIds(out bool enumerationSucceeded)
        {
            var processIds = new HashSet<int>();
            EnumWindowsCallback callback = (window, state) =>
            {
                if (!IsWindowVisible(window) || GetWindow(window, GwOwner) != IntPtr.Zero)
                    return true;

                GetWindowThreadProcessId(window, out uint processId);
                if (processId > 0 && processId <= int.MaxValue)
                    processIds.Add((int)processId);
                return true;
            };

            enumerationSucceeded = EnumWindows(callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return processIds;
        }

        private string ResolveApplicationName(ProcessMemoryProcessSample process)
        {
            string normalizedPath = NormalizeExecutablePath(process.ImagePath);
            if (normalizedPath == null)
                return GetExecutableBaseName(process.ExecutableName, process.ImagePath);

            if (_applicationNameCache.TryGetValue(normalizedPath, out string cachedName))
                return cachedName;

            string name = null;
            try
            {
                FileVersionInfo version = FileVersionInfo.GetVersionInfo(process.ImagePath);
                if (!string.IsNullOrWhiteSpace(version.FileDescription))
                    name = version.FileDescription.Trim();
                else if (!string.IsNullOrWhiteSpace(version.ProductName))
                    name = version.ProductName.Trim();
            }
            catch
            {
                // The image may have exited or its file may no longer be readable.
            }

            if (string.IsNullOrWhiteSpace(name))
                name = GetExecutableBaseName(process.ExecutableName, process.ImagePath);

            while (_applicationNameCache.Count >= MaximumCachedApplicationNames && _applicationNameCacheOrder.Count > 0)
            {
                string oldestPath = _applicationNameCacheOrder.Dequeue();
                _applicationNameCache.Remove(oldestPath);
            }

            _applicationNameCache[normalizedPath] = name;
            _applicationNameCacheOrder.Enqueue(normalizedPath);
            return name;
        }

        private static ProcessMemoryProcessSample FindNearestVisibleApplicationRoot(
            ProcessMemoryProcessSample process,
            IReadOnlyDictionary<int, ProcessMemoryProcessSample> processById,
            ISet<int> visibleIds)
        {
            var visited = new HashSet<int>();
            ProcessMemoryProcessSample current = process;

            while (current != null && visited.Add(current.ProcessId))
            {
                if (IsExplorerShell(current))
                    break;

                if (visibleIds.Contains(current.ProcessId))
                    return current;

                if (current.ParentProcessId <= 0 || current.ParentProcessId == current.ProcessId ||
                    !processById.TryGetValue(current.ParentProcessId, out ProcessMemoryProcessSample parent))
                    break;

                // Toolhelp parent IDs can be recycled. Only follow an edge when the sampled
                // parent identity predates the child, as a real process parent must.
                if (current.CreationTime == 0 || parent.CreationTime == 0 ||
                    parent.CreationTime > current.CreationTime)
                    break;

                current = parent;
            }

            return null;
        }

        private static bool IsExplorerShell(ProcessMemoryProcessSample process)
        {
            if (string.IsNullOrWhiteSpace(process.ImagePath) || string.IsNullOrWhiteSpace(SystemExplorerPath))
                return false;

            string processPath = NormalizeExecutablePath(process.ImagePath);
            return processPath != null && string.Equals(processPath, SystemExplorerPath,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSystemExplorerPath()
        {
            try
            {
                string windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return string.IsNullOrWhiteSpace(windowsPath)
                    ? null
                    : NormalizeExecutablePath(Path.Combine(windowsPath, "explorer.exe"));
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeExecutablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                string fullPath = Path.GetFullPath(path.Trim());
                return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .ToUpperInvariant();
            }
            catch
            {
                return null;
            }
        }

        private static string GetExecutableBaseName(string executableName, string imagePath)
        {
            string source = !string.IsNullOrWhiteSpace(imagePath) ? imagePath : executableName;
            if (string.IsNullOrWhiteSpace(source))
                return string.Empty;

            try
            {
                return Path.GetFileNameWithoutExtension(source.Trim());
            }
            catch
            {
                return source.Trim();
            }
        }

        private sealed class GroupAccumulator
        {
            private ulong _memoryBytes;
            private int _processCount;
            private bool _isPartial;
            private bool _hasReadableData;
            private int _representativeProcessId;

            public GroupAccumulator(string name, int representativeProcessId)
            {
                Name = name ?? string.Empty;
                _representativeProcessId = representativeProcessId;
            }

            public string Name { get; }
            public bool HasReadableData => _hasReadableData;

            public void Add(ProcessMemoryProcessSample sample, int rootProcessId, bool identityPartial)
            {
                _processCount++;
                _representativeProcessId = Math.Min(_representativeProcessId, rootProcessId);
                _isPartial |= identityPartial;

                if (sample.PrivateWorkingSetAvailable)
                {
                    _hasReadableData = true;
                    _memoryBytes = ulong.MaxValue - _memoryBytes < sample.PrivateWorkingSetBytes
                        ? ulong.MaxValue
                        : _memoryBytes + sample.PrivateWorkingSetBytes;
                }
                else
                {
                    _isPartial = true;
                }
            }

            public ProcessInfo ToProcessInfo()
            {
                return new ProcessInfo
                {
                    ProcessId = _representativeProcessId,
                    ProcessCount = _processCount,
                    MemoryIsPartial = _isPartial,
                    Name = Name,
                    MemoryBytes = _memoryBytes
                };
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
