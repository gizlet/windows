using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Gizlet.Core.Flash;
using Microsoft.Win32;

namespace Gizlet.Windows;

/// <summary>
/// Lists the disks Windows sees, with what the card writer needs to tell them apart. Every query
/// here works on a handle opened without read or write access, so it needs no elevation.
/// </summary>
public static unsafe class DiskEnumerator
{
    static readonly Guid EfiSystem = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    static readonly Guid MicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    static readonly Guid WindowsRecovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    /// <param name="protectedPaths">Folders whose disks must never be written, besides Windows' own.</param>
    public static IReadOnlyList<DiskInfo> List(IEnumerable<string>? protectedPaths = null)
    {
        var volumes = Volumes();
        var protectedVolumes = ProtectedVolumes(protectedPaths ?? []);
        var disks = new List<DiskInfo>();
        foreach (var path in DiskInterfaces())
        {
            if (Describe(path, volumes, protectedVolumes) is { } disk) disks.Add(disk);
        }
        disks.Sort((a, b) => a.Number.CompareTo(b.Number));
        return disks;
    }

    /// <summary>What a disk handle (opened for queries, or for writing) says about the disk.</summary>
    public sealed record DeviceFacts(int Number, DiskBus Bus, bool RemovableMedia, string Vendor, string Product, string? Serial,
        long Size, int BytesPerSector, uint? MediaChangeCount);

