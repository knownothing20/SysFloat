using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SysFloat.Configuration;

namespace SysFloat
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            if (!EnsureSingleInstance())
                return;

            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            using (var context = new TrayApplicationContext())
            {
                Application.Run(context);
            }
        }

        private static bool EnsureSingleInstance()
        {
            bool createdNew;
            var mutex = new Mutex(true, "SysFloat_SingleInstance_Mutex", out createdNew);

            if (!createdNew)
            {
                try
                {
                    var current = Process.GetCurrentProcess();
                    foreach (var process in Process.GetProcessesByName("SysFloat"))
                    {
                        if (process.Id != current.Id)
                        {
                            IntPtr hwnd = process.MainWindowHandle;
                            if (hwnd != IntPtr.Zero)
                            {
                                ShowWindow(hwnd, SW_RESTORE);
                                SetForegroundWindow(hwnd);
                            }
                            break;
                        }
                    }
                }
                catch
                {
                }
                return false;
            }

            AppDomain.CurrentDomain.ProcessExit += (s, e) => mutex.ReleaseMutex();
            return true;
        }

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
