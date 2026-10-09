using System;
using SysFloat.Native;

namespace SysFloat.Monitoring
{
    public class MemoryMonitor
    {
        public (float percent, ulong used, ulong total) GetMemoryInfo()
        {
            var status = NativeMethods.MEMORYSTATUSEX.Create();
            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
                return (-1, 0, 0);

            float percent = status.dwMemoryLoad;
            ulong total = status.ullTotalPhys;
            ulong used = total - status.ullAvailPhys;

            return (percent, used, total);
        }
    }
}
