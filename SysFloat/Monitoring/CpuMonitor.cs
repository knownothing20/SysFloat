using System;
using SysFloat.Native;

namespace SysFloat.Monitoring
{
    public class CpuMonitor
    {
        private ulong _prevIdle;
        private ulong _prevKernel;
        private ulong _prevUser;
        private bool _initialized;

        public float GetCpuPercent()
        {
            if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
                return -1;

            ulong idleVal = idle.Value;
            ulong kernelVal = kernel.Value;
            ulong userVal = user.Value;

            if (!_initialized)
            {
                _prevIdle = idleVal;
                _prevKernel = kernelVal;
                _prevUser = userVal;
                _initialized = true;
                return -1;
            }

            ulong idleDiff = idleVal - _prevIdle;
            ulong kernelDiff = kernelVal - _prevKernel;
            ulong userDiff = userVal - _prevUser;
            ulong totalDiff = kernelDiff + userDiff;

            _prevIdle = idleVal;
            _prevKernel = kernelVal;
            _prevUser = userVal;

            if (totalDiff == 0)
                return 0;

            ulong busyDiff = totalDiff - idleDiff;
            return (float)((double)busyDiff / totalDiff * 100.0);
        }
    }
}
