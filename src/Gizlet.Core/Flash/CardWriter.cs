using System.Security.Cryptography;

namespace Gizlet.Core.Flash;

/// <summary>A disk opened for raw sector access.</summary>
public interface IBlockDevice
{
    long Length { get; }
    int SectorSize { get; }
    /// <summary>Writes whole sectors at a sector-aligned offset.</summary>
    void Write(long offset, ReadOnlySpan<byte> data);
    /// <summary>Reads whole sectors at a sector-aligned offset, from the media rather than a cache.</summary>
    void Read(long offset, Span<byte> data);
    void Flush();
}

/// <summary>What will be written over the card: the image with its grown MBR, then the grow blocks.</summary>
public sealed record CardPlan(long ImageBytes, byte[] Mbr, IReadOnlyList<CardLayout.Block> Blocks)
{
    public long TotalBytes => ImageBytes + Blocks.Count(b => b.Offset >= ImageBytes) * 4096L;
}

/// <summary>Writes an R36S image over a whole card and reads it all back.</summary>
public static class CardWriter
{
    const int Chunk = 4 << 20;

    /// <summary>
    /// Checks the image against its expected size and SHA-256, reading the stream the write will use,
    /// and plans the write for a card of <paramref name="diskBytes"/>.
    /// </summary>
    public static CardPlan Prepare(Stream image, long expectedSize, string expectedSha256, long diskBytes, IProgress<(long Done, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (image.Length != expectedSize) throw new GizletException(GizletError.ImageBadChecksum, $"{image.Length} bytes, expected {expectedSize}");
        if (image.Length > diskBytes) throw new GizletException(GizletError.ImageTooLarge, $"{image.Length} bytes on a {diskBytes}-byte card");
        image.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = image.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            done += read;
            progress?.Report((done, image.Length));
        }
        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new GizletException(GizletError.ImageBadChecksum, "SHA-256");
        }
        var mbr = new byte[512];
        image.Position = 0;
        image.ReadExactly(mbr);
        var grown = CardLayout.GrowPartition(mbr, diskBytes);
        var head = new byte[8192];
        var start = CardLayout.DataStart(grown);
        IReadOnlyList<CardLayout.Block> blocks = [];
        if (start + head.Length <= image.Length)
        {
            image.Position = start;
            image.ReadExactly(head);
            blocks = CardLayout.GrowFilesystem(grown, head);
        }
        if (blocks.Any(b => b.Offset % 4096 != 0 || b.Offset + 4096 > diskBytes)) throw new GizletException(GizletError.ImageBadLayout, "grow block outside the card");
        return new CardPlan(image.Length, grown, blocks);
    }

    public static void Write(CardPlan plan, Stream image, IBlockDevice device, IProgress<(long Done, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[Chunk];
        long offset = 0;
        image.Position = 0;
        while (offset < plan.ImageBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Fill(image, buffer, plan.ImageBytes - offset);
            CardLayout.Overlay(buffer.AsSpan(0, length), offset, plan.Mbr, []);
            var padded = Pad(buffer, length, device.SectorSize);
            device.Write(offset, buffer.AsSpan(0, padded));
            offset += length;
            progress?.Report((offset, plan.TotalBytes));
        }
        var done = plan.ImageBytes;
        foreach (var block in plan.Blocks)
        {
            device.Write(block.Offset, block.Data);
            if (block.Offset >= plan.ImageBytes) progress?.Report((done += 4096, plan.TotalBytes));
        }
        device.Flush();
    }

    /// <summary>Reads the card back and compares every written byte with the plan.</summary>
    public static void Verify(CardPlan plan, Stream image, IBlockDevice device, IProgress<(long Done, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        var expected = new byte[Chunk];
        var actual = new byte[Chunk];
        long offset = 0;
        image.Position = 0;
        while (offset < plan.ImageBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Fill(image, expected, plan.ImageBytes - offset);
            CardLayout.Overlay(expected.AsSpan(0, length), offset, plan.Mbr, plan.Blocks);
            var padded = Pad(expected, length, device.SectorSize);
            device.Read(offset, actual.AsSpan(0, padded));
            if (!actual.AsSpan(0, padded).SequenceEqual(expected.AsSpan(0, padded)))
            {
                throw new GizletException(GizletError.DiskVerifyFailed, $"bytes {offset} to {offset + padded} differ");
            }
            offset += length;
            progress?.Report((offset, plan.TotalBytes));
        }
        var done = plan.ImageBytes;
        var block = new byte[4096];
        foreach (var written in plan.Blocks.Where(b => b.Offset >= plan.ImageBytes))
        {
            device.Read(written.Offset, block);
            if (!block.AsSpan().SequenceEqual(LastWrite(plan, written.Offset)))
            {
                throw new GizletException(GizletError.DiskVerifyFailed, $"block at {written.Offset} differs");
            }
            progress?.Report((done += 4096, plan.TotalBytes));
        }
    }

    static byte[] LastWrite(CardPlan plan, long offset) => plan.Blocks.Last(b => b.Offset == offset).Data;

    static int Fill(Stream image, byte[] buffer, long remaining)
    {
        var want = (int)Math.Min(buffer.Length, remaining);
        image.ReadExactly(buffer, 0, want);
        return want;
    }

    static int Pad(byte[] buffer, int length, int sectorSize)
    {
        var padded = (length + sectorSize - 1) / sectorSize * sectorSize;
        if (padded > length) Array.Clear(buffer, length, padded - length);
        return padded;
    }
}
