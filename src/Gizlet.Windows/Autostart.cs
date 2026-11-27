using Microsoft.Win32;

namespace Gizlet.Windows;

/// <summary>Starting the Launcher when the user signs in to Windows, through the user's Run key.</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Gizlet Launcher";
    public const string Argument = "--autostart";

    public static bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && Environment.ProcessPath is { } path
                && value.StartsWith($"\"{path}\"", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } path) key.SetValue(ValueName, $"\"{path}\" {Argument}");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
