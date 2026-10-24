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
        private readonly Dictionary<string, string> _applicationNameCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<string> _applicationNameCacheOrder = new Queue<string>();

        internal List<ProcessInfo> Group(IReadOnlyList<ProcessMemoryProcessSample> samples,
            IReadOnlyCollection<int> visibleOwnerlessWindowProcessIds)
        {
            var applicationSamples = new List<ApplicationProcessSample>(samples?.Count ?? 0);
            if (samples != null)
                foreach (var sample in samples)
                    if (sample != null) applicationSamples.Add(ToApplicationSample(sample));
            return GroupApplicationSamples(applicationSamples, ProcessMetric.Memory,
                visibleOwnerlessWindowProcessIds);
        }

        internal List<ProcessInfo> GroupApplicationSamples(IReadOnlyList<ApplicationProcessSample> samples,
            ProcessMetric metric, IReadOnlyCollection<int> visibleOwnerlessWindowProcessIds)
        {
            return ApplicationProcessGrouper.GroupSamples(samples, metric,
                visibleOwnerlessWindowProcessIds, ResolveApplicationName);
        }

        internal static List<ProcessInfo> GroupSamples(IReadOnlyList<ProcessMemoryProcessSample> samples,
            IReadOnlyCollection<int> visibleOwnerlessWindowProcessIds,
            Func<ProcessMemoryProcessSample, string> applicationNameResolver)
        {
            if (samples == null || samples.Count == 0)
                return new List<ProcessInfo>();

            var originalById = samples.Where(sample => sample != null && sample.ProcessId > 0)
                .GroupBy(sample => sample.ProcessId)
                .ToDictionary(group => group.Key, group => group.First());
            List<ApplicationProcessSample> applicationSamples = samples
                .Where(sample => sample != null)
                .Select(ToApplicationSample)
                .ToList();

            return ApplicationProcessGrouper.GroupSamples(applicationSamples, ProcessMetric.Memory,
                visibleOwnerlessWindowProcessIds, identity =>
                {
                    return identity != null && originalById.TryGetValue(identity.ProcessId,
                        out ProcessMemoryProcessSample original)
                        ? applicationNameResolver?.Invoke(original)
                        : null;
                });
        }

        private static ApplicationProcessSample ToApplicationSample(ProcessMemoryProcessSample sample)
        {
            if (sample == null)
                return null;

            return new ApplicationProcessSample
            {
                ProcessId = sample.ProcessId,
                ParentProcessId = sample.ParentProcessId,
                ExecutableName = sample.ExecutableName,
                ImagePath = sample.ImagePath,
                CreationTime = sample.CreationTime,
                MemoryBytes = sample.PrivateWorkingSetBytes,
                MemoryAvailable = sample.PrivateWorkingSetAvailable
            };
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

        private string ResolveApplicationName(ApplicationProcessSample process)
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
