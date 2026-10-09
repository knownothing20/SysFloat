using System;
using System.Runtime.InteropServices;

namespace SysFloat.Native
{
    internal static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern bool GetSystemTimes(
            out FILETIME idleTime,
            out FILETIME kernelTime,
            out FILETIME userTime);

        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TOOLWINDOW = 0x00000080;

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public ulong Value => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        public struct MEMORYSTATUSEX
        {
            [FieldOffset(0)]  public uint dwLength;
            [FieldOffset(4)]  public uint dwMemoryLoad;
            [FieldOffset(8)]  public ulong ullTotalPhys;
            [FieldOffset(16)] public ulong ullAvailPhys;
            [FieldOffset(24)] public ulong ullTotalPageFile;
            [FieldOffset(32)] public ulong ullAvailPageFile;
            [FieldOffset(40)] public ulong ullTotalVirtual;
            [FieldOffset(48)] public ulong ullAvailExtendedVirtual;

            public static MEMORYSTATUSEX Create()
            {
                var s = new MEMORYSTATUSEX();
                s.dwLength = 64;
                return s;
            }
        }
    }
}
