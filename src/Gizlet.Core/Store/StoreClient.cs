using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Gizlet.Core.Packages;

namespace Gizlet.Core.Store;

/// <summary>The Gizlet Store API (<c>gizlet.io/api/v1</c>), asked for packages this machine runs.</summary>
public sealed class StoreClient(HttpClient http, WindowsTarget target, Uri? api = null)
{
    public static readonly Uri DefaultApi = new(
        Environment.GetEnvironmentVariable("GIZLET_STORE_API") is { Length: > 0 } url ? url.TrimEnd('/') + "/" : "https://gizlet.io/api/");

    readonly Uri api = api ?? DefaultApi;

    /// <summary>Desktops run packages of either tier; the Store falls back to lite when there is no full one.</summary>
    public const string Tier = "full";

    public WindowsTarget Target => target;

    /// <summary>The target to filter by on the server, when this machine runs only one architecture.</summary>
    string? ServerTarget => target.Architectures.Count == 1 ? target.Preferred : null;

    public async Task<StoreFeatured> FeaturedAsync(CancellationToken cancellationToken = default)
    {
        var featured = await GetAsync<StoreFeatured>($"v1/featured?kind=app{TargetQuery()}", cancellationToken).ConfigureAwait(false);
        return featured with
        {
            Hero = featured.Hero.Where(Runs).ToList(),
            Sections = featured.Sections
                .Select(s => s with { Gizlets = s.Gizlets.Where(Runs).ToList() })
                .Where(s => s.Gizlets.Count > 0)
                .ToList(),
        };
    }

    public async Task<StoreSearch> SearchAsync(string query, string? cursor = null, int limit = 24, CancellationToken cancellationToken = default)
    {
        var path = $"v1/gizlets?kind=app&q={Uri.EscapeDataString(query.Trim())}&limit={limit}{TargetQuery()}";
        if (cursor is not null) path += $"&cursor={Uri.EscapeDataString(cursor)}";
        var search = await GetAsync<StoreSearch>(path, cancellationToken).ConfigureAwait(false);
        return search with { Gizlets = search.Gizlets.Where(Runs).ToList() };
    }

    public Task<StoreGizlet> GizletAsync(string repo, CancellationToken cancellationToken = default) =>
        GetAsync<StoreGizlet>($"v1/gizlets/{RepoPath(repo)}", cancellationToken);

    /// <summary>Whether the gizlet has a package for this machine.</summary>
    public bool Runs(StoreGizlet gizlet) => gizlet.Kind == "app" && gizlet.Targets.Any(target.Runs);

    /// <summary>The package to install: the best target the gizlet has for this machine.</summary>
    public async Task<StoreInstall> InstallAsync(StoreGizlet gizlet, CancellationToken cancellationToken = default)
    {
        var triple = target.Triples.FirstOrDefault(gizlet.Targets.Contains)
            ?? throw new GizletException(GizletError.StoreNoPackage, gizlet.Repo);
        var install = await GetAsync<StoreInstall>(
            $"v1/gizlets/{RepoPath(gizlet.Repo)}/install?target={Uri.EscapeDataString(triple)}&tier={Tier}", cancellationToken).ConfigureAwait(false);
        if (install.Package is not { Url: not null, Sha256: not null, Size: not null }) throw new GizletException(GizletError.StoreNotReady, gizlet.Repo);
        if (install.Kind != "app") throw new GizletException(GizletError.PackageService, install.Id);
        return install;
    }

    /// <summary>Update status for Store installs, per target they were installed for.</summary>
    public async Task<IReadOnlyList<StoreUpdate>> UpdatesAsync(IEnumerable<(StoreInstalled App, string Target)> installed, CancellationToken cancellationToken = default)
    {
        var results = new List<StoreUpdate>();
        foreach (var group in installed.GroupBy(i => i.Target))
        {
            foreach (var chunk in group.Select(i => i.App).Chunk(200))
            {
                var body = new
                {
                    target = group.Key,
                    tier = Tier,
                    installed = chunk.Select(a => new { repo = a.Repo, repository_id = a.RepositoryId, id = a.Id, version = a.Version }),
                };
                using var response = await http.PostAsJsonAsync(new Uri(api, "v1/updates"), body, JsonFile.Options, cancellationToken).ConfigureAwait(false);
                await EnsureSuccess(response).ConfigureAwait(false);
                var answer = await response.Content.ReadFromJsonAsync<UpdatesAnswer>(JsonFile.Options, cancellationToken).ConfigureAwait(false);
                results.AddRange(answer?.Gizlets ?? []);
            }
        }
        return results;
    }

    /// <summary>
    /// Downloads a package into <paramref name="directory"/>, checking its size and SHA-256 as the
    /// Store gave them. Returns the file's path; a mismatch leaves nothing behind.
    /// </summary>
    public async Task<string> DownloadAsync(StorePackage package, string directory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (package is not { Url: { } url, Sha256: { } sha256, Size: { } size }) throw new GizletException(GizletError.StoreNotReady, package.File);
        if (size > PackageReader.MaxPackageBytes) throw new GizletException(GizletError.PackageTooLarge, $"{size} bytes");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.gzl");
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await EnsureSuccess(response).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > size) throw new GizletException(GizletError.PackageDownloadMismatch, $"more than {size} bytes");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((double)received / size);
                }
                if (received != size) throw new GizletException(GizletError.PackageDownloadMismatch, $"{received} of {size} bytes");
            }
            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new GizletException(GizletError.PackageDownloadMismatch, "SHA-256");
            }
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    string TargetQuery() => ServerTarget is { } t ? $"&target={Uri.EscapeDataString(t)}&tier={Tier}" : "";

    /// <summary><c>github.com/owner/repo</c>, checked, as an API path.</summary>
    internal static string RepoPath(string repo)
    {
        var parts = repo.Split('/');
        if (parts.Length != 3 || parts[0] != "github.com" || parts.Skip(1).Any(p => p.Length == 0 || p is "." or ".." || !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new GizletException(GizletError.StoreUnavailable, $"not a repository: {repo}");
        }
        return string.Join('/', parts.Select(Uri.EscapeDataString));
    }

    async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(new Uri(api, path), cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new GizletException(GizletError.StoreUnavailable, e.Message, e);
        }
        using (response)
        {
            await EnsureSuccess(response).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<T>(JsonFile.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new GizletException(GizletError.StoreUnavailable, $"empty answer for {path}");
        }
    }

    static async Task EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new GizletException(GizletError.StoreNoPackage, body),
            HttpStatusCode.Conflict => new GizletException(GizletError.StoreNotReady, body),
            _ => new GizletException(GizletError.StoreUnavailable, $"{(int)response.StatusCode}: {body}"),
        };
    }

    sealed record UpdatesAnswer
    {
        public List<StoreUpdate> Gizlets { get; init; } = [];
    }
}
