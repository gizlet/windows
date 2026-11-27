using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Gizlet.Core.Packages;

/// <summary>A package unpacked into a folder and checked: every rule below held.</summary>
public sealed record UnpackedPackage(PackageManifest Manifest, string Directory, long Bytes);

/// <summary>
/// Unpacks a <c>.gzl</c> into a new folder and checks it before anything installed is touched:
/// canonical entry paths that stay inside the folder and do not collide on Windows, no links,
/// devices or encryption, size limits, <c>CHECKSUMS</c> covering exactly the other files, a
/// readable app manifest for a target this machine runs, and an entry that is a PE executable
/// for that target's machine.
/// </summary>
public static class PackageReader
{
    public const long MaxPackageBytes = 1L << 30;
    public const long MaxUnpackedBytes = 4L << 30;
    public const int MaxEntries = 50_000;
    const string ManifestName = "manifest.json";
    const string ChecksumsName = "CHECKSUMS";

    public static UnpackedPackage Unpack(string packagePath, string directory, WindowsTarget target, CancellationToken cancellationToken = default)
    {
        using var file = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Unpack(file, directory, target, cancellationToken);
    }

    public static UnpackedPackage Unpack(Stream package, string directory, WindowsTarget target, CancellationToken cancellationToken = default)
    {
        if (package.CanSeek && package.Length > MaxPackageBytes) throw new GizletException(GizletError.PackageTooLarge, $"{package.Length} bytes");
        if (System.IO.Directory.Exists(directory) || File.Exists(directory)) throw new IOException($"{directory} already exists");
        System.IO.Directory.CreateDirectory(directory);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true, Encoding.UTF8);
        }
        catch (InvalidDataException e)
        {
            throw new GizletException(GizletError.PackageUnreadable, e.Message, e);
        }
        using (archive)
        {
            var files = CheckEntries(archive);
            if (!files.ContainsKey(ManifestName)) throw new GizletException(GizletError.PackageNoManifest, ManifestName);
            if (!files.ContainsKey(ChecksumsName)) throw new GizletException(GizletError.PackageChecksums, $"{ChecksumsName} is missing");
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            long total = 0;
            var buffer = new byte[1 << 16];
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (path, isDirectory) = Split(entry.FullName);
                var full = PackagePath.Resolve(directory, path);
                if (isDirectory)
                {
                    System.IO.Directory.CreateDirectory(full);
                    continue;
                }
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                try
                {
                    using var input = entry.Open();
                    using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                    long written = 0;
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        written += read;
                        total += read;
                        if (written > entry.Length || total > MaxUnpackedBytes) throw new GizletException(GizletError.PackageTooLarge, path);
                        hash.AppendData(buffer, 0, read);
                        output.Write(buffer, 0, read);
                    }
                    if (written != entry.Length) throw new GizletException(GizletError.PackageUnreadable, $"{path} is cut short");
                }
                catch (InvalidDataException e)
                {
                    throw new GizletException(GizletError.PackageUnreadable, $"{path}: {e.Message}", e);
                }
                hashes[path] = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            CheckChecksums(File.ReadAllBytes(PackagePath.Resolve(directory, ChecksumsName)), hashes);
            var manifest = PackageManifest.Parse(File.ReadAllBytes(PackagePath.Resolve(directory, ManifestName)));
            if (manifest.Kind != "app") throw new GizletException(GizletError.PackageService, manifest.Id);
            var machine = target.Machine(manifest.Target)
                ?? throw new GizletException(GizletError.PackageWrongPlatform, manifest.Target);
            if (!hashes.ContainsKey(manifest.Entry)) throw new GizletException(GizletError.PackageNoEntry, manifest.Entry);
            var head = new byte[4096];
            int filled;
            using (var entryFile = File.OpenRead(PackagePath.Resolve(directory, manifest.Entry)))
            {
                filled = entryFile.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            }
            var found = PortableExecutable.Machine(head.AsSpan(0, filled));
            if (found != machine)
            {
                throw new GizletException(GizletError.PackageEntryNotExecutable,
                    found is null ? $"{manifest.Entry} is not a Windows executable" : $"{manifest.Entry} is for machine 0x{found:x4}, not 0x{machine:x4}");
            }
            if (manifest.Icon is not null && !hashes.ContainsKey(manifest.Icon)) manifest = manifest with { Icon = null };
            return new UnpackedPackage(manifest, directory, total);
        }
    }

    static (string Path, bool IsDirectory) Split(string name) =>
        name.EndsWith('/') ? (name[..^1], true) : (name, false);

    /// <summary>The package's files by path, after checking every entry's name and type.</summary>
    static Dictionary<string, ZipArchiveEntry> CheckEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries) throw new GizletException(GizletError.PackageTooLarge, $"{archive.Entries.Count} entries");
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        var keys = new Dictionary<string, bool>(StringComparer.Ordinal);
        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            var (path, isDirectory) = Split(entry.FullName);
            if (!PackagePath.IsValid(path)) throw new GizletException(GizletError.PackageBadPath, entry.FullName);
            if (entry.IsEncrypted) throw new GizletException(GizletError.PackageEncrypted, path);
            // Unix file types in the high half of the external attributes.
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type == 0xA000) throw new GizletException(GizletError.PackageLink, path);
            if (type != 0 && type != (isDirectory ? 0x4000 : 0x8000)) throw new GizletException(GizletError.PackageBadPath, $"{path} is not a regular file");
            if (isDirectory && entry.Length != 0) throw new GizletException(GizletError.PackageBadPath, entry.FullName);
            var key = PackagePath.CollisionKey(path);
            if (keys.TryGetValue(key, out var wasDirectory))
            {
                if (isDirectory && wasDirectory) continue;
                throw new GizletException(GizletError.PackageDuplicatePath, path);
            }
            keys[key] = isDirectory;
            if (isDirectory) continue;
            declared += entry.Length;
            if (declared > MaxUnpackedBytes) throw new GizletException(GizletError.PackageTooLarge, $"{declared} bytes unpacked");
            files[path] = entry;
        }
        // A file may not sit where another entry needs a folder.
        foreach (var path in files.Keys)
        {
            for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                if (keys.TryGetValue(PackagePath.CollisionKey(path[..slash]), out var isDirectory) && !isDirectory)
                {
                    throw new GizletException(GizletError.PackageDuplicatePath, path);
                }
            }
        }
        return files;
    }

    /// <summary>
    /// <c>CHECKSUMS</c> lists the SHA-256 of every other file, one <c>&lt;hex&gt;  &lt;path&gt;</c> line each, and nothing else.
    /// </summary>
    internal static void CheckChecksums(byte[] checksums, Dictionary<string, string> hashes)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(checksums);
        }
        catch (DecoderFallbackException)
        {
            throw new GizletException(GizletError.PackageChecksums, "not UTF-8");
        }
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.Length < 67 || line[64] != ' ' || (line[65] != ' ' && line[65] != '*'))
            {
                throw new GizletException(GizletError.PackageChecksums, $"malformed line: {line}");
            }
            var hex = line[..64];
            if (!hex.All(Uri.IsHexDigit)) throw new GizletException(GizletError.PackageChecksums, $"malformed line: {line}");
            var path = line[66..];
            if (!listed.TryAdd(path, hex.ToLowerInvariant())) throw new GizletException(GizletError.PackageChecksums, $"{path} is listed twice");
        }
        foreach (var (path, hash) in hashes)
        {
            if (path == ChecksumsName) continue;
            if (!listed.Remove(path, out var expected)) throw new GizletException(GizletError.PackageChecksums, $"{path} is not listed");
            if (expected != hash) throw new GizletException(GizletError.PackageChecksums, $"{path} does not match");
        }
        if (listed.Count > 0) throw new GizletException(GizletError.PackageChecksums, $"{listed.Keys.First()} is listed but missing");
    }
}
