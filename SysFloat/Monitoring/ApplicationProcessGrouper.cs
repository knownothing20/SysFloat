using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SysFloat.Monitoring
{
    internal sealed class ApplicationProcessSample
    {
        public int ProcessId { get; set; }
        public int ParentProcessId { get; set; }
        public string ExecutableName { get; set; } = string.Empty;
        public string ImagePath { get; set; }
        public ulong CreationTime { get; set; }
        public float CpuPercent { get; set; } = -1;
        public long CpuTimeTicks { get; set; }
        public long CpuMonotonicTimestamp { get; set; }
        public bool CpuTimeAvailable { get; set; }
        public ulong MemoryBytes { get; set; }
        public bool MemoryAvailable { get; set; }
        public ulong VramBytes { get; set; }
    }

    internal static class ApplicationProcessGrouper
    {
        private static readonly string SystemExplorerPath = GetSystemExplorerPath();
        private static readonly string SystemWindowsPath = GetSystemWindowsPath();

        internal static List<ProcessInfo> GroupSamples(IReadOnlyList<ApplicationProcessSample> samples,
            ProcessMetric metric, IReadOnlyCollection<int> visibleOwnerlessWindowProcessIds,
            Func<ApplicationProcessSample, string> applicationNameResolver)
        {
            if (samples == null || samples.Count == 0 || !Enum.IsDefined(typeof(ProcessMetric), metric))
                return new List<ProcessInfo>();

            var processById = new Dictionary<int, ApplicationProcessSample>();
            foreach (ApplicationProcessSample sample in samples)
            {
                if (sample != null && sample.ProcessId > 0 && !processById.ContainsKey(sample.ProcessId))
                    processById.Add(sample.ProcessId, sample);
            }

            var visibleIds = visibleOwnerlessWindowProcessIds == null
                ? new HashSet<int>()
                : new HashSet<int>(visibleOwnerlessWindowProcessIds);
            var groups = new Dictionary<string, GroupAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (ApplicationProcessSample sample in processById.Values.OrderBy(item => item.ProcessId))
            {
                // Keep the complete identity graph for ancestry, but only resolve and name
                // groups that have a value for the selected metric.
                if ((metric == ProcessMetric.Cpu && (!IsFinite(sample.CpuPercent) || sample.CpuPercent < 0)) ||
                    (metric == ProcessMetric.Vram && sample.VramBytes == 0))
                    continue;

                ApplicationProcessSample identitySource = FindVisibleApplicationRoot(sample, processById, visibleIds)
                    ?? FindSafeBackgroundRoot(sample, processById)
                    ?? sample;
                string normalizedPath = NormalizeExecutablePath(identitySource.ImagePath);
                string groupKey = normalizedPath != null
                    ? "path:" + normalizedPath
                    : "pid:" + identitySource.ProcessId + ":" + identitySource.CreationTime;

                if (!groups.TryGetValue(groupKey, out GroupAccumulator group))
                {
                    string name = applicationNameResolver?.Invoke(identitySource);
                    if (string.IsNullOrWhiteSpace(name))
                        name = GetExecutableBaseName(identitySource.ExecutableName, identitySource.ImagePath);

                    group = new GroupAccumulator(name, identitySource.ProcessId, metric);
                    groups.Add(groupKey, group);
                }

                group.Add(sample, identitySource.ProcessId, normalizedPath == null);
            }

            IEnumerable<ProcessInfo> results = groups.Values
                .Where(group => group.HasReadableData)
                .Select(group => group.ToProcessInfo());

            switch (metric)
            {
                case ProcessMetric.Cpu:
                    return results.OrderByDescending(process => process.CpuPercent)
                        .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(process => process.ProcessId).ToList();
                case ProcessMetric.Vram:
                    return results.OrderByDescending(process => process.VramBytes)
                        .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(process => process.ProcessId).ToList();
                case ProcessMetric.Memory:
                default:
                    return results.OrderByDescending(process => process.MemoryBytes)
                        .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(process => process.ProcessId).ToList();
            }
        }

        private static ApplicationProcessSample FindVisibleApplicationRoot(
            ApplicationProcessSample process,
            IReadOnlyDictionary<int, ApplicationProcessSample> processById,
            ISet<int> visibleIds)
        {
            var visited = new HashSet<int>();
            ApplicationProcessSample current = process;

            while (current != null && visited.Add(current.ProcessId))
            {
                if (IsSystemExplorer(current))
                    break;

                if (visibleIds.Contains(current.ProcessId))
                    return current;

                if (!TryGetParent(current, processById, out ApplicationProcessSample parent))
                    break;

                current = parent;
            }

            return null;
        }

        private static ApplicationProcessSample FindSafeBackgroundRoot(ApplicationProcessSample process,
            IReadOnlyDictionary<int, ApplicationProcessSample> processById)
        {
            var visited = new HashSet<int>();
            ApplicationProcessSample current = process;
            ApplicationProcessSample root = process;

            while (current != null && visited.Add(current.ProcessId) && !IsBackgroundBoundary(current) &&
                TryGetParent(current, processById, out ApplicationProcessSample parent))
            {
                if (visited.Contains(parent.ProcessId))
                    return null;
                if (IsBackgroundBoundary(parent) || !SameInstallationEdge(current, parent))
                    break;

                root = parent;
                current = parent;
            }

            return ReferenceEquals(root, process) ? null : root;
        }

        private static bool TryGetParent(ApplicationProcessSample child,
            IReadOnlyDictionary<int, ApplicationProcessSample> processById,
            out ApplicationProcessSample parent)
        {
            parent = null;
            if (child.ParentProcessId <= 0 || child.ParentProcessId == child.ProcessId ||
                !processById.TryGetValue(child.ParentProcessId, out parent))
                return false;

            // Toolhelp parent IDs can be recycled. Never follow an edge without a valid,
            // older sampled parent identity.
            return child.CreationTime != 0 && parent.CreationTime != 0 &&
                parent.CreationTime <= child.CreationTime;
        }

        private static bool SameInstallationEdge(ApplicationProcessSample child,
            ApplicationProcessSample parent)
        {
            string childPath = NormalizeExecutablePath(child.ImagePath);
            string parentPath = NormalizeExecutablePath(parent.ImagePath);
            if (childPath == null || parentPath == null)
                return false;

            if (string.Equals(childPath, parentPath, StringComparison.OrdinalIgnoreCase))
                return true;

            string childDirectory = NormalizeDirectory(Path.GetDirectoryName(childPath));
            string parentDirectory = NormalizeDirectory(Path.GetDirectoryName(parentPath));
            if (childDirectory == null || parentDirectory == null)
                return false;

            if (string.Equals(childDirectory, parentDirectory, StringComparison.OrdinalIgnoreCase))
                return !IsBroadInstallDirectory(parentDirectory);

            if (IsBroadInstallDirectory(parentDirectory))
                return false;

            // A helper directly below the parent executable's directory is a conservative
            // same-install relationship; do not walk arbitrary shared ancestors.
            string childParentDirectory = NormalizeDirectory(Path.GetDirectoryName(childDirectory));
            return string.Equals(childParentDirectory, parentDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBroadInstallDirectory(string directory)
        {
            string normalizedDirectory = NormalizeDirectory(directory);
            if (normalizedDirectory == null)
                return true;

            try
            {
                string root = NormalizeDirectory(Path.GetPathRoot(directory));
                if (string.Equals(normalizedDirectory, root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                return true;
            }

            string windowsDirectory = NormalizeDirectory(SystemWindowsPath);
            if (windowsDirectory != null &&
                (string.Equals(normalizedDirectory, windowsDirectory, StringComparison.OrdinalIgnoreCase) ||
                    normalizedDirectory.StartsWith(windowsDirectory + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)))
                return true;

            string programFiles = NormalizeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            string programFilesX86 = NormalizeDirectory(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86));
            return string.Equals(normalizedDirectory, programFiles, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalizedDirectory, programFilesX86, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBackgroundBoundary(ApplicationProcessSample process)
        {
            string processPath = NormalizeExecutablePath(process.ImagePath);
            if (processPath == null || IsSystemExplorer(process))
                return IsSystemExplorer(process);

            string executableName = GetExecutableBaseName(process.ExecutableName, process.ImagePath);
            bool serviceHost = executableName.Equals("services", StringComparison.OrdinalIgnoreCase) ||
                executableName.Equals("svchost", StringComparison.OrdinalIgnoreCase);
            bool commandHost = executableName.Equals("cmd", StringComparison.OrdinalIgnoreCase);
            bool powershellHost = executableName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
                executableName.Equals("pwsh", StringComparison.OrdinalIgnoreCase);
            if (!serviceHost && !commandHost && !powershellHost)
                return false;

            string windowsPath = NormalizeDirectory(SystemWindowsPath);
            string programFilesPath = NormalizeDirectory(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            string x86ProgramFilesPath = NormalizeDirectory(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86));
            return IsPathWithin(processPath, windowsPath) ||
                (powershellHost && (IsPathWithin(processPath, JoinPath(programFilesPath, "PowerShell")) ||
                    IsPathWithin(processPath, JoinPath(x86ProgramFilesPath, "PowerShell"))));
        }

        private static bool IsSystemExplorer(ApplicationProcessSample process)
        {
            string processPath = NormalizeExecutablePath(process.ImagePath);
            return processPath != null && SystemExplorerPath != null &&
                string.Equals(processPath, SystemExplorerPath, StringComparison.OrdinalIgnoreCase);
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

        private static string GetSystemWindowsPath()
        {
            try
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            }
            catch
            {
                return null;
            }
        }

        private static string JoinPath(string basePath, string child)
        {
            return string.IsNullOrWhiteSpace(basePath) ? null : Path.Combine(basePath, child);
        }

        private static bool IsPathWithin(string path, string directory)
        {
            string normalizedPath = NormalizeExecutablePath(path);
            string normalizedDirectory = NormalizeDirectory(directory);
            return normalizedPath != null && normalizedDirectory != null &&
                normalizedPath.StartsWith(normalizedDirectory + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeExecutablePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar).ToUpperInvariant();
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar).ToUpperInvariant();
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
            private readonly ProcessMetric _metric;
            private ulong _memoryBytes;
            private ulong _vramBytes;
            private double _cpuPercent;
            private int _processCount;
            private bool _isPartial;
            private bool _hasReadableData;
            private int _representativeProcessId;

            internal GroupAccumulator(string name, int representativeProcessId, ProcessMetric metric)
            {
                Name = name ?? string.Empty;
                _representativeProcessId = representativeProcessId;
                _metric = metric;
            }

            private string Name { get; }
            internal bool HasReadableData => _hasReadableData;

            internal void Add(ApplicationProcessSample sample, int applicationRootProcessId, bool identityPartial)
            {
                _representativeProcessId = Math.Min(_representativeProcessId, applicationRootProcessId);
                if (_metric == ProcessMetric.Memory)
                {
                    _processCount++;
                    _isPartial |= identityPartial || !sample.MemoryAvailable;
                    if (!sample.MemoryAvailable)
                        return;

                    _hasReadableData = true;
                    _memoryBytes = SaturatingAdd(_memoryBytes, sample.MemoryBytes);
                }
                else if (_metric == ProcessMetric.Cpu)
                {
                    if (!IsFinite(sample.CpuPercent) || sample.CpuPercent < 0)
                        return;

                    _processCount++;
                    _hasReadableData = true;
                    _cpuPercent = Math.Min(100d, _cpuPercent + sample.CpuPercent);
                }
                else if (sample.VramBytes > 0)
                {
                    _processCount++;
                    _hasReadableData = true;
                    _vramBytes = SaturatingAdd(_vramBytes, sample.VramBytes);
                }
            }

            internal ProcessInfo ToProcessInfo()
            {
                return new ProcessInfo
                {
                    ProcessId = _representativeProcessId,
                    ProcessCount = _processCount,
                    MemoryIsPartial = _isPartial,
                    Name = Name,
                    CpuPercent = _metric == ProcessMetric.Cpu ? (float)_cpuPercent : 0,
                    MemoryBytes = _metric == ProcessMetric.Memory ? _memoryBytes : 0,
                    VramBytes = _metric == ProcessMetric.Vram ? _vramBytes : 0
                };
            }
        }

        private static ulong SaturatingAdd(ulong left, ulong right)
        {
            return ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
