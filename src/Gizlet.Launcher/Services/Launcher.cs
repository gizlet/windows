using System.Diagnostics;
using System.Reflection;
using Gizlet.Core;
using Gizlet.Core.Flash;
using Gizlet.Core.Library;
using Gizlet.Core.Packages;
using Gizlet.Core.Profiles;
using Gizlet.Core.Signing;
using Gizlet.Core.Store;
using Gizlet.Core.Updates;

namespace Gizlet.Launcher.Services;

/// <summary>Everything the pages share. Created once, on the UI thread.</summary>
sealed class Launcher
{
    /// <summary>
    /// The key <c>windows/update.json</c> must be signed with: the Release workflow's, unless this is
    /// a rehearsal build given a throwaway one (<c>-p:GizletTestUpdateKey</c>).
    /// </summary>
    public static string UpdateKey { get; } =
        typeof(Launcher).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GizletTestUpdateKey")?.Value is { Length: > 0 } test
            ? $"ssh-ed25519 {test}"
            : UpdateManifest.TrustedKey;

    /// <summary>Whether this build was made by scripts/package.ps1 (<c>-p:GizletPackaged=true</c>), not a development build.</summary>
    public static bool Packaged { get; } =
        typeof(Launcher).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "GizletPackaged" && a.Value == "true");

    public static bool AllowsUnsignedUpdates { get; } =
        typeof(Launcher).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Any(a => a.Key == "GizletUnsignedUpdates" && a.Value == "true");

    /// <summary>The SHA-256 of the flash helper shipped with this build (<c>-p:GizletHelperSha256</c>), if packaged.</summary>
    public static string? HelperSha256 { get; } =
        typeof(Launcher).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "GizletHelperSha256")?.Value is { Length: > 0 } sha ? sha : null;

    public Launcher()
    {
        Layout = DataLayout.Default;
        Directory.CreateDirectory(Layout.Device);
        Http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"GizletLauncher/{Version} (Windows; {Target.Preferred})");
        Library = new AppLibrary(Layout, Target);
        Library.Clean();
        Profiles = new ProfileStore(Layout);
        Store = new StoreClient(Http, Target);
        Runner = new AppRunner(this);
        _ = Task.Run(Runner.Rediscover);
        AppUpdates = new AppUpdates(this);
        Flash = new FlashSession(this);
        Updates = new LauncherUpdates(this);
        Profile = Profiles.Current(S.DefaultProfileName);
        Settings = Profiles.Settings(Profile.Id);
        S.Use(Settings.Language);
    }

    public static SemVersion Version { get; } = SemVersion.TryParse(
        typeof(Launcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0], out var v)
        ? v.Value : new SemVersion(0, 0, 0);

    public WindowsTarget Target { get; } = WindowsTarget.Current;
    /// <summary>The installer to update with: this build's own architecture (an x64 build on Arm64 stays x64).</summary>
    public static string Architecture => System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
    public DataLayout Layout { get; }
    public HttpClient Http { get; }
    public AppLibrary Library { get; }
    public ProfileStore Profiles { get; }
    public StoreClient Store { get; }
    public AppRunner Runner { get; }
    public AppUpdates AppUpdates { get; }
    public FlashSession Flash { get; }
    public LauncherUpdates Updates { get; }

    public Profile Profile { get; private set; }
    public ProfileSettings Settings { get; private set; }

    /// <summary>The profile, its language or appearance changed: pages build themselves again.</summary>
    public event EventHandler? ProfileChanged;

    /// <summary>Apps were installed, updated, removed, started or stopped.</summary>
    public event EventHandler? AppsChanged;

    public void RaiseAppsChanged() => AppsChanged?.Invoke(this, EventArgs.Empty);

    public void SwitchProfile(string id)
    {
        if (Runner.AnyRunning) throw new InvalidOperationException(S.CloseAppsFirst);
        Profiles.Switch(id);
        ReloadProfile();
    }

    public void ReloadProfile()
    {
        Profile = Profiles.Current(S.DefaultProfileName);
        Settings = Profiles.Settings(Profile.Id);
        S.Use(Settings.Language);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ChangeSettings(Func<ProfileSettings, ProfileSettings> change, bool rebuild)
    {
        Settings = change(Profiles.Settings(Profile.Id));
        Profiles.SaveSettings(Profile.Id, Settings);
        S.Use(Settings.Language);
        if (rebuild) ProfileChanged?.Invoke(this, EventArgs.Empty);
        else AppsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsFavorite(string id) => Settings.Favorites.Contains(id);

    public void ToggleFavorite(string id) => ChangeSettings(s => s with
    {
        Favorites = s.Favorites.Contains(id) ? s.Favorites.Where(f => f != id).ToList() : [.. s.Favorites, id],
    }, rebuild: false);

    /// <summary>Installs a package file from the Store or from disk, then tells the pages.</summary>
    public async Task<InstalledApp> InstallAsync(string packagePath, AppOrigin? origin, CancellationToken cancellationToken = default)
    {
        var app = await Task.Run(() => Library.Install(packagePath, origin, Runner.IsRunning, cancellationToken), cancellationToken);
        AppUpdates.Forget(app.Id);
        RaiseAppsChanged();
        return app;
    }

    public async Task<InstalledApp> InstallFromStoreAsync(StorePackage package, string repo, long repositoryId, string tag, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        var file = await Store.DownloadAsync(package, Layout.Downloads, progress, cancellationToken);
        try
        {
            return await InstallAsync(file, new AppOrigin(repo, repositoryId, tag), cancellationToken);
        }
        finally
        {
            File.Delete(file);
        }
    }

    public async Task UninstallAsync(string id)
    {
        await Task.Run(() => Library.Uninstall(id, Runner.IsRunning));
        AppUpdates.Forget(id);
        RaiseAppsChanged();
    }

    public static void OpenInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });

    public static void OpenUrl(Uri url)
    {
        if (url.Scheme is "https" or "http") Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });
    }
}
