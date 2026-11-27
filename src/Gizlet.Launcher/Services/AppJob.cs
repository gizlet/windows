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
    readonly Lock gate = new();

    AppJob(SafeFileHandle? job) => this.job = job;

    /// <summary>
    /// Starts the app suspended, puts it in a new job and only then lets it run, so nothing it
    /// starts can escape the job. A process that cannot join the job is ended before it runs.
    /// </summary>
    public static unsafe AppJob Start(ProcessStartInfo start)
    {
        var job = CreateJobObject(0, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var app = new AppJob(job);
        try
        {
            var environment = new System.Text.StringBuilder();
            foreach (var (name, value) in start.Environment.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                environment.Append(name).Append('=').Append(value).Append('\0');
            }
            environment.Append('\0');
            var commandLine = $"\"{start.FileName}\"\0".ToCharArray();
            var startup = new StartupInfo { Size = (uint)sizeof(StartupInfo) };
            ProcessInformation info;
            fixed (char* line = commandLine)
            fixed (char* block = environment.ToString())
            {
                if (!CreateProcess(start.FileName, line, 0, 0, false, CreateSuspended | CreateUnicodeEnvironment, block,
                    start.WorkingDirectory, ref startup, out info))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), start.FileName);
                }
            }
            using var process = new SafeFileHandle(info.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(info.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, info.Process))
            {
                var error = Marshal.GetLastPInvokeError();
                TerminateProcess(info.Process, 1);
                throw new Win32Exception(error, "the app could not be put in its job");
            }
            app.processes.Add(Process.GetProcessById((int)info.ProcessId));
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return app;
        }
        catch
        {
            app.Dispose();
            throw;
        }
    }

    const uint CreateSuspended = 0x4;
    const uint CreateUnicodeEnvironment = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    struct StartupInfo
    {
        public uint Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcess(string application, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, void* environment, string? directory, ref StartupInfo startup, out ProcessInformation info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    public static AppJob Found() => new(null);

    public void Add(Process process)
    {
        lock (gate)
        {
            if (processes.Any(p => p.Id == process.Id)) process.Dispose();
            else processes.Add(process);
        }
    }

    /// <summary>Takes over another job's processes, such as ones found from an earlier Launcher.</summary>
    public void Merge(AppJob other)
    {
        List<Process> taken;
        lock (other.gate)
        {
            taken = [.. other.processes];
            other.processes.Clear();
        }
        foreach (var process in taken) Add(process);
        other.Dispose();
    }

    public Process? Main
    {
        get
        {
            lock (gate) return processes.FirstOrDefault(p => !HasExited(p));
        }
    }

    public bool Running
    {
        get
        {
            lock (gate)
            {
                if (processes.Any(p => !HasExited(p))) return true;
            }
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
        lock (gate)
        {
            foreach (var process in processes) process.Dispose();
            processes.Clear();
        }
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
