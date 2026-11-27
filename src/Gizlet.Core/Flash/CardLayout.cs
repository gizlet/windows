using System.Buffers.Binary;

namespace Gizlet.Core.Flash;

/// <summary>
/// The R36S image's partition table and data filesystem, grown to fill a card as gizlet/os's
/// <c>boards/gameconsole/r36s/flash.ps1</c> grows them: partition 3 is extended to the end of the
/// card (aligned down to 1 MiB, at most the MBR's 2 TiB), and its ext4 filesystem is grown offline
/// by writing the new groups' metadata, primary superblock last.
/// </summary>
public static class CardLayout
{
    const int Partition3 = 478;

    /// <summary>A 4 KiB block to write at a byte offset of the card.</summary>
    public sealed record Block(long Offset, byte[] Data);

    /// <summary>The image's MBR with partition 3 extended to a card of <paramref name="diskBytes"/>.</summary>
    public static byte[] GrowPartition(ReadOnlySpan<byte> imageMbr, long diskBytes)
    {
        if (imageMbr.Length < 512 || imageMbr[510] != 0x55 || imageMbr[511] != 0xAA) throw new GizletException(GizletError.ImageBadLayout, "no MBR");
        var mbr = imageMbr[..512].ToArray();
        long start = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(Partition3 + 8));
        long sectors = diskBytes / 512 / 2048 * 2048 - start;
        if (start + sectors > uint.MaxValue) sectors = uint.MaxValue - start;
        if (mbr[Partition3 + 4] != 0x83 || start <= 0 || sectors <= 0) throw new GizletException(GizletError.ImageBadLayout, "partition 3 is not a Linux partition");
        long imageSectors = BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(Partition3 + 12));
        if (sectors < imageSectors) throw new GizletException(GizletError.ImageTooLarge, $"{diskBytes} bytes");
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(Partition3 + 12), (uint)sectors);
        return mbr;
    }

    /// <summary>Byte offset of partition 3 in an MBR.</summary>
    public static long DataStart(ReadOnlySpan<byte> mbr) => (long)BinaryPrimitives.ReadUInt32LittleEndian(mbr[(Partition3 + 8)..]) * 512;

    /// <summary>
    /// The blocks that grow the image's ext4 data filesystem to fill partition 3 of the grown
    /// <paramref name="mbr"/>. <paramref name="head"/> is the image's first 8 KiB of partition 3. An
    /// image laid out otherwise gives none, and the card's first boot grows the filesystem instead.
    /// </summary>
    public static IReadOnlyList<Block> GrowFilesystem(ReadOnlySpan<byte> mbr, byte[] head)
    {
        long startSector = BinaryPrimitives.ReadUInt32LittleEndian(mbr[(Partition3 + 8)..]);
        long sectors = BinaryPrimitives.ReadUInt32LittleEndian(mbr[(Partition3 + 12)..]);
        return Ext4Grow.Blocks(head, startSector, sectors);
    }

    /// <summary>
    /// The image's bytes with the card's changes over them: the grown MBR at offset 0 and the grow
    /// blocks inside the image's length. Blocks past the image are written separately.
    /// </summary>
    public static void Overlay(Span<byte> chunk, long offset, byte[] mbr, IReadOnlyList<Block> blocks)
    {
        Apply(chunk, offset, 0, mbr);
        foreach (var block in blocks) Apply(chunk, offset, block.Offset, block.Data);
    }

    static void Apply(Span<byte> chunk, long chunkOffset, long at, byte[] data)
    {
        var start = Math.Max(chunkOffset, at);
        var end = Math.Min(chunkOffset + chunk.Length, at + data.Length);
        if (start >= end) return;
        data.AsSpan((int)(start - at), (int)(end - start)).CopyTo(chunk[(int)(start - chunkOffset)..]);
    }
}
