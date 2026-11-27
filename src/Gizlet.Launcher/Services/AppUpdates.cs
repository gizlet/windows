using Gizlet.Core.Store;

namespace Gizlet.Launcher.Services;

/// <summary>Which Store installs have a newer release, asked of the Store for each install's target.</summary>
sealed class AppUpdates(Launcher launcher)
{
    readonly Dictionary<string, StoreUpdate> updates = new(StringComparer.Ordinal);

    public DateTimeOffset? CheckedAt { get; private set; }

    public StoreUpdate? For(string id) => updates.TryGetValue(id, out var update) && update.HasUpdate ? update : null;

    public int Count => updates.Values.Count(u => u.HasUpdate);

    public void Forget(string id) => updates.Remove(id);

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        var installed = launcher.Library.List()
            .Where(a => a.Origin is not null)
            .Select(a => (new StoreInstalled(a.Origin!.Repo, a.Origin.RepositoryId, a.Id, a.Manifest.Version), a.Manifest.Target))
            .ToList();
        var answers = installed.Count == 0 ? [] : await launcher.Store.UpdatesAsync(installed, cancellationToken);
        updates.Clear();
        foreach (var answer in answers)
        {
            // Only a package this PC runs, of the same app, replaces what is installed.
            if (answer.HasUpdate && answer.Package is { } package && launcher.Target.Runs(package.Target)) updates[answer.Id] = answer;
        }
        CheckedAt = DateTimeOffset.Now;
        launcher.RaiseAppsChanged();
    }

    /// <summary>Whether the Store's newest release of a gizlet is newer than the installed one.</summary>
    public static bool IsNewer(StoreGizlet gizlet, string installedVersion) =>
        Core.SemVersion.TryParse(gizlet.Version, out var store) && Core.SemVersion.TryParse(installedVersion, out var have)
            ? store.Value > have.Value
            : gizlet.Version is not null && gizlet.Version != installedVersion;
}
