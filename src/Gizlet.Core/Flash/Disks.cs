namespace Gizlet.Core.Flash;

public enum DiskBus { Other, Usb, Sd, Mmc }

public sealed record DiskVolume
{
    /// <summary><c>\\?\Volume{GUID}\</c>.</summary>
    public string GuidPath { get; init; } = "";
    /// <summary>A drive letter such as <c>E:\</c>, when it has one.</summary>
    public string? MountPoint { get; init; }
    public string? Label { get; init; }
    public string? FileSystem { get; init; }
    public uint? Serial { get; init; }
}

/// <summary>What Windows tells about a disk, as the Launcher (unelevated) and the helper read it.</summary>
public sealed record DiskInfo
{
    public int Number { get; init; }
    /// <summary>The disk's device interface path, stable while it stays plugged in.</summary>
    public string DevicePath { get; init; } = "";
    public long Size { get; init; }
    public int BytesPerSector { get; init; } = 512;
    public DiskBus Bus { get; init; }
    public bool RemovableMedia { get; init; }
    public string Vendor { get; init; } = "";
    public string Product { get; init; } = "";
    public string? Serial { get; init; }
    /// <summary>The reader's media change count: it moves when a card is taken out or put in.</summary>
    public uint? MediaChangeCount { get; init; }
    public IReadOnlyList<DiskVolume> Volumes { get; init; } = [];
    /// <summary>Holds Windows, the boot or EFI system partition, a page file, or the Launcher itself.</summary>
    public bool HoldsSystem { get; init; }

    public string Reader => string.Join(' ', new[] { Vendor, Product }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
    public string VolumeNames => string.Join(", ", Volumes.Select(v => v.Label).Where(l => !string.IsNullOrWhiteSpace(l)));
    public bool HasGizletOs => Volumes.Any(v => string.Equals(v.Label, "GIZLETBOOT", StringComparison.OrdinalIgnoreCase));
    public bool LooksLikeStockCard => Volumes.Any(v => string.Equals(v.Label, "ROMS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(v.Label, "EASYROMS", StringComparison.OrdinalIgnoreCase));
    public DiskVolume? BootVolume => Volumes.FirstOrDefault(v => v.FileSystem is { } fs && fs.StartsWith("FAT", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The facts a confirmation was given for. The helper reads them again after elevation and writes
/// nothing unless every one is the same: another card, a card taken out and put back, or a disk
/// renumbered since is refused.
/// </summary>
public sealed record DiskIdentity
{
    public int Number { get; init; }
    public string DevicePath { get; init; } = "";
    public long Size { get; init; }
    public DiskBus Bus { get; init; }
    public string Vendor { get; init; } = "";
    public string Product { get; init; } = "";
    public string? Serial { get; init; }
    public uint? MediaChangeCount { get; init; }
    public IReadOnlyList<string> Volumes { get; init; } = [];

    public static DiskIdentity Of(DiskInfo disk) => new()
    {
        Number = disk.Number,
        DevicePath = disk.DevicePath,
        Size = disk.Size,
        Bus = disk.Bus,
        Vendor = disk.Vendor,
        Product = disk.Product,
        Serial = disk.Serial,
        MediaChangeCount = disk.MediaChangeCount,
        Volumes = disk.Volumes.Select(v => $"{v.GuidPath}|{v.Serial?.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)}").Order(StringComparer.Ordinal).ToList(),
    };

    public bool SameAs(DiskIdentity other) =>
        Number == other.Number
        && string.Equals(DevicePath, other.DevicePath, StringComparison.OrdinalIgnoreCase)
        && Size == other.Size
        && Bus == other.Bus
        && Vendor == other.Vendor
        && Product == other.Product
        && Serial == other.Serial
        && MediaChangeCount == other.MediaChangeCount
        && Volumes.SequenceEqual(other.Volumes, StringComparer.OrdinalIgnoreCase);
}

public static class DiskRules
{
    /// <summary>TF cards are rarely larger: a bigger disk is probably an external drive.</summary>
    public const long LargeCard = 256_000_000_000;

    /// <summary>
    /// Why the disk may not be written, or null when it may: only USB, SD or MMC disks, never one
    /// holding Windows or the system, and large enough for the image.
    /// </summary>
    public static string? Refusal(DiskInfo disk, long imageBytes = 0)
    {
        if (disk.HoldsSystem) return "holds the system";
        if (disk.Bus is not (DiskBus.Usb or DiskBus.Sd or DiskBus.Mmc)) return "is not a USB or SD card reader";
        if (disk.Size <= 0) return "has no media";
        if (disk.BytesPerSector is not (512 or 4096)) return $"has {disk.BytesPerSector}-byte sectors";
        if (imageBytes > disk.Size) return "is smaller than the image";
        return null;
    }
}
