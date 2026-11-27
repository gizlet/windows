using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gizlet.Launcher.Services;

/// <summary>
/// The processes of one running app: the one the Launcher started, every process it starts (a Job
/// Object), and any found from an earlier Launcher. Running until the last of them exits.
/// </summary>
sealed partial class AppJob : IDisposable
{
    readonly SafeFileHandle? job;
    readonly List<Process> processes = [];

    AppJob(SafeFileHandle? job) => this.job = job;

    public static AppJob Start(ProcessStartInfo start)
    {
        var job = CreateJobObject(0, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var app = new AppJob(job);
        try
        {
            var process = Process.Start(start) ?? throw new InvalidOperationException(start.FileName);
            // A child started before this line escapes the job; apps start their children later.
            if (!AssignProcessToJobObject(job, process.Handle)) App.Log(new Win32Exception(Marshal.GetLastPInvokeError()));
            app.processes.Add(process);
            return app;
        }
        catch
        {
            app.Dispose();
            throw;
        }
    }

    public static AppJob Found() => new(null);

    public void Add(Process process) => processes.Add(process);

    public Process? Main => processes.FirstOrDefault(p => !HasExited(p));

    public bool Running
    {
        get
        {
            if (processes.Any(p => !HasExited(p))) return true;
            if (job is null || job.IsInvalid) return false;
            // JOBOBJECT_BASIC_ACCOUNTING_INFORMATION.ActiveProcesses, at byte 40.
            Span<byte> info = stackalloc byte[48];
            unsafe
            {
                fixed (byte* p = info)
                {
                    if (!QueryInformationJobObject(job, 1, p, (uint)info.Length, out _)) return false;
                }
            }
            return BitConverter.ToUInt32(info[40..]) > 0;
        }
    }

    static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        foreach (var process in processes) process.Dispose();
        job?.Dispose();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryInformationJobObject(SafeFileHandle job, int infoClass, void* info, uint length, out uint returned);
}
