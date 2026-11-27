using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Gizlet.Launcher;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Contains("--smoke")) return Smoke();
        // One Launcher per Windows user: a second start brings the first one forward.
        var instance = AppInstance.FindOrRegisterForKey("gizlet-launcher");
        if (!instance.IsCurrent)
        {
            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            Task.Run(() => instance.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }
        // The installer waits for this mutex to go before it replaces the Launcher's files.
        using var running = new Mutex(false, @"Local\GizletLauncher");
        instance.Activated += (_, _) => App.Current?.BringToFront();
        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            GC.KeepAlive(new App(args.Contains(Gizlet.Windows.Autostart.Argument)));
        });
        return 0;
    }

    /// <summary>
    /// For CI: loads the self-contained Windows App SDK and the Launcher's data without opening a
    /// window. Exit code 0 when everything loads.
    /// </summary>
    static int Smoke()
    {
        try
        {
            _ = AppInstance.GetCurrent().GetActivatedEventArgs();
            var layout = Core.DataLayout.Default;
            var library = new Core.Library.AppLibrary(layout, Core.Packages.WindowsTarget.Current);
            _ = library.List();
            _ = new Core.Profiles.ProfileStore(layout).List();
            _ = Core.Signing.SshPublicKey.Parse(Services.Launcher.UpdateKey);
            return File.Exists(Path.Combine(AppContext.BaseDirectory, Services.FlashSession.HelperName)) ? 0 : 3;
        }
        catch (Exception e)
        {
            App.Log(e);
            return 2;
        }
    }
}
