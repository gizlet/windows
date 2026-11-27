using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Gizlet.Core.Flash;

namespace Gizlet.Core.Tests;

/// <summary>
/// Grows an empty ext4 filesystem made with gizlet/os's options for the R36S data partition
/// (<c>-b 4096 -i 65536 -O ^resize_inode,meta_bg</c>, 32768 blocks), and checks the result with a
/// bitwise CRC32C written here and, where e2fsprogs is installed, with <c>e2fsck -fn</c>.
/// </summary>
public class Ext4GrowTests
{
    const int Block = 4096;
    const long Group = 32768;

    static byte[] Partition(string variant)
    {
        using var gz = new GZipStream(File.OpenRead(Fixtures.Path($"ext4-data-{variant}.img.gz")), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        gz.CopyTo(raw);
        return raw.ToArray();
    }

    /// <summary>The grown filesystem: the image's blocks, with the grow blocks over them.</summary>
    sealed class Grown(byte[] image, IReadOnlyList<CardLayout.Block> blocks)
    {
        readonly Dictionary<long, byte[]> written = blocks.ToDictionary(b => b.Offset / Block, b => b.Data);

        public byte[] Read(long block)
        {
            if (written.TryGetValue(block, out var data)) return data;
            var result = new byte[Block];
            if ((block + 1) * Block <= image.Length) Array.Copy(image, block * Block, result, 0, Block);
            return result;
        }

        public IReadOnlyDictionary<long, byte[]> Written => written;
    }

    // Bitwise, so it shares nothing with the table the grow code uses.
    static uint Crc32c(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82F63B78u : crc >> 1;
        }
        return crc;
    }

    static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
    static ushort U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);

    [Theory]
    [InlineData("plain", 1L << 30)]                     // 8 full groups
    [InlineData("plain", (1L << 30) + (100L << 20))]    // a partial last group
    [InlineData("plain", (1L << 30) + (128L << 10))]    // a last group too small to keep
    [InlineData("plain", 64 * Group * Block + (512L << 20))] // past the first descriptor block (meta_bg group 64)
    [InlineData("seed", (3L << 30) + (7L << 20))]
    public void GrowsToAConsistentFilesystem(string variant, long bytes)
    {
        var image = Partition(variant);
        var head = image.AsSpan(0, 8192).ToArray();
        var blocks = Ext4Grow.Blocks(head, 0, bytes / 512);
        Assert.NotEmpty(blocks);
        var fs = new Grown(image, blocks);

        // Only metadata of the new groups and the superblocks are written, never the image's data.
        var imageBlocks = image.Length / Block;
        Assert.All(fs.Written.Keys.Where(b => b < imageBlocks), b => Assert.True(b == 0 || b == 1, $"block {b} of the image rewritten"));

        var sb = fs.Read(0).AsSpan(1024, 1024).ToArray();
        Assert.Equal(0xEF53, U16(sb, 0x38));
        Assert.Equal(U32(sb, 0x3FC), Crc32c(0xFFFFFFFF, sb.AsSpan(0, 0x3FC)));
        long count = U32(sb, 0x04) | ((long)U32(sb, 0x150) << 32);
        var groups = (count + Group - 1) / Group;
        Assert.True(count * Block <= bytes, "the filesystem fits the partition");
        Assert.True(count * Block > bytes - 2 * Group * Block, "the filesystem fills the partition");
        Assert.Equal(groups * U32(sb, 0x28), U32(sb, 0x00));

        var incompat = U32(sb, 0x60);
        var seed = (incompat & 0x2000) != 0 ? U32(sb, 0x270) : Crc32c(0xFFFFFFFF, sb.AsSpan(0x68, 16));
        long free = 0;
        for (long g = 0; g < groups; g++)
        {
            // meta_bg: descriptor block k sits in group 64k, at its first block after a backup superblock.
            var k = g / 64;
            var location = k * 64 * Group + (HasSuper(k * 64) ? 1 : 0);
            var desc = fs.Read(location).AsSpan((int)(g % 64) * 64, 64).ToArray();
            var check = desc.ToArray();
            check[30] = check[31] = 0;
            var number = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)g);
            Assert.Equal(U16(desc, 30), (ushort)(Crc32c(Crc32c(seed, number), check) & 0xFFFF));
            free += U16(desc, 12) | ((long)U16(desc, 0x2C) << 16);
            // A group with BLOCK_UNINIT has no bitmap on disk to check.
            if ((U16(desc, 18) & 2) == 0)
            {
                var bitmap = fs.Read(U32(desc, 0));
                var csum = U16(desc, 24) | ((uint)U16(desc, 56) << 16);
                Assert.Equal(csum, Crc32c(seed, bitmap.AsSpan(0, (int)(Group / 8))));
            }
        }
        long sbFree = U32(sb, 0x0C) | ((long)U32(sb, 0x158) << 32);
        Assert.Equal(sbFree, free);

        E2fsck(image, fs, bytes);
    }

    static bool HasSuper(long g)
    {
        if (g <= 1) return true;
        foreach (var p in new long[] { 3, 5, 7 })
        {
            for (var n = p; n <= g; n *= p) if (n == g) return true;
        }
        return false;
    }

    /// <summary>The grown filesystem through e2fsck, when it is installed.</summary>
    static void E2fsck(byte[] image, Grown fs, long bytes)
    {
        var tool = new[] { "/opt/homebrew/opt/e2fsprogs/sbin/e2fsck", "/usr/local/opt/e2fsprogs/sbin/e2fsck", "/sbin/e2fsck", "/usr/sbin/e2fsck" }.FirstOrDefault(File.Exists);
        if (tool is null || OperatingSystem.IsWindows()) return;
        using var temp = new TempDirectory();
        var path = temp.Combine("data.img");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            file.SetLength(bytes);
            file.Write(image);
            foreach (var (block, data) in fs.Written)
            {
                file.Position = block * Block;
                file.Write(data);
            }
        }
        using var process = Process.Start(new ProcessStartInfo(tool, ["-fn", path]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
    }
}
