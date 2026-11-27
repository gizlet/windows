using System.Text.RegularExpressions;

namespace Gizlet.Core.Flash;

/// <summary>The two R36S LCD panels Gizlet OS drives.</summary>
public enum R36sPanel
{
    /// <summary>Panel 0, a Sitronix ST7703, on early V12 boards: the image's default.</summary>
    Original,
    /// <summary>Panel 4, a NewVision NV3051D, on later V12 and V21, V22 and V30 boards.</summary>
    V30Panel4,
}

/// <summary>How the panel was found on the card.</summary>
public enum PanelSource
{
    /// <summary>A stock card's kernel dtb names the controller: certain.</summary>
    Stock,
    /// <summary>A Gizlet OS card says what it was written with, which may be the mistake being fixed.</summary>
    Gizlet,
}

public sealed record PanelGuess(R36sPanel Panel, PanelSource Source);

/// <summary>
/// Tells the panel from the card being written, as <c>Get-CardPanel</c> in gizlet/os's R36S
/// <c>flash.ps1</c> does, by reading its FAT boot partition through a mounted folder.
/// </summary>
public static partial class PanelDetector
{
    public const string OverlayFile = "mipi-panel.dtbo";
    public const string Panel4Overlay = "r36s-v30-panel4.dtbo";

    [GeneratedRegex("^[A-Za-z0-9_-][A-Za-z0-9._-]*$")]
    private static partial Regex DtbName();

    public static string Id(R36sPanel panel) => panel == R36sPanel.V30Panel4 ? "v30-panel4" : "original";

    /// <summary>
    /// The panel a kernel dtb's DSI panel-init-sequence starts for, in Rockchip's format (type, delay,
    /// length, payload) right after the property's 12-byte header: NV3051D's page select
    /// <c>FF 30, FF 52, FF 01</c> is Panel 4, ST7703's <c>B9 F1 12 83</c> is Panel 0. Null for another
    /// panel, such as the R36S Plus, whose ST7703 sequence starts with a sleep-out.
    /// </summary>
    public static R36sPanel? FromDtb(ReadOnlySpan<byte> dtb)
    {
        var nv = Contains(dtb, [0x15, 0x00, 0x02, 0xFF, 0x30, 0x15, 0x00, 0x02, 0xFF, 0x52, 0x15, 0x00, 0x02, 0xFF, 0x01]);
        var st = Contains(dtb, [0x39, 0x00, 0x04, 0xB9, 0xF1, 0x12, 0x83]);
        return (nv, st) switch
        {
            (true, false) => R36sPanel.V30Panel4,
            (false, true) => R36sPanel.Original,
            _ => null,
        };
    }

    // The payload follows 00 00 00 03 and eight more header bytes (the length and name offset).
    static bool Contains(ReadOnlySpan<byte> data, ReadOnlySpan<byte> payload)
    {
        for (var i = 0; i + 12 + payload.Length <= data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 3 && data.Slice(i + 12, payload.Length).SequenceEqual(payload)) return true;
        }
        return false;
    }

    /// <summary>
    /// The panel the card whose boot partition is at <paramref name="boot"/> was made for. A stock card's
    /// <c>boot.ini</c> loads the kernel dtb that describes the unit, and every dtb it loads must agree.
    /// A Gizlet OS card has <c>mipi-panel.dtbo</c>, a copy of the Panel 4 overlay, when written for Panel 4.
    /// </summary>
    public static PanelGuess? FromBootPartition(string boot, bool gizlet)
    {
        try
        {
            if (gizlet)
            {
                var overlay = Path.Combine(boot, OverlayFile);
                if (!File.Exists(overlay)) return new PanelGuess(R36sPanel.Original, PanelSource.Gizlet);
                var panel4 = Path.Combine(boot, Panel4Overlay);
                return File.Exists(panel4) && File.ReadAllBytes(overlay).AsSpan().SequenceEqual(File.ReadAllBytes(panel4))
                    ? new PanelGuess(R36sPanel.V30Panel4, PanelSource.Gizlet)
                    : null;
            }
            var ini = Path.Combine(boot, "boot.ini");
            if (!File.Exists(ini)) return null;
            R36sPanel? found = null;
            foreach (var line in File.ReadLines(ini))
            {
                var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5 || f[0] is not ("load" or "fatload") || !f[3].Contains("dtb_loadaddr", StringComparison.Ordinal)) continue;
                if (!DtbName().IsMatch(f[4])) return null;
                var dtb = Path.Combine(boot, f[4]);
                if (!File.Exists(dtb) || new FileInfo(dtb).Length > 4 << 20) return null;
                var panel = FromDtb(File.ReadAllBytes(dtb));
                if (panel is null || (found is not null && found != panel)) return null;
                found = panel;
            }
            return found is { } p ? new PanelGuess(p, PanelSource.Stock) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
