using System.Buffers.Binary;

namespace Gizlet.Core.Packages;

public static class PortableExecutable
{
    /// <summary>The COFF machine type of a PE image's first bytes, or null when it is not one.</summary>
    public static ushort? Machine(ReadOnlySpan<byte> head)
    {
        if (head.Length < 0x40 || head[0] != 'M' || head[1] != 'Z') return null;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(head[0x3C..]);
        if (pe < 0x40 || pe > head.Length - 24) return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(head[pe..]) != 0x00004550) return null;
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 22)..]);
        // IMAGE_FILE_EXECUTABLE_IMAGE, and not IMAGE_FILE_DLL.
        if ((characteristics & 0x0002) == 0 || (characteristics & 0x2000) != 0) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(head[(pe + 4)..]);
    }
}
