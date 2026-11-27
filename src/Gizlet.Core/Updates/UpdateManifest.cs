using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gizlet.Core.Signing;

namespace Gizlet.Core.Updates;

public sealed record UpdateInstaller
{
    public string Path { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
}

/// <summary>
/// A Launcher release, as the Release workflow signs it into <c>windows/update.json</c>:
/// <c>{"manifest": "&lt;this JSON as text&gt;", "signature": "&lt;ssh-keygen -Y sign output&gt;"}</c>.
/// </summary>
public sealed record UpdateManifest
{
    public const string Product = "gizlet-windows";
    public const string Namespace = "gizlet-windows-update";

    /// <summary>
    /// The public half of the update signing key, the same as <c>devops/update-key.pub</c>. Its
    /// private half is only in <c>devops/secrets/release.enc.yaml</c>, for the Release workflow.
    /// </summary>
    public const string TrustedKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOwT97syJvu38qFyVCtvTnsjP6VU17q9gdTt8YCy8y2c gizlet-windows-update";

    public int Schema { get; init; }
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Commit { get; init; } = "";
    public DateTimeOffset PublishedAt { get; init; }
    public Dictionary<string, string> Notes { get; init; } = [];
    public Dictionary<string, UpdateInstaller> Installers { get; init; } = [];

    public string NotesIn(string language) =>
        Notes.TryGetValue(language, out var notes) ? notes : Notes.GetValueOrDefault("en", "");
}

/// <summary>A newer Launcher than this one, with the installer for this machine.</summary>
public sealed record LauncherUpdate(UpdateManifest Manifest, SemVersion Version, UpdateInstaller Installer, Uri InstallerUrl);

/// <summary>
/// Reads the signed update manifest, bypassing caches, and downloads its installer. Nothing is
/// offered unless the signature verifies with the trusted key and the version is newer.
/// </summary>
public sealed partial class UpdateChecker(HttpClient http, SemVersion current, string architecture, SshPublicKey trusted, Uri? manifestUrl = null)
{
    public static readonly Uri DefaultManifest = new(
        Environment.GetEnvironmentVariable("GIZLET_UPDATE_URL") is { Length: > 0 } url ? url : "https://gizlet.io/assets/windows/update.json");

    readonly Uri manifestUrl = manifestUrl ?? DefaultManifest;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,199}$")]
    private static partial Regex FileName();

    public SemVersion Current => current;

    public async Task<LauncherUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new GizletException(GizletError.UpdateBadManifest, $"{(int)response.StatusCode} from {manifestUrl}");
        var envelope = await response.Content.ReadFromJsonAsync<Envelope>(JsonFile.Options, cancellationToken).ConfigureAwait(false);
        return Accept(envelope, response.RequestMessage?.RequestUri ?? manifestUrl);
    }

    internal LauncherUpdate? Accept(Envelope? envelope, Uri from)
    {
        if (envelope is not { Manifest: { } text, Signature: { } signature }) throw new GizletException(GizletError.UpdateBadManifest, "no manifest or signature");
        if (!SshSignature.Verify(Encoding.UTF8.GetBytes(text), signature, trusted, UpdateManifest.Namespace))
        {
            throw new GizletException(GizletError.SignatureInvalid, from.ToString());
        }
        UpdateManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(text, JsonFile.Options) ?? throw new JsonException("null");
        }
        catch (JsonException e)
        {
            throw new GizletException(GizletError.UpdateBadManifest, e.Message, e);
        }
        if (manifest.Schema != 1 || manifest.Name != UpdateManifest.Product) throw new GizletException(GizletError.UpdateBadManifest, $"schema {manifest.Schema} of {manifest.Name}");
        if (!SemVersion.TryParse(manifest.Version, out var version) || manifest.Tag != $"v{manifest.Version}")
        {
            throw new GizletException(GizletError.UpdateBadManifest, $"version {manifest.Version}, tag {manifest.Tag}");
        }
        if (version.Value <= current) return null;
        if (!manifest.Installers.TryGetValue(architecture, out var installer)) return null;
        if (!FileName().IsMatch(installer.Path) || installer.Path.Contains("..", StringComparison.Ordinal)
            || installer.Sha256.Length != 64 || !installer.Sha256.All(Uri.IsHexDigit) || installer.Size <= 0)
        {
            throw new GizletException(GizletError.UpdateBadManifest, $"installer {installer.Path}");
        }
        var url = new Uri(manifestUrl, $"releases/{manifest.Tag}/{installer.Path}");
        return new LauncherUpdate(manifest, version.Value, installer, url);
    }

    /// <summary>
    /// Downloads the installer into <paramref name="directory"/>, resuming a partial download, and
    /// returns its path once its size and SHA-256 match the signed manifest.
    /// </summary>
    public async Task<string> DownloadAsync(LauncherUpdate update, string directory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var final = Path.Combine(directory, update.Installer.Path);
        var partial = final + ".partial";
        if (File.Exists(final))
        {
            if (await MatchesAsync(final, update.Installer, cancellationToken).ConfigureAwait(false)) return final;
            File.Delete(final);
        }
        long have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > update.Installer.Size) { File.Delete(partial); have = 0; }
        if (have < update.Installer.Size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, update.InstallerUrl);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new GizletException(GizletError.UpdateDownloadMismatch, $"{(int)response.StatusCode} from {update.InstallerUrl}");
            var append = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            await using var output = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
            if (!append) have = 0;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                have += read;
                if (have > update.Installer.Size) throw new GizletException(GizletError.UpdateDownloadMismatch, "too long");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report((double)have / update.Installer.Size);
            }
        }
        if (!await MatchesAsync(partial, update.Installer, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new GizletException(GizletError.UpdateDownloadMismatch, update.Installer.Path);
        }
        File.Move(partial, final, overwrite: true);
        return final;
    }

    /// <summary>Whether the file at <paramref name="path"/> is the installer the manifest names.</summary>
    public static async Task<bool> MatchesAsync(string path, UpdateInstaller installer, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await MatchesAsync(file, installer, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> MatchesAsync(Stream file, UpdateInstaller installer, CancellationToken cancellationToken = default)
    {
        if (file.Length != installer.Size) return false;
        var hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), installer.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record Envelope
    {
        public string? Manifest { get; init; }
        public string? Signature { get; init; }
    }
}
