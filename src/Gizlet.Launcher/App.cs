using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gizlet.Launcher;

/// <summary>
/// The Launcher. App.xaml merges the Fluent control resources; every page is built in code.
/// </summary>
sealed partial class App : Application
{
    readonly bool autostart;

    public App(bool autostart)
    {
        this.autostart = autostart;
        Current = this;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Log(e.Exception);
            e.Handled = true;
        };
    }

    public static new App? Current { get; private set; }

    public MainWindow? Window { get; private set; }

    public Services.Launcher? Launcher { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Launcher = new Services.Launcher();
        Window = new MainWindow(Launcher);
        Window.Show(minimized: autostart);
        Launcher.Updates.Start();
        _ = CheckAppUpdatesAsync();
    }

    async Task CheckAppUpdatesAsync()
    {
        try
        {
            await Launcher!.AppUpdates.CheckAsync();
        }
        catch (Exception e)
        {
            Log(e);
        }
    }

    public void BringToFront() => Window?.DispatcherQueue.TryEnqueue(() => Window.Show(minimized: false));

    public static void Log(Exception exception)
    {
        try
        {
            var path = Path.Combine(Core.DataLayout.Default.Device, "launcher.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 1 << 20) File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {exception}\n");
        }
        catch (IOException)
        {
        }
    }
}
