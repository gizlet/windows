using System.Diagnostics;
using Gizlet.Core;
using Gizlet.Core.Signing;
using Gizlet.Core.Updates;
using Gizlet.Windows;
using Microsoft.UI.Dispatching;

namespace Gizlet.Launcher.Services;

public enum UpdateState { Idle, Checking, Available, Downloading, Verifying, WaitingForFlash, Installing, Failed }

/// <summary>
/// Updates of the Launcher itself. It checks the signed manifest at start and every 120 seconds
/// while it runs; a newer version is offered, and installed only when the user agrees, after the
/// manifest's signature, the installer's size and SHA-256 and its Authenticode publisher (the same
/// as this program's) check out. While a card is being written the install waits. Any failure
/// leaves this version running.
/// </summary>
sealed class LauncherUpdates
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(120);

    readonly Launcher launcher;
    readonly UpdateChecker checker;
    readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    readonly SemaphoreSlim checking = new(1, 1);

    public LauncherUpdates(Launcher launcher)
    {
        this.launcher = launcher;
        checker = new UpdateChecker(launcher.Http, Launcher.Version, launcher.Architecture, SshPublicKey.Parse(Launcher.UpdateKey));
    }

    public UpdateState State { get; private set; }
    public LauncherUpdate? Available { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }
    public bool LastCheckFailed { get; private set; }
    public string? Failure { get; private set; }
    public double Progress { get; private set; }

    public event EventHandler? Changed;

    public void Start()
    {
        _ = CheckAsync();
        var timer = dispatcher.CreateTimer();
        timer.Interval = Interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => _ = CheckAsync();
        timer.Start();
    }

    bool installing;

    public async Task CheckAsync()
    {
        if (installing) return;
        if (!await checking.WaitAsync(0)) return;
        try
        {
            if (State != UpdateState.Available) Set(UpdateState.Checking);
            var update = await Task.Run(() => checker.CheckAsync());
            LastCheckFailed = false;
            CheckedAt = DateTimeOffset.Now;
            if (installing) return;
            if (update is not null && (Available is null || update.Version >= Available.Version)) Available = update;
            Set(Available is null ? UpdateState.Idle : UpdateState.Available);
        }
        catch (Exception e) when (e is GizletException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            LastCheckFailed = true;
            CheckedAt = DateTimeOffset.Now;
            if (!installing) Set(Available is null ? UpdateState.Idle : UpdateState.Available);
        }
        finally
        {
            checking.Release();
        }
    }

    /// <summary>Downloads, verifies and installs the available update, then restarts into it.</summary>
    public async Task InstallAsync()
    {
        if (installing || Available is not { } update || State is not (UpdateState.Available or UpdateState.Failed)) return;
        installing = true;
        try
        {
            Failure = null;
            Set(UpdateState.Downloading);
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                Changed?.Invoke(this, EventArgs.Empty);
            });
            var installer = await Task.Run(() => checker.DownloadAsync(update, launcher.Layout.Updates, progress));

            Set(UpdateState.Verifying);
            var publisher = await Task.Run(() => Authenticode.SamePublisherAsSelf(installer));
            if (publisher is null) throw new GizletException(GizletError.UpdatePublisher, S.UpdateDevBuild);
            if (publisher is false) throw new GizletException(GizletError.UpdatePublisher, S.UpdateUnsigned);
            // The file may not change between the check and the start.
            using var locked = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!await UpdateChecker.MatchesAsync(locked, update.Installer)) throw new GizletException(GizletError.UpdateDownloadMismatch, installer);

            if (launcher.Flash.Busy)
            {
                Set(UpdateState.WaitingForFlash);
                while (launcher.Flash.Busy) await Task.Delay(1000);
            }
            Set(UpdateState.Installing);
            Process.Start(new ProcessStartInfo(installer)
            {
                UseShellExecute = false,
                ArgumentList = { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/RELAUNCH=1", $"/LANG={(S.Zh ? "zh" : "en")}" },
            });
            locked.Dispose();
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception e)
        {
            Failure = e is GizletException { Error: GizletError.UpdatePublisher } g ? g.Detail : S.Error(e);
            installing = false;
            Set(UpdateState.Failed);
        }
    }

    public void Dismiss()
    {
        if (State is UpdateState.Available or UpdateState.Failed) Set(UpdateState.Idle);
    }

    void Set(UpdateState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
