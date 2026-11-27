using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gizlet.Core.Signing;

namespace Gizlet.Core.Flash;

/// <summary>An unpacked card image ready to write, with the size and SHA-256 the helper checks again.</summary>
public sealed record CardImage(string Path, long Size, string Sha256, bool Official, string? Tag);

/// <summary>
/// The R36S image of a Gizlet OS release. Releases from gizlet/os#58 on sign the image's own
/// SHA-256 and size; earlier ones only name its <c>SHA256SUMS</c>.
/// </summary>
public sealed record OsRelease(string Tag, Uri Image, Uri Checksums, string? ImageSha256 = null, long? ImageBytes = null);

/// <summary>
/// Gizlet OS releases, found with <c>GET /api/v1/os/releases/latest</c> and served from
/// <c>gizlet.io/assets/releases/</c>. A release's <c>manifest.json</c> is trusted only
/// with a valid <c>manifest.json.sig</c>, made with the Gizlet OS release key under the
/// <c>gizlet-launcher</c> namespace, as the handheld Launcher checks it; its image is then checked
/// against the release's <c>SHA256SUMS</c>.
/// </summary>
public sealed partial class OsImages(HttpClient http, SshPublicKey trusted, string directory, Uri? api = null)
{
    /// <summary>gizlet/os's <c>rootfs/usr/lib/gizlet/launcher/allowed_signers</c>.</summary>
    public const string ReleaseKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIC6BHYSIBbKjF/rNLby8IwmqBpqpQDST9maXBVfmBVzf";
    public const string Namespace = "gizlet-launcher";
    public const long MaxImageBytes = 8L << 30;

    readonly Uri api = api ?? Store.StoreClient.DefaultApi;

    [GeneratedRegex(@"^v(0|[1-9][0-9]{0,8})(\.(0|[1-9][0-9]{0,8})){2}$")]
    private static partial Regex Tag();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex RelativePath();