    public static DeviceFacts? Query(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        Span<byte> number = stackalloc byte[12];
        if (Native.Ioctl(handle, Native.IoctlStorageGetDeviceNumber, [], number, out _) < 12) return null;
        // FILE_DEVICE_DISK only: never a CD-ROM, a volume or anything else behind the same path.
        if (BinaryPrimitives.ReadInt32LittleEndian(number) != 7) return null;
        var diskNumber = BinaryPrimitives.ReadInt32LittleEndian(number[4..]);

        Span<byte> query = stackalloc byte[12];
        var descriptor = new byte[1024];
        var length = Native.Ioctl(handle, Native.IoctlStorageQueryProperty, query, descriptor, out _);
        if (length < 32) return null;
        var bus = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(28)) switch
        {
            7 => DiskBus.Usb,
            0xC => DiskBus.Sd,
            0xD => DiskBus.Mmc,
            _ => DiskBus.Other,
        };
        long size = 0;
        var sector = 512;
        Span<byte> geometry = stackalloc byte[256];
        if (Native.Ioctl(handle, Native.IoctlDiskGetDriveGeometryEx, [], geometry, out _) >= 32)
        {
            sector = BinaryPrimitives.ReadInt32LittleEndian(geometry[20..]);
            size = BinaryPrimitives.ReadInt64LittleEndian(geometry[24..]);
        }
        Span<byte> changes = stackalloc byte[4];
        uint? mediaChanges = Native.Ioctl(handle, Native.IoctlStorageCheckVerify2, [], changes, out _) >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(changes)
            : null;
        return new DeviceFacts(diskNumber, bus, descriptor[10] != 0,
            DescriptorString(descriptor, length, 12), DescriptorString(descriptor, length, 16),
            DescriptorString(descriptor, length, 24) is { Length: > 0 } serial ? serial : null,
            size, sector, mediaChanges);
    }

    /// <summary>The volumes Windows has on a disk.</summary>
    public static IReadOnlyList<DiskVolume> VolumesOf(int number) =>
        Volumes().Where(v => v.Disks.Contains(number)).Select(v => v.Volume).ToList();

    static DiskInfo? Describe(string path, List<(DiskVolume Volume, HashSet<int> Disks, bool System)> volumes, HashSet<string> protectedVolumes)
    {
        using var handle = Native.OpenForQuery(path);
        if (handle.IsInvalid || Query(handle) is not { } facts) return null;
        var own = volumes.Where(v => v.Disks.Contains(facts.Number)).ToList();
        var holdsSystem = own.Any(v => v.System || protectedVolumes.Contains(v.Volume.GuidPath))
            || (facts.Bus == DiskBus.Other && facts.Size > 0 && own.Count == 0 && facts.Number == 0);
        return new DiskInfo
        {
            Number = facts.Number,
            DevicePath = path,
            Size = facts.Size,
            BytesPerSector = facts.BytesPerSector,
            Bus = facts.Bus,
            RemovableMedia = facts.RemovableMedia,
            Vendor = facts.Vendor,
            Product = facts.Product,
            Serial = facts.Serial,
            MediaChangeCount = facts.MediaChangeCount,
            Volumes = own.Select(v => v.Volume).ToList(),
            HoldsSystem = holdsSystem,
        };
    }

    /// <summary>
    /// Whether the disk may hold an EFI system, Microsoft reserved or recovery partition (GPT types,
    /// or MBR types 0xEF and 0x27). Reading the layout needs read access, so the elevated helper
    /// checks this before it writes. A layout that cannot be read counts as holding one.
    /// </summary>
    public static bool HasSystemPartitions(Microsoft.Win32.SafeHandles.SafeFileHandle disk)
    {
        const int ErrorInsufficientBuffer = 122;
        const int ErrorMoreData = 234;
        for (var entries = 128; entries <= 4096; entries *= 2)
        {
            var layout = new byte[48 + 144 * entries];
            var length = Native.Ioctl(disk, Native.IoctlDiskGetDriveLayoutEx, [], layout, out var error);
            if (length < 0 && error is ErrorInsufficientBuffer or ErrorMoreData) continue;
            if (length < 48) return true;
            var style = BinaryPrimitives.ReadInt32LittleEndian(layout);
            var count = BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(4));
            if (count < 0 || 48 + (long)count * 144 > length) return true;
            for (var i = 0; i < count; i++)
            {
                var entry = layout.AsSpan(48 + i * 144, 144);
                if (style == 1)
                {
                    var type = new Guid(entry.Slice(32, 16));
                    if (type == EfiSystem || type == MicrosoftReserved || type == WindowsRecovery) return true;
                }
                else if (style == 0)
                {
                    if (entry[32] is 0xEF or 0x27) return true;
                }
                else
                {
                    return style != 2;
                }
            }
            return false;
        }
        return true;
    }

    static string DescriptorString(byte[] descriptor, int length, int offsetField)
    {
        var offset = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(offsetField));
        if (offset <= 0 || offset >= length) return "";
        var end = Array.IndexOf(descriptor, (byte)0, offset, length - offset);
        if (end < 0) end = length;
        return Encoding.ASCII.GetString(descriptor, offset, end - offset).Trim();
    }

    static List<string> DiskInterfaces()
    {
        var paths = new List<string>();
        var devices = Native.SetupDiGetClassDevs(Native.DiskInterface, 0, 0, Native.DigcfPresent | Native.DigcfDeviceInterface);
        if (devices == -1) return paths;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new Native.DeviceInterfaceData { Size = (uint)sizeof(Native.DeviceInterfaceData) };
                if (!Native.SetupDiEnumDeviceInterfaces(devices, 0, Native.DiskInterface, index, ref data)) break;
                Native.SetupDiGetDeviceInterfaceDetail(devices, ref data, null, 0, out var required, 0);
                if (required is < 8 or > 4096) continue;
                var buffer = new byte[required];
                fixed (byte* detail = buffer)
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W's cbSize is 8 on 64-bit Windows.
                    *(uint*)detail = 8;
                    if (!Native.SetupDiGetDeviceInterfaceDetail(devices, ref data, detail, required, out _, 0)) continue;
                    paths.Add(new string((char*)(detail + 4)));
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(devices);
        }
        return paths;
    }

    /// <summary>Every volume, the disks it lies on, and whether it is a system volume.</summary>
    static List<(DiskVolume Volume, HashSet<int> Disks, bool System)> Volumes()
    {
        var result = new List<(DiskVolume, HashSet<int>, bool)>();
        var systemLetters = SystemLetters();
        var name = stackalloc char[300];
        var label = stackalloc char[261];
        var fileSystem = stackalloc char[261];
        var find = Native.FindFirstVolume(name, 300);
        if (find == -1) return result;
        try
        {
            do
            {
                var guidPath = new string(name);
                var mountPoints = MountPoints(guidPath);
                var disks = new HashSet<int>();
                var system = mountPoints.Any(m => m.Length >= 2 && systemLetters.Contains(char.ToUpperInvariant(m[0])) && m[1] == ':');
                using (var handle = Native.OpenForQuery(guidPath.TrimEnd('\\')))
                {
                    if (!handle.IsInvalid)
                    {
                        var extents = new byte[8 + 24 * 32];
                        var length = Native.Ioctl(handle, Native.IoctlVolumeGetVolumeDiskExtents, [], extents, out _);
                        if (length >= 8)
                        {
                            var count = BinaryPrimitives.ReadInt32LittleEndian(extents);
                            for (var i = 0; i < count && 8 + (i + 1) * 24 <= length; i++) disks.Add(BinaryPrimitives.ReadInt32LittleEndian(extents.AsSpan(8 + i * 24)));
                        }
                        var partition = new byte[144];
                        if (Native.Ioctl(handle, Native.IoctlDiskGetPartitionInfoEx, [], partition, out _) >= 144
                            && BinaryPrimitives.ReadInt32LittleEndian(partition) == 1)
                        {
                            var type = new Guid(partition.AsSpan(32, 16));
                            system |= type == EfiSystem || type == MicrosoftReserved || type == WindowsRecovery;
                        }
                    }
                }
                uint? serial = null;
                string? labelText = null, fileSystemText = null;
                if (Native.GetVolumeInformation(guidPath, label, 261, out var s, out _, out _, fileSystem, 261))
                {
                    serial = s;
                    labelText = new string(label);
                    fileSystemText = new string(fileSystem);
                }
                var volume = new DiskVolume
                {
                    GuidPath = guidPath,
                    MountPoint = mountPoints.FirstOrDefault(m => m.Length == 3 && m[1] == ':'),
                    Label = labelText,
                    FileSystem = fileSystemText,
                    Serial = serial,
                };
                result.Add((volume, disks, system));
            }
            while (Native.FindNextVolume(find, name, 300));
        }
        finally
        {
            Native.FindVolumeClose(find);
        }
        return result;
    }

    static List<string> MountPoints(string guidPath)
    {
        var buffer = stackalloc char[2048];
        if (!Native.GetVolumePathNamesForVolumeName(guidPath, buffer, 2048, out var returned)) return [];
        return new string(buffer, 0, (int)Math.Min(returned, 2048)).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>The drive letters of Windows, programs, user folders and page files.</summary>
    static HashSet<char> SystemLetters()
    {
        var letters = new HashSet<char>();
        void Add(string? path)
        {
            if (path is { Length: >= 2 } && path[1] == ':' && char.IsAsciiLetter(path[0])) letters.Add(char.ToUpperInvariant(path[0]));
        }
        Add(Environment.GetEnvironmentVariable("SystemDrive"));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Add(Path.GetTempPath());
        Add(AppContext.BaseDirectory);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            foreach (var file in key?.GetValue("PagingFiles") as string[] ?? [])
            {
                if (file.StartsWith("?:", StringComparison.Ordinal)) Add(Environment.GetEnvironmentVariable("SystemDrive"));
                else Add(file);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return letters;
    }

    /// <summary>The volumes (as <c>\\?\Volume{GUID}\</c>) that hold the given folders.</summary>
    static HashSet<string> ProtectedVolumes(IEnumerable<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = stackalloc char[300];
        var guid = stackalloc char[300];
        foreach (var path in paths)
        {
            if (!Native.GetVolumePathName(path, root, 300)) continue;
            if (Native.GetVolumeNameForVolumeMountPoint(new string(root), guid, 300)) result.Add(new string(guid));
        }
        return result;
    }

    /// <summary>Whether this platform has disks to list.</summary>
    public static bool Supported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}
