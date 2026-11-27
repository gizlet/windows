using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Gizlet.Core.Packages;

namespace Gizlet.Core.Tests;

/// <summary>Builds <c>.gzl</c> packages for tests.</summary>
sealed class TestPackage
{
    public string Id { get; set; } = "io.gizlet.test";
    public string Version { get; set; } = "1.0.0";
    public string Target { get; set; } = "x86_64-pc-windows-msvc";
    public string Kind { get; set; } = "app";
    public string Entry { get; set; } = "bin/test.exe";
    public ushort Machine { get; set; } = WindowsTarget.MachineAmd64;
    public Dictionary<string, byte[]> Files { get; } = [];
    public List<string> Unlisted { get; } = [];
    public Dictionary<string, string> WrongChecksums { get; } = [];
    public bool OmitChecksums { get; set; }
    public Dictionary<string, int> UnixModes { get; } = [];

    public static byte[] Pe(ushort machine, ushort characteristics = 0x0022)
    {
        var image = new byte[512];
        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(image.AsSpan(0x3C), 0x80);
        "PE\0\0"u8.CopyTo(image.AsSpan(0x80));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x84), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x80 + 22), characteristics);
        return image;
    }

    public string Manifest() =>
        $$"""{"schema":2,"kind":"{{Kind}}","id":"{{Id}}","name":"Test","names":{"zh-CN":"测试"},"version":"{{Version}}","runtime":1,"target":"{{Target}}","tier":"full","entry":"{{Entry}}","icon":"icon.png"}""";

    public byte[] Build()
    {
        var files = new Dictionary<string, byte[]>(Files);
        files.TryAdd("manifest.json", Encoding.UTF8.GetBytes(Manifest()));
        files.TryAdd(Entry, Pe(Machine));
        files.TryAdd("icon.png", [0x89, (byte)'P', (byte)'N', (byte)'G']);
        var checksums = new StringBuilder();
        foreach (var (path, data) in files)
        {
            if (Unlisted.Contains(path)) continue;
            var hash = WrongChecksums.TryGetValue(path, out var wrong) ? wrong : Convert.ToHexStringLower(SHA256.HashData(data));
            checksums.Append(hash).Append("  ").Append(path).Append('\n');
        }
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, data) in files) Add(zip, path, data);
            if (!OmitChecksums) Add(zip, "CHECKSUMS", Encoding.UTF8.GetBytes(checksums.ToString()));
        }
        return output.ToArray();
    }

    void Add(ZipArchive zip, string path, byte[] data)
    {
        var entry = zip.CreateEntry(path);
        if (UnixModes.TryGetValue(path, out var mode)) entry.ExternalAttributes = mode << 16;
        using var stream = entry.Open();
        stream.Write(data);
    }

    public string WriteTo(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Id}-{Version}-{Guid.NewGuid():N}.gzl");
        File.WriteAllBytes(path, Build());
        return path;
    }
}

sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gizlet-tests-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    public static string Text(string name) => File.ReadAllText(Path(name));
    public static byte[] Bytes(string name) => File.ReadAllBytes(Path(name));
}
