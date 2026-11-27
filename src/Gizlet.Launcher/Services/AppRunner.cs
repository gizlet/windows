using System.Collections.Concurrent;
using System.Diagnostics;
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
        var start = new ProcessStartInfo(app.EntryPath)
        {
            WorkingDirectory = app.Directory,
            UseShellExecute = false,
        };
        start.Environment["GIZLET_APP_ID"] = app.Id;
        start.Environment["GIZLET_APP_DIR"] = app.Directory;
        start.Environment["GIZLET_SAVE_DIR"] = saves;
        start.Environment["GIZLET_LANG"] = S.Language;
        start.Environment["GIZLET_SWAP_AB"] = "0";
        start.Environment["GIZLET_PROFILE"] = launcher.Profile.Id;
        var process = Process.Start(start) ?? throw new InvalidOperationException(app.Id);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => dispatcher.TryEnqueue(() =>
        {
            running.TryRemove(new KeyValuePair<string, Process>(app.Id, process));
            process.Dispose();
            launcher.RaiseAppsChanged();
        });
        running[app.Id] = process;
        launcher.ChangeSettings(s => s with { Recent = [app.Id, .. s.Recent.Where(r => r != app.Id).Take(49)] }, rebuild: false);
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