    public async Task<OsRelease> LatestAsync(CancellationToken cancellationToken = default)
    {
        var latest = await http.GetFromJsonAsync<Latest>(new Uri(api, "v1/os/releases/latest"), JsonFile.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new GizletException(GizletError.ImageBadLayout, "no release");
        if (latest is not { Tag: { } tag, ManifestUrl: { } manifestUrl, SignatureUrl: { } signatureUrl }
            || !Pinned(manifestUrl, tag, "manifest.json") || !Pinned(signatureUrl, tag, "manifest.json.sig"))
        {
            throw new GizletException(GizletError.SignatureInvalid, "the latest release has no signed manifest on the Gizlet server");
        }
        var manifest = await GetBytesAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
        var signature = System.Text.Encoding.UTF8.GetString(await GetBytesAsync(signatureUrl, cancellationToken).ConfigureAwait(false));
        var release = Parse(manifest, signature, manifestUrl);
        if (release.Tag != tag) throw new GizletException(GizletError.SignatureInvalid, $"signed for {release.Tag}, offered as {tag}");
        return release;
    }

    /// <summary>The API's own host, over HTTPS, under <c>/assets/releases/TAG/</c>: release files come from nowhere else.</summary>
    bool Pinned(Uri url, string tag, string? file = null) =>
        url.Scheme == Uri.UriSchemeHttps && string.Equals(url.Host, api.Host, StringComparison.OrdinalIgnoreCase) && url.Port == api.Port
        && url.AbsolutePath.StartsWith($"/assets/releases/{tag}/", StringComparison.Ordinal)
        && (file is null || url.AbsolutePath == $"/assets/releases/{tag}/{file}")
        && string.IsNullOrEmpty(url.Query) && !url.AbsolutePath.Contains("/../", StringComparison.Ordinal);

    void CheckFinal(HttpResponseMessage response, string tag)
    {
        if (response.RequestMessage?.RequestUri is not { } final || !Pinned(final, tag))
        {
            throw new GizletException(GizletError.SignatureInvalid, $"redirected away from the Gizlet server: {response.RequestMessage?.RequestUri}");
        }
    }

    async Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var tag = url.AbsolutePath.Split('/')[3];
        CheckFinal(response, tag);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    sealed record Latest
    {
        public string? Tag { get; init; }
        public Uri? ManifestUrl { get; init; }
        public Uri? SignatureUrl { get; init; }
    }

    internal OsRelease Parse(byte[] manifest, string signature, Uri at)
    {
        if (!SshSignature.Verify(manifest, signature, trusted, Namespace)) throw new GizletException(GizletError.SignatureInvalid, at.ToString());
        using var document = JsonDocument.Parse(manifest);
        var root = document.RootElement;
        if (!root.TryGetProperty("schema_version", out var schema) || schema.GetInt32() != 1) throw new GizletException(GizletError.ImageBadLayout, "release manifest schema");
        var tag = root.GetProperty("tag").GetString() ?? "";
        if (!Tag().IsMatch(tag) || !at.AbsolutePath.EndsWith($"/releases/{tag}/manifest.json", StringComparison.Ordinal))
        {
            throw new GizletException(GizletError.SignatureInvalid, $"tag {tag} at {at}");
        }
        var image = root.GetProperty("boards").GetProperty("gameconsole").GetProperty("r36s").GetProperty("image");
        var imagePath = image.GetProperty("path").GetString() ?? "";
        var checksumPath = image.GetProperty("checksum").GetString() ?? "";
        if (!RelativePath().IsMatch(imagePath) || !RelativePath().IsMatch(checksumPath) || imagePath.Contains("..", StringComparison.Ordinal) || checksumPath.Contains("..", StringComparison.Ordinal))
        {
            throw new GizletException(GizletError.ImageBadLayout, "release paths");
        }
        string? digest = null;
        long? bytes = null;
        if (image.TryGetProperty("sha256", out var sha))
        {
            digest = sha.GetString();
            if (digest is not { Length: 64 } || !digest.All(Uri.IsHexDigit)) throw new GizletException(GizletError.ImageBadLayout, "image sha256");
            digest = digest.ToLowerInvariant();
            if (!image.TryGetProperty("bytes", out var size) || !size.TryGetInt64(out var b) || b <= 0) throw new GizletException(GizletError.ImageBadLayout, "image bytes");
            bytes = b;
        }
        return new OsRelease(tag, new Uri(at, imagePath), new Uri(at, checksumPath), digest, bytes);
    }

    /// <summary>Downloads, checks and unpacks a release's image, reusing an earlier download.</summary>
    public async Task<CardImage> DownloadAsync(OsRelease release, IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken cancellationToken = default)
    {
        var file = release.Image.Segments[^1];
        if (!Pinned(release.Image, release.Tag) || !Pinned(release.Checksums, release.Tag)) throw new GizletException(GizletError.SignatureInvalid, "release files outside the Gizlet server");
        var sums = System.Text.Encoding.UTF8.GetString(await GetBytesAsync(release.Checksums, cancellationToken).ConfigureAwait(false));
        var expected = ChecksumFor(sums, file) ?? throw new GizletException(GizletError.ImageBadChecksum, $"SHA256SUMS does not list {file}");
        // The signed digest, when the release has one, decides; SHA256SUMS must agree with it.
        if (release.ImageSha256 is { } signed && signed != expected) throw new GizletException(GizletError.ImageBadChecksum, "SHA256SUMS differs from the signed manifest");
        var folder = Path.Combine(directory, release.Tag);
        Directory.CreateDirectory(folder);
        var raw = Path.Combine(folder, "card.img");
        var rawSum = raw + ".sha256";
        var packed = Path.Combine(folder, file);
        if (File.Exists(raw) && File.Exists(rawSum) && File.ReadAllText(rawSum).Split(' ') is [var hash, var bytes, var source]
            && source == expected && long.TryParse(bytes, out var size) && new FileInfo(raw).Length == size)
        {
            return new CardImage(raw, size, hash, true, release.Tag);
        }
        await DownloadFileAsync(release.Image, packed, release.Tag, p => progress?.Report(("download", p)), cancellationToken).ConfigureAwait(false);
        string actual;
        await using (var check = File.OpenRead(packed))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancellationToken).ConfigureAwait(false));
        }
        if (actual != expected || (release.ImageBytes is { } signedBytes && new FileInfo(packed).Length != signedBytes))
        {
            File.Delete(packed);
            throw new GizletException(GizletError.ImageBadChecksum, file);
        }
        var image = await UnpackAsync(packed, raw, p => progress?.Report(("unpack", p)), cancellationToken).ConfigureAwait(false);
        File.WriteAllText(rawSum, $"{image.Sha256} {image.Size} {expected}");
        File.Delete(packed);
        return image with { Official = true, Tag = release.Tag };
    }

    /// <summary>A local <c>.img</c>, hashed where it is, or an <c>.img.gz</c>, unpacked into the cache.</summary>
    public async Task<CardImage> LocalAsync(string path, IProgress<(string Stage, double Fraction)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.Combine(directory, "local");
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            Directory.CreateDirectory(folder);
            return await UnpackAsync(path, Path.Combine(folder, "card.img"), p => progress?.Report(("unpack", p)), cancellationToken).ConfigureAwait(false);
        }
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaxImageBytes) throw new GizletException(GizletError.ImageTooLarge, $"{file.Length} bytes");
        var hash = await HashAsync(file, file.Length, p => progress?.Report(("check", p)), cancellationToken).ConfigureAwait(false);
        return new CardImage(Path.GetFullPath(path), file.Length, hash, false, null);
    }

    internal static string? ChecksumFor(string sums, string file)
    {
        foreach (var line in sums.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), @"^([0-9a-f]{64}) +\*?(.+?)\s*$");
            if (match.Success && match.Groups[2].Value == file) return match.Groups[1].Value;
        }
        return null;
    }

    async Task DownloadFileAsync(Uri url, string path, string tag, Action<double> progress, CancellationToken cancellationToken)
    {
        var partial = path + ".partial";
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        CheckFinal(response, tag);
        var append = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!append) have = 0;
        var total = have + (response.Content.Headers.ContentLength ?? 0);
        if (total > MaxImageBytes) throw new GizletException(GizletError.ImageTooLarge, $"{total} bytes");
        await using (var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                have += read;
                if (have > MaxImageBytes) throw new GizletException(GizletError.ImageTooLarge, $"{have} bytes");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                if (total > 0) progress((double)have / total);
            }
        }
        File.Move(partial, path, overwrite: true);
    }

    static async Task<CardImage> UnpackAsync(string packed, string raw, Action<double> progress, CancellationToken cancellationToken)
    {
        var temporary = raw + ".partial";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        try
        {
            await using (var input = new FileStream(packed, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    if (size > MaxImageBytes) throw new GizletException(GizletError.ImageTooLarge, $"over {MaxImageBytes} bytes unpacked");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress((double)input.Position / input.Length);
                }
            }
            File.Move(temporary, raw, overwrite: true);
        }
        catch (InvalidDataException e)
        {
            throw new GizletException(GizletError.ImageBadLayout, $"not a gzip file: {e.Message}", e);
        }
        finally
        {
            File.Delete(temporary);
        }
        return new CardImage(raw, size, Convert.ToHexStringLower(hash.GetHashAndReset()), false, null);
    }

    static async Task<string> HashAsync(Stream stream, long total, Action<double> progress, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        long done = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            if (total > 0) progress((double)done / total);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
