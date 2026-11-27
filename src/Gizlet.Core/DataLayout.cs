using Gizlet.Core.Packages;

namespace Gizlet.Core;

/// <summary>
/// Where the Launcher keeps its data. <c>device/</c> is shared by every profile on this Windows
/// account: installed apps, Store records, caches and downloads. <c>profiles/&lt;id&gt;/</c> is one
/// profile's own: its settings and favourites, and each app's saves in <c>apps/&lt;app-id&gt;/</c>.
/// </summary>
public sealed class DataLayout(string root)
{
    public static DataLayout Default { get; } = new(
        Environment.GetEnvironmentVariable("GIZLET_PREFIX") is { Length: > 0 } prefix
            ? prefix
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gizlet"));

    public string Root { get; } = Path.GetFullPath(root);
    public string Device => Path.Combine(Root, "device");
    public string Apps => Path.Combine(Device, "apps");
    public string Store => Path.Combine(Device, "store");
    public string Cache => Path.Combine(Device, "cache");
    public string Downloads => Path.Combine(Device, "downloads");
    public string Updates => Path.Combine(Device, "updates");
    public string Images => Path.Combine(Device, "images");
    public string DeviceSettings => Path.Combine(Device, "settings.json");
    public string Origins => Path.Combine(Store, "origins.json");
    public string Profiles => Path.Combine(Root, "profiles");

    public string App(string id) => Path.Combine(Apps, CheckId(id));

    public string Profile(string profileId) => Path.Combine(Profiles, CheckProfileId(profileId));

    public string ProfileSettings(string profileId) => Path.Combine(Profile(profileId), "settings.json");

    /// <summary>An app's saves in a profile (<c>GIZLET_SAVE_DIR</c>).</summary>
    public string Saves(string profileId, string appId) => Path.Combine(Profile(profileId), "apps", CheckId(appId));

    static string CheckId(string id) =>
        PackageManifest.IsValidId(id) ? id : throw new ArgumentException($"Not an app id: {id}", nameof(id));

    internal static string CheckProfileId(string id) =>
        id.Length == 12 && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            ? id
            : throw new ArgumentException($"Not a profile id: {id}", nameof(id));
}
