using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysFloat
{
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "SysFloat";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return key?.GetValue(ValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey, true))
                {
                    if (enabled) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue(ValueName, false);
                }
            }
            catch
            {
            }
        }
    }
}
