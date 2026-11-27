namespace Gizlet.Core.Flash;

/// <summary>
/// Grows the R36S image's ext4 data filesystem offline. A port of <c>GizletDataGrow</c> in
/// gizlet/os's <c>boards/gameconsole/r36s/flash.ps1</c> (and <c>grown_data</c> in its flash.sh), which
/// explains the layout: 4 KiB blocks, 32768 blocks per group, <c>meta_bg</c> without
/// <c>resize_inode</c>, so growing only adds metadata in the new groups and never moves a block.
/// </summary>
static class Ext4Grow
{
    const long Bpg = 32768;
    static readonly uint[] Table = MakeTable();

    static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var x = i;
            for (var j = 0; j < 8; j++) x = (x & 1) != 0 ? (x >> 1) ^ 0x82F63B78u : x >> 1;
            table[i] = x;
        }
        return table;
    }

    static uint Crc(uint c, byte[] b, int off, int len)
    {
        for (var i = 0; i < len; i++) c = Table[(c ^ b[off + i]) & 255] ^ (c >> 8);
        return c;
    }

    static long Get(byte[] b, int off, int size)
    {
        long r = 0;
        for (var i = size - 1; i >= 0; i--) r = (r << 8) | b[off + i];
        return r;
    }

    static void Put(byte[] b, int off, int size, long v)
    {
        for (var i = 0; i < size; i++) b[off + i] = (byte)(v >> (8 * i));
    }

    // Groups 0 and 1 and the powers of 3, 5 and 7 below the group count, in the order grown_data takes them.
    static List<long> Supers(long groups)
    {
        var s = new List<long> { 0, 1 };
        foreach (var p in new long[] { 3, 5, 7 })
        {
            for (var n = p; n < groups; n *= p) s.Add(n);
        }
        return s;
    }

    static long Overhead(long g, long meta, long itb, List<long> supers, out long hs, out long gd)
    {
        hs = supers.Contains(g) ? 1 : 0;
        if (g < meta * 64)
        {
            gd = hs * meta;
        }
        else
        {
            var m = g % 64;
            gd = m is 0 or 1 or 63 ? 1 : 0;
        }
        return hs + gd + 2 + itb;
    }

    static void Add(List<CardLayout.Block> list, long cardBlock, byte[] from, int index)
    {
        var block = new byte[4096];
        Array.Copy(from, index * 4096, block, 0, 4096);
        list.Add(new CardLayout.Block(cardBlock * 4096, block));
    }

    public static List<CardLayout.Block> Blocks(byte[] head, long startSector, long sectors)
    {
        var list = new List<CardLayout.Block>();
        if (startSector % 8 != 0 || head.Length < 8192) return list;
        var bas = startSector / 8;
        var sb = new byte[1024];
        Array.Copy(head, 1024, sb, 0, 1024);
        long incompat = Get(sb, 0x60, 4), ro = Get(sb, 0x64, 4);
        if (Get(sb, 0x38, 2) != 0xEF53 || Get(sb, 0x18, 4) != 2 || Get(sb, 0x14, 4) != 0) return list;
        if (Get(sb, 0x20, 4) != Bpg || Get(sb, 0x24, 4) != Bpg || Get(sb, 0x04, 4) != Bpg || Get(sb, 0x150, 4) != 0) return list;
        if ((Get(sb, 0x5C, 4) & 0x210) != 0) return list;
        if ((incompat & 0x290) != 0x290 || (incompat & ~0x22D2L) != 0) return list;
        if ((ro & 0x401) != 0x401 || (ro & 0x210) != 0) return list;
        if (Get(sb, 0xFE, 2) != 64 || Get(sb, 0xCE, 2) != 0 || Get(sb, 0x3A, 2) != 1) return list;
        var meta = Get(sb, 0x104, 4);
        if (meta > 1) return list;
        long ipg = Get(sb, 0x28, 4), isz = Get(sb, 0x58, 2);
        if (ipg <= 0 || ipg >= 65536 || ipg * isz % 4096 != 0) return list;
        var itb = ipg * isz / 4096;
        var seed = (incompat & 0x2000) != 0 ? (uint)Get(sb, 0x270, 4) : Crc(0xFFFFFFFFu, sb, 0x68, 16);
        var part = sectors / 8;
        var groups = (part + Bpg - 1) / Bpg;
        if (groups <= 1) return list;
        var supers = Supers(groups);
        long hs, gd;
        var last = part - (groups - 1) * Bpg;
        if (last < Overhead(groups - 1, meta, itb, supers, out hs, out gd) + 50)
        {
            groups--;
            last = Bpg;
        }
        if (groups <= 1) return list;
        var blocks = (groups - 1) * Bpg + last;
        var descBlocks = (groups + 63) / 64;
        var gdt = new byte[descBlocks * 4096];
        Array.Copy(head, 4096, gdt, 0, 64);
        var bitmap = new byte[4096];
        long freeAdd = 0, overAdd = 0;
        for (long g = 1; g < groups; g++)
        {
            var ov = Overhead(g, meta, itb, supers, out hs, out gd);
            long bb = g * Bpg + hs + gd, free, flags;
            uint csum = 0;
            if (g == groups - 1)
            {
                free = last - ov;
                flags = 1;
                for (var i = 0; i < 4096; i++)
                {
                    long lo = i * 8, v;
                    if (lo + 8 <= ov || lo >= last)
                    {
                        v = 255;
                    }
                    else if (lo >= ov && lo + 8 <= last)
                    {
                        v = 0;
                    }
                    else
                    {
                        v = 0;
                        if (lo + 8 > last) v = 255 & ~((1L << (int)(last - lo)) - 1);
                        if (lo < ov) v |= (1L << (int)(ov - lo)) - 1;
                    }
                    bitmap[i] = (byte)v;
                }
                csum = Crc(seed, bitmap, 0, 4096);
            }
            else
            {
                free = Bpg - ov;
                flags = 3;
            }
            freeAdd += free;
            overAdd += ov;
            var at = (int)(g * 64);
            Put(gdt, at, 4, bb);
            Put(gdt, at + 4, 4, bb + 1);
            Put(gdt, at + 8, 4, bb + 2);
            Put(gdt, at + 12, 2, free);
            Put(gdt, at + 14, 2, ipg);
            Put(gdt, at + 18, 2, flags);
            Put(gdt, at + 24, 2, csum & 0xFFFF);
            Put(gdt, at + 28, 2, ipg);
            Put(gdt, at + 56, 2, csum >> 16);
            var number = new byte[4];
            Put(number, 0, 4, g);
            Put(gdt, at + 30, 2, Crc(Crc(seed, number, 0, 4), gdt, at, 64) & 0xFFFF);
        }
        for (long k = 0; k < descBlocks; k++)
        {
            if (k < meta)
            {
                foreach (var g in supers)
                {
                    if (g < groups && g < meta * 64) Add(list, bas + g * Bpg + 1 + k, gdt, (int)k);
                }
            }
            else
            {
                foreach (var g in new[] { k * 64, k * 64 + 1, k * 64 + 63 })
                {
                    if (g >= groups) continue;
                    Overhead(g, meta, itb, supers, out hs, out gd);
                    Add(list, bas + g * Bpg + hs, gdt, (int)k);
                }
            }
        }
        Overhead(groups - 1, meta, itb, supers, out hs, out gd);
        Add(list, bas + (groups - 1) * Bpg + hs + gd, bitmap, 0);
        var d = (byte[])sb.Clone();
        Put(d, 0x08, 4, Get(sb, 0x08, 4) * blocks / Bpg);
        Put(d, 0x154, 4, 0);
        Put(d, 0x04, 4, blocks);
        Put(d, 0x150, 4, 0);
        Put(d, 0x00, 4, groups * ipg);
        Put(d, 0x10, 4, Get(sb, 0x10, 4) + (groups - 1) * ipg);
        Put(d, 0x0C, 4, Get(sb, 0x0C, 4) + freeAdd);
        Put(d, 0x158, 4, 0);
        if (Get(sb, 0x248, 4) != 0) Put(d, 0x248, 4, Get(sb, 0x248, 4) + overAdd);
        var order = new List<long>(supers);
        order.RemoveAt(0);
        order.Add(0);
        foreach (var g in order)
        {
            if (g >= groups) continue;
            Put(d, 0x5A, 2, g & 0xFFFF);
            Put(d, 0x3FC, 4, Crc(0xFFFFFFFFu, d, 0, 1020));
            var block = new byte[4096];
            if (g == 0)
            {
                // The primary superblock sits 1 KiB into block 0.
                Array.Copy(head, 0, block, 0, 4096);
                Array.Copy(d, 0, block, 1024, 1024);
            }
            else
            {
                Array.Copy(d, 0, block, 0, 1024);
            }
            Add(list, bas + g * Bpg, block, 0);
        }
        return list;
    }
}
