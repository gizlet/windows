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
sealed class AppRunner(Launcher launcher)
{
    readonly ConcurrentDictionary<string, Process> running = new(StringComparer.Ordinal);
    readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    public bool IsRunning(string id) => running.TryGetValue(id, out var process) && !HasExited(process);

    public bool AnyRunning => running.Values.Any(p => !HasExited(p));

    public void Start(InstalledApp app)
    {
        if (IsRunning(app.Id))
        {
            Native.Focus(running[app.Id]);
            return;
        }
        var saves = launcher.Profiles.SaveDirectory(launcher.Profile.Id, app.Id);
        var start = AppLaunch.StartInfo(app, launcher.Profile.Id, saves, S.Language);
        var process = Process.Start(start) ?? throw new InvalidOperationException(app.Id);
        Track(app.Id, process);
        launcher.ChangeSettings(s => s with { Recent = [app.Id, .. s.Recent.Where(r => r != app.Id).Take(49)] }, rebuild: false);
    }

    void Track(string id, Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => dispatcher.TryEnqueue(() =>
        {
            running.TryRemove(new KeyValuePair<string, Process>(id, process));
            process.Dispose();
            launcher.RaiseAppsChanged();
        });
        running[id] = process;
    }

    /// <summary>
    /// Finds apps still running from an earlier Launcher (closed, or updated): this user's processes
    /// whose program is in <c>device/apps/&lt;id&gt;/</c>, so profiles are not switched under them and
    /// no second copy shares their saves.
    /// </summary>
    public void Rediscover()
    {
        var apps = Path.TrimEndingDirectorySeparator(launcher.Layout.Apps) + Path.DirectorySeparatorChar;
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
            if (id is null || !PackageManifest.IsValidId(id) || running.ContainsKey(id) || HasExited(process))
            {
                process.Dispose();
                continue;
            }
            try
            {
                Track(id, process);
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                process.Dispose();
            }
        }
        dispatcher.TryEnqueue(launcher.RaiseAppsChanged);
    }

    static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
