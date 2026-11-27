using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Gizlet.Core.Packages;
using Gizlet.Core.Library;
using Microsoft.UI.Dispatching;

namespace Gizlet.Launcher.Services;

/// <summary>
/// Starts installed apps as gizlet/os's launcher does: the app's folder as the working directory,
/// and <c>GIZLET_APP_ID</c>, <c>GIZLET_APP_DIR</c>, <c>GIZLET_SAVE_DIR</c> (its folder in the current
/// profile), <c>GIZLET_LANG</c> and <c>GIZLET_SWAP_AB</c>.
/// </summary>
sealed class AppRunner
{
    readonly Launcher launcher;
    readonly ConcurrentDictionary<string, AppJob> running = new(StringComparer.Ordinal);
    readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    readonly DispatcherQueueTimer timer;

    public AppRunner(Launcher launcher)
    {
        this.launcher = launcher;
        // Apps may hand over to child processes and exit: their state is polled, not only awaited.
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = true;
        timer.Tick += (_, _) => Sweep();
        timer.Start();
    }

    public bool IsRunning(string id) => running.TryGetValue(id, out var job) && job.Running;

    public bool AnyRunning => running.Values.Any(j => j.Running);

    public void Start(InstalledApp app)
    {
        if (running.TryGetValue(app.Id, out var job) && job.Running)
        {
            if (job.Main is { } main) Native.Focus(main);
            return;
        }
        var saves = launcher.Profiles.SaveDirectory(launcher.Profile.Id, app.Id);
        var start = AppLaunch.StartInfo(app, launcher.Profile.Id, saves, S.Language);
        if (running.TryRemove(app.Id, out var old)) old.Dispose();
        running[app.Id] = AppJob.Start(start);
        launcher.ChangeSettings(s => s with { Recent = [app.Id, .. s.Recent.Where(r => r != app.Id).Take(49)] }, rebuild: false);
    }

    /// <summary>Forgets apps whose every process has exited, and tells the pages.</summary>
    void Sweep()
    {
        var changed = false;
        foreach (var (id, job) in running)
        {
            if (job.Running || !running.TryRemove(new KeyValuePair<string, AppJob>(id, job))) continue;
            job.Dispose();
            changed = true;
        }
        if (changed) launcher.RaiseAppsChanged();
    }

    /// <summary>
    /// Finds apps still running from an earlier Launcher (closed, or updated): this user's processes
    /// whose program is in <c>device/apps/&lt;id&gt;/</c>, every one of them, so profiles are not
    /// switched under them and no second copy shares their saves.
    /// </summary>
    public void Rediscover()
    {
        var apps = Path.TrimEndingDirectorySeparator(launcher.Layout.Apps) + Path.DirectorySeparatorChar;
        var found = new Dictionary<string, AppJob>(StringComparer.Ordinal);
        foreach (var process in Process.GetProcesses())
        {
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
            {
            }
            var id = path is not null && path.StartsWith(apps, StringComparison.OrdinalIgnoreCase)
                ? path[apps.Length..].Split(Path.DirectorySeparatorChar)[0]
                : null;
            if (id is null || !PackageManifest.IsValidId(id))
            {
                process.Dispose();
                continue;
            }
            if (!found.TryGetValue(id, out var job)) found[id] = job = AppJob.Found();
            job.Add(process);
        }
        foreach (var (id, job) in found)
        {
            if (!running.TryAdd(id, job)) job.Dispose();
        }
        dispatcher.TryEnqueue(launcher.RaiseAppsChanged);
    }
}
