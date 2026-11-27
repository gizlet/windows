namespace Gizlet.Core.Library;

/// <summary>How an app starts, as on Gizlet OS: in its folder, with the Gizlet environment.</summary>
public static class AppLaunch
{
    /// <summary>
    /// <c>GIZLET_APP_ID</c>, <c>GIZLET_APP_DIR</c>, <c>GIZLET_SAVE_DIR</c> (its folder in the profile),
    /// <c>GIZLET_LANG</c> (<c>en</c> or <c>zh-CN</c>), <c>GIZLET_SWAP_AB</c> and <c>GIZLET_PROFILE</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(InstalledApp app, string profileId, string saveDirectory, string language) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIZLET_APP_ID"] = app.Id,
            ["GIZLET_APP_DIR"] = app.Directory,
            ["GIZLET_SAVE_DIR"] = saveDirectory,
            ["GIZLET_LANG"] = language,
            ["GIZLET_SWAP_AB"] = "0",
            ["GIZLET_PROFILE"] = profileId,
        };

    public static System.Diagnostics.ProcessStartInfo StartInfo(InstalledApp app, string profileId, string saveDirectory, string language)
    {
        var start = new System.Diagnostics.ProcessStartInfo(app.EntryPath) { WorkingDirectory = app.Directory, UseShellExecute = false };
        foreach (var (name, value) in Environment(app, profileId, saveDirectory, language)) start.Environment[name] = value;
        return start;
    }
}
