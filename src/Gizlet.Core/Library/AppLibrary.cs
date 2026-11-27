using Gizlet.Core.Packages;

namespace Gizlet.Core.Library;

/// <summary>Where a Store install came from: only that GitHub repository updates it.</summary>
public sealed record AppOrigin(string Repo, long RepositoryId, string Tag);

public sealed record InstalledApp(PackageManifest Manifest, string Directory, AppOrigin? Origin)
{
    public string Id => Manifest.Id;
    public string EntryPath => PackagePath.Resolve(Directory, Manifest.Entry);
    public string? IconPath => Manifest.Icon is null ? null : PackagePath.Resolve(Directory, Manifest.Icon);
}

/// <summary>
/// The apps installed for every profile, in <c>device/apps/&lt;id&gt;/</c>. Installing unpacks
/// and checks the package in a staging folder first, so a bad package changes nothing; only then is
/// the installed folder swapped for it. Profiles' saves are never touched here.
/// </summary>
public sealed class AppLibrary(DataLayout layout, WindowsTarget target)
{
    readonly Lock gate = new();

    public IReadOnlyList<InstalledApp> List()
    {
        lock (gate)
        {
            var origins = ReadOrigins();
            var apps = new List<InstalledApp>();
            if (!Directory.Exists(layout.Apps)) return apps;
            foreach (var directory in Directory.EnumerateDirectories(layout.Apps))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.')) continue;
                if (Load(directory, origins) is { } app && app.Id == name) apps.Add(app);
            }
            apps.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Manifest.Name, b.Manifest.Name));
            return apps;
        }
    }

    public InstalledApp? Find(string id)
    {
        if (!PackageManifest.IsValidId(id)) return null;
        lock (gate)
        {
            var directory = layout.App(id);
            return Directory.Exists(directory) && Load(directory, ReadOrigins()) is { } app && app.Id == id ? app : null;
        }
    }

    /// <summary>
    /// Installs a package, replacing an installed app with the same ID. A Store install
    /// (<paramref name="origin"/> set) refuses to replace an app another repository installed;
    /// a file install forgets where the app came from.
    /// </summary>
    public InstalledApp Install(string packagePath, AppOrigin? origin, Func<string, bool> isRunning, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(layout.Apps, ".staging", Guid.NewGuid().ToString("N"));
        try
        {
            var unpacked = PackageReader.Unpack(packagePath, staging, target, cancellationToken);
            var id = unpacked.Manifest.Id;
            lock (gate)
            {
                var origins = ReadOrigins();
                if (origin is not null && origins.TryGetValue(id, out var existing) && existing.RepositoryId != origin.RepositoryId
                    && Directory.Exists(layout.App(id)))
                {
                    throw new GizletException(GizletError.AppOtherRepository, $"{id} was installed from {existing.Repo}");
                }
                if (isRunning(id)) throw new GizletException(GizletError.AppRunning, id);
                var final = layout.App(id);
                string? old = null;
                if (Directory.Exists(final))
                {
                    old = Path.Combine(layout.Apps, ".trash", Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.GetDirectoryName(old)!);
                    try
                    {
                        Directory.Move(final, old);
                    }
                    catch (IOException e)
                    {
                        throw new GizletException(GizletError.AppRunning, $"{id}: {e.Message}", e);
                    }
                }
                try
                {
                    Directory.Move(staging, final);
                }
                catch
                {
                    if (old is not null) Directory.Move(old, final);
                    throw;
                }
                if (origin is null) origins.Remove(id);
                else origins[id] = origin;
                WriteOrigins(origins);
                if (old is not null) TryDelete(old);
                return Load(final, origins) ?? throw new GizletException(GizletError.PackageBadManifest, id);
            }
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Removes an app's package and Store record. Every profile keeps its saves.</summary>
    public void Uninstall(string id, Func<string, bool> isRunning)
    {
        lock (gate)
        {
            var final = layout.App(id);
            if (!Directory.Exists(final)) throw new GizletException(GizletError.AppNotInstalled, id);
            if (isRunning(id)) throw new GizletException(GizletError.AppRunning, id);
            var old = Path.Combine(layout.Apps, ".trash", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            try
            {
                Directory.Move(final, old);
            }
            catch (IOException e)
            {
                throw new GizletException(GizletError.AppRunning, $"{id}: {e.Message}", e);
            }
            var origins = ReadOrigins();
            if (origins.Remove(id)) WriteOrigins(origins);
            TryDelete(old);
        }
    }

    /// <summary>Removes what an interrupted install or uninstall left behind.</summary>
    public void Clean()
    {
        lock (gate)
        {
            TryDelete(Path.Combine(layout.Apps, ".staging"));
            TryDelete(Path.Combine(layout.Apps, ".trash"));
        }
    }

    static InstalledApp? Load(string directory, Dictionary<string, AppOrigin> origins)
    {
        try
        {
            var manifest = PackageManifest.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")));
            return new InstalledApp(manifest, directory, origins.GetValueOrDefault(manifest.Id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or GizletException)
        {
            return null;
        }
    }

    Dictionary<string, AppOrigin> ReadOrigins() =>
        JsonFile.Read<Dictionary<string, AppOrigin>>(layout.Origins) is { } read
            ? new Dictionary<string, AppOrigin>(read.Where(pair => PackageManifest.IsValidId(pair.Key)), StringComparer.Ordinal)
            : new Dictionary<string, AppOrigin>(StringComparer.Ordinal);

    void WriteOrigins(Dictionary<string, AppOrigin> origins) => JsonFile.Write(layout.Origins, origins);

    static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
