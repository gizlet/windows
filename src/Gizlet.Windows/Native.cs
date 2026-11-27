using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gizlet.Windows;

static unsafe partial class Native
{
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 1;
    public const uint FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public const uint FileFlagNoBuffering = 0x20000000;
    public const uint FileFlagWriteThrough = 0x80000000;

    public const uint IoctlStorageQueryProperty = 0x002D1400;
    public const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    public const uint IoctlStorageCheckVerify2 = 0x002D0800;
    public const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    public const uint IoctlDiskGetDriveLayoutEx = 0x00070050;
    public const uint IoctlDiskGetPartitionInfoEx = 0x00070048;
    public const uint IoctlDiskUpdateProperties = 0x00070140;
    public const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    public const uint FsctlLockVolume = 0x00090018;
    public const uint FsctlUnlockVolume = 0x0009001C;
    public const uint FsctlDismountVolume = 0x00090020;

    public const uint DigcfPresent = 0x2;
    public const uint DigcfDeviceInterface = 0x10;
    public static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* input, uint inputSize, void* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", SetLastError = true)]
    public static partial nint FindFirstVolume(char* name, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextVolumeW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextVolume(nint find, char* name, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindVolumeClose(nint find);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathNamesForVolumeName(string volume, char* names, uint length, out uint returned);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(string root, char* label, uint labelLength, out uint serial, out uint maxComponent, out uint flags, char* fileSystem, uint fileSystemLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumePathName(string path, char* volume, uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeNameForVolumeMountPoint(string mountPoint, char* volume, uint length);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    public static partial nint SetupDiGetClassDevs(in Guid classGuid, nint enumerator, nint parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(nint devices, nint deviceInfo, in Guid interfaceGuid, uint index, ref DeviceInterfaceData data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetail(nint devices, ref DeviceInterfaceData data, void* detail, uint detailSize, out uint required, nint deviceInfo);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(nint devices);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceProperty(nint devices, ref DevInfoData device, in DevPropKey key, out uint type, void* buffer, uint size, out uint required, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct DevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DevPropKey(Guid fmtid, uint pid)
    {
        public Guid Fmtid = fmtid;
        public uint Pid = pid;
    }

    /// <summary>DEVPKEY_Device_LastArrivalDate: when the device last came, as a FILETIME.</summary>
    public static readonly DevPropKey LastArrivalDate = new(new Guid("83da6326-97a6-4088-9453-a1923f573b29"), 102);
    public const uint DevPropTypeFiletime = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClass;
        public uint Flags;
        public nuint Reserved;
    }

    /// <summary>A handle for queries only: no read or write access, which needs no elevation.</summary>
    public static SafeFileHandle OpenForQuery(string path) =>
        CreateFile(path, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);

    /// <summary>Runs an IOCTL into a buffer, returning the bytes written, or -1 with the error in <paramref name="error"/>.</summary>
    public static int Ioctl(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, Span<byte> output, out int error)
    {
        fixed (byte* inPointer = input)
        fixed (byte* outPointer = output)
        {
            if (DeviceIoControl(handle, code, inPointer, (uint)input.Length, outPointer, (uint)output.Length, out var returned, 0))
            {
                error = 0;
                return (int)returned;
            }
            error = Marshal.GetLastPInvokeError();
            return -1;
        }
    }

    public static void IoctlOrThrow(SafeFileHandle handle, uint code)
    {
        if (Ioctl(handle, code, [], [], out var error) < 0) throw new Win32Exception(error);
    }
}
