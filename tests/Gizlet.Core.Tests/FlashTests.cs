using System.Buffers.Binary;
using System.Security.Cryptography;
using Gizlet.Core.Flash;

namespace Gizlet.Core.Tests;

sealed class MemoryDisk(long size, int sectorSize = 512) : IBlockDevice
{
    public byte[] Data { get; } = new byte[size];
    public long Length => Data.Length;
    public int SectorSize => sectorSize;
    public int Writes { get; private set; }
    public long? CorruptAt { get; set; }

    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        Assert.Equal(0, offset % sectorSize);
        Assert.Equal(0, data.Length % sectorSize);
        data.CopyTo(Data.AsSpan((int)offset));
        if (CorruptAt is { } at && at >= offset && at < offset + data.Length) Data[at] ^= 0xFF;
        Writes++;
    }

    public void Read(long offset, Span<byte> data) => Data.AsSpan((int)offset, data.Length).CopyTo(data);

    public void Flush()
    {
    }
}

public class FlashTests
{
    /// <summary>An image laid out as the R36S card is, scaled down: partition 3 at 1 MiB, 1 MiB long.</summary>
    static byte[] Image()
    {
        var image = new byte[2 << 20];
        RandomNumberGenerator.Fill(image.AsSpan(512));
        image.AsSpan(0, 512).Clear();
        image[510] = 0x55;
        image[511] = 0xAA;
        image[478 + 4] = 0x83;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(478 + 8), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(478 + 12), 2048);
        return image;
    }

    static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public void GrowsPartitionThreeToTheCard()
    {
        var mbr = CardLayout.GrowPartition(Image().AsSpan(0, 512), 64L << 30);
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(478 + 8)));
        Assert.Equal((uint)((64L << 30) / 512 - 2048), BinaryPrimitives.ReadUInt32LittleEndian(mbr.AsSpan(478 + 12)));
        var huge = CardLayout.GrowPartition(Image().AsSpan(0, 512), 4L << 40);
        Assert.Equal(uint.MaxValue - 2048u, BinaryPrimitives.ReadUInt32LittleEndian(huge.AsSpan(478 + 12)));
        var bad = Image();
        bad[478 + 4] = 0x0C;
        Assert.Equal(GizletError.ImageBadLayout, Assert.Throws<GizletException>(() => CardLayout.GrowPartition(bad.AsSpan(0, 512), 64L << 30)).Error);
    }

    [Fact]
    public void WritesAndReadsBackTheWholeImage()
    {
        var image = Image();
        var disk = new MemoryDisk(8 << 20);
        using var stream = new MemoryStream(image);
        var plan = CardWriter.Prepare(stream, image.Length, Sha(image), disk.Length);
        Assert.Empty(plan.Blocks);
        CardWriter.Write(plan, stream, disk);
        CardWriter.Verify(plan, stream, disk);
        Assert.True(disk.Data.AsSpan(512, image.Length - 512).SequenceEqual(image.AsSpan(512)));
        Assert.Equal((uint)((8 << 20) / 512 - 2048), BinaryPrimitives.ReadUInt32LittleEndian(disk.Data.AsSpan(478 + 12)));
    }

    [Fact]
    public void ReadBackFindsABadWrite()
    {
        var image = Image();
        var disk = new MemoryDisk(8 << 20) { CorruptAt = 1_500_000 };
        using var stream = new MemoryStream(image);
        var plan = CardWriter.Prepare(stream, image.Length, Sha(image), disk.Length);
        CardWriter.Write(plan, stream, disk);
        Assert.Equal(GizletError.DiskVerifyFailed, Assert.Throws<GizletException>(() => CardWriter.Verify(plan, stream, disk)).Error);
    }

    [Fact]
    public void RefusesAChangedOrOversizedImage()
    {
        var image = Image();
        using var stream = new MemoryStream(image);
        Assert.Equal(GizletError.ImageBadChecksum, Assert.Throws<GizletException>(() => CardWriter.Prepare(stream, image.Length, new string('0', 64), 8 << 20)).Error);
        Assert.Equal(GizletError.ImageBadChecksum, Assert.Throws<GizletException>(() => CardWriter.Prepare(stream, image.Length + 1, Sha(image), 8 << 20)).Error);
        Assert.Equal(GizletError.ImageTooLarge, Assert.Throws<GizletException>(() => CardWriter.Prepare(stream, image.Length, Sha(image), 1 << 20)).Error);
    }

    [Fact]
    public void OverlaysChangesInsideAChunk()
    {
        var chunk = new byte[8192];
        var blocks = new[] { new CardLayout.Block(4096, Enumerable.Repeat((byte)7, 4096).ToArray()) };
        CardLayout.Overlay(chunk, 4096 + 1024, Enumerable.Repeat((byte)1, 512).ToArray(), blocks);
        Assert.All(chunk.AsSpan(0, 3072).ToArray(), b => Assert.Equal(7, b));
        Assert.All(chunk.AsSpan(3072).ToArray(), b => Assert.Equal(0, b));
    }

    static byte[] Dtb(params byte[] payload)
    {
        var dtb = new byte[64 + payload.Length];
        dtb[20] = 0; dtb[21] = 0; dtb[22] = 0; dtb[23] = 3;
        payload.CopyTo(dtb, 32);
        return dtb;
    }

    [Fact]
    public void TellsThePanelFromAKernelDtb()
    {
        Assert.Equal(R36sPanel.V30Panel4, PanelDetector.FromDtb(Dtb(0x15, 0x00, 0x02, 0xFF, 0x30, 0x15, 0x00, 0x02, 0xFF, 0x52, 0x15, 0x00, 0x02, 0xFF, 0x01)));
        Assert.Equal(R36sPanel.Original, PanelDetector.FromDtb(Dtb(0x39, 0x00, 0x04, 0xB9, 0xF1, 0x12, 0x83)));
        Assert.Null(PanelDetector.FromDtb(Dtb(0x05, 0x78, 0x01, 0x11)));
    }

    [Fact]
    public void TellsThePanelFromABootPartition()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.Combine("boot.ini"), "odroidgoa-uboot-config\nload mmc 1:1 ${dtb_loadaddr} rk3326-r35s-linux.dtb\n");
        File.WriteAllBytes(temp.Combine("rk3326-r35s-linux.dtb"), Dtb(0x39, 0x00, 0x04, 0xB9, 0xF1, 0x12, 0x83));
        Assert.Equal(new PanelGuess(R36sPanel.Original, PanelSource.Stock), PanelDetector.FromBootPartition(temp.Path, gizlet: false));

        File.WriteAllText(temp.Combine("boot.ini"), "load mmc 1:1 ${dtb_loadaddr} ../escape.dtb\n");
        Assert.Null(PanelDetector.FromBootPartition(temp.Path, gizlet: false));

        Assert.Equal(new PanelGuess(R36sPanel.Original, PanelSource.Gizlet), PanelDetector.FromBootPartition(temp.Path, gizlet: true));
        File.WriteAllBytes(temp.Combine(PanelDetector.Panel4Overlay), [1, 2, 3]);
        File.WriteAllBytes(temp.Combine(PanelDetector.OverlayFile), [1, 2, 3]);
        Assert.Equal(new PanelGuess(R36sPanel.V30Panel4, PanelSource.Gizlet), PanelDetector.FromBootPartition(temp.Path, gizlet: true));
        File.WriteAllBytes(temp.Combine(PanelDetector.OverlayFile), [9]);
        Assert.Null(PanelDetector.FromBootPartition(temp.Path, gizlet: true));
    }

    static DiskInfo Card(Action<DiskVolume>? _ = null) => new()
    {
        Number = 3,
        DevicePath = @"\\?\usbstor#disk&ven_generic&prod_sd_card#123#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}",
        Size = 32_000_000_000,
        Bus = DiskBus.Usb,
        RemovableMedia = true,
        Vendor = "Generic",
        Product = "SD Card",
        Serial = "123",
        MediaChangeCount = 4,
        Volumes = [new DiskVolume { GuidPath = @"\\?\Volume{1}\", MountPoint = @"E:\", Label = "BOOT", FileSystem = "FAT32", Serial = 0xABCD }],
    };

    [Fact]
    public void OnlyRemovableReadersMayBeWritten()
    {
        Assert.Null(DiskRules.Refusal(Card()));
        Assert.NotNull(DiskRules.Refusal(Card() with { HoldsSystem = true }));
        Assert.NotNull(DiskRules.Refusal(Card() with { Bus = DiskBus.Other }));
        Assert.NotNull(DiskRules.Refusal(Card() with { Size = 0 }));
        Assert.NotNull(DiskRules.Refusal(Card(), imageBytes: 64_000_000_000));
    }

    [Fact]
    public void AReinsertedOrSwappedCardIsAnotherDisk()
    {
        var chosen = DiskIdentity.Of(Card());
        Assert.True(chosen.SameAs(DiskIdentity.Of(Card())));
        Assert.False(chosen.SameAs(DiskIdentity.Of(Card() with { MediaChangeCount = 5 })));
        Assert.False(chosen.SameAs(DiskIdentity.Of(Card() with { Number = 4 })));
        Assert.False(chosen.SameAs(DiskIdentity.Of(Card() with { Size = 64_000_000_000 })));
        Assert.False(chosen.SameAs(DiskIdentity.Of(Card() with { Volumes = [new DiskVolume { GuidPath = @"\\?\Volume{2}\", Serial = 1 }] })));
    }

    [Fact]
    public void RequestsRoundTripAndAreChecked()
    {
        var request = new FlashRequest
        {
            Disk = DiskIdentity.Of(Card()),
            ImagePath = @"C:\Users\me\AppData\Local\Gizlet\device\images\v0.4.4\card.img",
            ImageSize = 486_539_264,
            ImageSha256 = new string('a', 64),
            Official = true,
            Panel = R36sPanel.V30Panel4,
            Pipe = "gizlet-flash-0123456789abcdef",
            Language = "zh-CN",
        };
        var decoded = FlashRequest.Decode(request.Encode());
        Assert.True(decoded.Disk.SameAs(request.Disk));
        Assert.Equal(request with { Disk = decoded.Disk }, decoded);
        Assert.Throws<FormatException>(() => FlashRequest.Decode((request with { Pipe = "other" }).Encode()));
        Assert.Throws<FormatException>(() => FlashRequest.Decode((request with { ImageSha256 = "x" }).Encode()));
        var line = new FlashEvent { Stage = FlashStage.Failed, Error = GizletError.DiskChanged, Detail = "x" }.ToLine();
        Assert.DoesNotContain('\n', line);
        Assert.Equal(GizletError.DiskChanged, FlashEvent.FromLine(line)?.Error);
    }
}
