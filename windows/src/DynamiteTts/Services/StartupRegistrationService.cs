using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DynamiteTts.Services;

public class StartupRegistrationService
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "DynamiteTts";

    public bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: false);
            var value = key?.GetValue(AppName) as string;
            if (string.IsNullOrEmpty(value)) return false;

            var currentExe = GetCurrentExecutablePath();
            return value.Contains(currentExe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void SetStartupEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = GetCurrentExecutablePath();
                var formattedValue = $"\"{exePath}\"";
                key.SetValue(AppName, formattedValue, RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(AppName) != null)
                {
                    key.DeleteValue(AppName, false);
                }
            }
        }
        catch
        {
            // Non-fatal if registry permissions restricted
        }
    }

    private static string GetCurrentExecutablePath()
    {
        var mainModule = Process.GetCurrentProcess().MainModule;
        if (!string.IsNullOrEmpty(mainModule?.FileName))
        {
            return mainModule.FileName;
        }

        return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DynamiteTts.exe");
    }
}
