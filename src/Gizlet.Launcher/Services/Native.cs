using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gizlet.Launcher.Services;

static partial class Native
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    public static void Focus(Process process)
    {
        try
        {
            process.Refresh();
            if (process.MainWindowHandle != 0)
            {
                ShowWindow(process.MainWindowHandle, 9);
                SetForegroundWindow(process.MainWindowHandle);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);

    public static void Focus(nint window)
    {
        ShowWindow(window, 9);
        SetForegroundWindow(window);
    }
}
