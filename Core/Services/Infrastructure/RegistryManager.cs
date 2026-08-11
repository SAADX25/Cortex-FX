using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Reflection;

namespace CortexFX.Core.Services.Infrastructure
{
    public static class RegistryManager
    {
        private const string MenuName = "CortexFX";
        private const string MenuText = "Convert with Cortex FX";

        public static bool IsRegistered()
        {
            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\*\shell\{MenuName}"))
                {
                    return key != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void RegisterContextMenu()
        {
            var mainModule = Process.GetCurrentProcess().MainModule;
            if (mainModule == null) throw new Exception("Could not determine executable path.");

            string exePath = mainModule.FileName;
            string command = $"\"{exePath}\" \"%1\"";

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\*\shell\{MenuName}"))
                {
                    if (key == null) throw new Exception("Could not create registry key.");

                    key.SetValue(null, MenuText);
                    // Set the icon to the executable path. Windows will use the first icon resource.
                    key.SetValue("Icon", exePath, RegistryValueKind.String);

                    using (RegistryKey commandKey = key.CreateSubKey("command"))
                    {
                        if (commandKey == null) throw new Exception("Could not create command key.");
                        commandKey.SetValue(null, command);
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to register context menu: {ex.Message}");
            }
        }

        public static void UnregisterContextMenu()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\*\shell\{MenuName}", false);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to unregister context menu: {ex.Message}");
            }
        }
    }
}