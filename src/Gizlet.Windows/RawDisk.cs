using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Gizlet.Core.Flash;
using Microsoft.Win32.SafeHandles;

namespace Gizlet.Windows;

/// <summary>
/// A whole disk opened for unbuffered, write-through sector access (administrators only), so a
/// read-back reads the card rather than Windows' cache.
/// </summary>
public sealed unsafe class RawDisk : IBlockDevice, IDisposable
{
    const int BufferBytes = 4 << 20;
    readonly SafeFileHandle handle;
    readonly byte* buffer;

    RawDisk(SafeFileHandle handle, long length, int sectorSize)
    {
        this.handle = handle;
        Length = length;
        SectorSize = sectorSize;
        buffer = (byte*)NativeMemory.AlignedAlloc(BufferBytes, 4096);
    }

    public long Length { get; }
    public int SectorSize { get; }
    public SafeFileHandle Handle => handle;

    public static RawDisk Open(int number)
    {
        var handle = Native.CreateFile($@"\\.\PhysicalDrive{number}", Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite, 0, Native.OpenExisting, Native.FileFlagNoBuffering | Native.FileFlagWriteThrough, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Span<byte> geometry = stackalloc byte[256];
        if (Native.Ioctl(handle, Native.IoctlDiskGetDriveGeometryEx, [], geometry, out var error) < 32)
        {
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return new RawDisk(handle, BinaryPrimitives.ReadInt64LittleEndian(geometry[24..]), BinaryPrimitives.ReadInt32LittleEndian(geometry[20..]));
    }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        Check(offset, data.Length);
        for (var done = 0; done < data.Length;)
        {
            var length = Math.Min(BufferBytes, data.Length - done);
            data.Slice(done, length).CopyTo(new Span<byte>(buffer, length));
            RandomAccess.Write(handle, new ReadOnlySpan<byte>(buffer, length), offset + done);
            done += length;
        }
    }

    public void Read(long offset, Span<byte> data)
    {
        Check(offset, data.Length);
        for (var done = 0; done < data.Length;)
        {
            var length = Math.Min(BufferBytes, data.Length - done);
            var read = RandomAccess.Read(handle, new Span<byte>(buffer, length), offset + done);
            if (read != length) throw new IOException($"Read {read} of {length} bytes at {offset + done}");
            new ReadOnlySpan<byte>(buffer, length).CopyTo(data.Slice(done, length));
            done += length;
        }
    }

    public void Flush()
    {
        if (!Native.FlushFileBuffers(handle)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>Asks Windows to read the new partition table.</summary>
    public void UpdateProperties() => Native.IoctlOrThrow(handle, Native.IoctlDiskUpdateProperties);

    /// <summary>The reader's media change count, as <see cref="DiskInfo.MediaChangeCount"/> reads it.</summary>
    public uint? MediaChangeCount()
    {
        Span<byte> changes = stackalloc byte[4];
        return Native.Ioctl(handle, Native.IoctlStorageCheckVerify2, [], changes, out _) >= 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(changes)
            : null;
    }

    void Check(long offset, int length)
    {
        if (offset < 0 || offset % SectorSize != 0 || length % SectorSize != 0 || offset + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), $"{length} bytes at {offset} on a {Length}-byte disk of {SectorSize}-byte sectors");
        }
    }

    public void Dispose()
    {
        handle.Dispose();
        NativeMemory.AlignedFree(buffer);
    }
}

/// <summary>
/// The volumes of a disk, locked and dismounted so Windows lets their sectors be written; released
/// when disposed.
/// </summary>
public sealed class LockedVolumes : IDisposable
{
    readonly List<SafeFileHandle> handles = [];

    public static LockedVolumes Lock(IEnumerable<DiskVolume> volumes, TimeSpan timeout)
    {
        var locked = new LockedVolumes();
        try
        {
            foreach (var volume in volumes)
            {
                var handle = Native.CreateFile(volume.GuidPath.TrimEnd('\\'), Native.GenericRead | Native.GenericWrite,
                    Native.FileShareRead | Native.FileShareWrite, 0, Native.OpenExisting, 0, 0);
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), volume.GuidPath);
                locked.handles.Add(handle);
                var deadline = DateTime.UtcNow + timeout;
                while (Native.Ioctl(handle, Native.FsctlLockVolume, [], [], out var error) < 0)
                {
                    if (DateTime.UtcNow > deadline) throw new Win32Exception(error, $"{volume.MountPoint ?? volume.GuidPath} is in use");
                    Thread.Sleep(250);
                }
                Native.IoctlOrThrow(handle, Native.FsctlDismountVolume);
            }
            return locked;
        }
        catch
        {
            locked.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var handle in handles)
        {
            Native.Ioctl(handle, Native.FsctlUnlockVolume, [], [], out _);
            handle.Dispose();
        }
        handles.Clear();
    }
}
