using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using Gizlet.Core;
using Gizlet.Core.Flash;
using Gizlet.Core.Signing;
using Gizlet.Windows;

namespace Gizlet.Launcher.Services;

/// <summary>
/// Writing a card from the unelevated Launcher: lists the card readers, prepares the image, then
/// starts the signed helper through UAC with what the user confirmed and follows its progress over
/// a named pipe. The helper decides for itself whether the write may go ahead.
/// </summary>
sealed class FlashSession(Launcher launcher)
{
    public const string HelperName = "Gizlet.FlashHelper.exe";
    const int ErrorCancelled = 1223;

    public bool Busy { get; private set; }

    public OsImages Images { get; } = new(launcher.Http, SshPublicKey.Parse(OsImages.ReleaseKey), launcher.Layout.Images);

    /// <summary>Card readers: USB, SD and MMC disks that hold no system volume, smallest first.</summary>
    public static Task<IReadOnlyList<DiskInfo>> CardsAsync() => Task.Run<IReadOnlyList<DiskInfo>>(() =>
        DiskEnumerator.List([AppContext.BaseDirectory, DataLayout.Default.Root])
            .Where(d => DiskRules.Refusal(d) is null)
            .OrderBy(d => d.Size)
            .ThenBy(d => d.Number)
            .ToList());

    public static Task<PanelGuess?> PanelAsync(DiskInfo disk) => Task.Run(() =>
        disk.BootVolume is { } boot ? PanelDetector.FromBootPartition(boot.MountPoint ?? boot.GuidPath, disk.HasGizletOs) : null);

    /// <summary>
    /// Writes <paramref name="image"/> over the card confirmed as <paramref name="confirmed"/>, then,
    /// for <paramref name="panel4"/>, selects the Panel 4 overlay; busy throughout, so updates and
    /// closing the window wait. Returns normally when the card was written and read back; throws
    /// otherwise, <see cref="Panel4Exception"/> when only the overlay failed.
    /// </summary>
    public async Task WriteAsync(DiskIdentity confirmed, CardImage image, bool panel4, Action<FlashEvent> progress, Action asking, CancellationToken cancellationToken)
    {
        if (Busy) throw new InvalidOperationException("busy");
        Busy = true;
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, HelperName);
            if (!File.Exists(helper)) throw new FileNotFoundException(helper);
            if (Authenticode.SamePublisherAsSelf(helper) is false) throw new InvalidOperationException(S.HelperUntrusted);

            var pipeName = "gizlet-flash-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
            var request = new FlashRequest
            {
                Disk = confirmed,
                ImagePath = image.Path,
                ImageSize = image.Size,
                ImageSha256 = image.Sha256,
                Pipe = pipeName,
                Language = S.Language,
            };
            await using var pipe = CreatePipe(pipeName);
            asking();
            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(helper, request.Encode()) { UseShellExecute = true, Verb = "runas" })
                    ?? throw new GizletException(GizletError.DiskWriteFailed, "helper did not start");
            }
            catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
            {
                throw new OperationCanceledException(S.PermissionDenied, e);
            }
            using (process)
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(TimeSpan.FromSeconds(30));
                await pipe.WaitForConnectionAsync(connect.Token);
                // Only the helper this Launcher started may report: its process, whoever it runs as.
                if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != (uint)process.Id)
                {
                    throw new GizletException(GizletError.DiskWriteFailed, "another process answered on the pipe");
                }
                using var reader = new StreamReader(pipe, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                await using var stop = cancellationToken.Register(() =>
                {
                    try
                    {
                        writer.WriteLine("cancel");
                    }
                    catch (Exception e) when (e is IOException or ObjectDisposedException)
                    {
                    }
                });
                FlashEvent? last = null;
                while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
                {
                    if (FlashEvent.FromLine(line) is not { } flashEvent) continue;
                    last = flashEvent;
                    progress(flashEvent);
                    if (flashEvent.Stage is FlashStage.Done or FlashStage.Failed) break;
                }
                await process.WaitForExitAsync(CancellationToken.None);
                switch (last)
                {
                    case { Stage: FlashStage.Done } when process.ExitCode == 0:
                        break;
                    case { Stage: FlashStage.Failed, Error: { } error }:
                        throw new GizletException(error, last.Detail ?? "");
                    case { Stage: FlashStage.Failed }:
                        throw new OperationCanceledException(S.NothingWritten);
                    default:
                        throw new GizletException(GizletError.DiskWriteFailed, $"helper exited with {process.ExitCode}");
                }
            }
            if (panel4)
            {
                progress(new FlashEvent { Stage = FlashStage.Configuring });
                try
                {
                    await ConfigurePanel4Async(confirmed, CancellationToken.None);
                }
                catch (Exception e) when (e is GizletException or IOException or UnauthorizedAccessException)
                {
                    throw new Panel4Exception(e);
                }
            }
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// The progress pipe: this user, and elevated administrators. The helper may run as another
    /// administrator when a standard user enters an administrator's credentials at UAC; an
    /// unelevated process of that administrator has the Administrators group only to deny. The
    /// client is checked to be the helper process itself once it connects.
    /// </summary>
    static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    /// <summary>
    /// Selects the Panel 4 overlay on a card just written: copies <c>r36s-v30-panel4.dtbo</c> to
    /// <c>mipi-panel.dtbo</c> on its boot partition, as gizlet/os's writer does. Runs as the user,
    /// on the FAT volume named GIZLETBOOT of the same device (path, serial and size) that was written.
    /// </summary>
    public static Task ConfigurePanel4Async(DiskIdentity written, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var disk = DiskEnumerator.List().FirstOrDefault(d =>
                string.Equals(d.DevicePath, written.DevicePath, StringComparison.OrdinalIgnoreCase)
                && d.Serial == written.Serial && d.Size == written.Size);
            var boot = disk?.Volumes.FirstOrDefault(v => string.Equals(v.Label, "GIZLETBOOT", StringComparison.OrdinalIgnoreCase)
                && v.FileSystem is { } fs && fs.StartsWith("FAT", StringComparison.OrdinalIgnoreCase));
            if (disk is not null && DiskRules.Refusal(disk) is null && boot is not null)
            {
                var root = boot.MountPoint ?? boot.GuidPath;
                var source = new FileInfo(Path.Combine(root, PanelDetector.Panel4Overlay));
                if (source.Exists && !source.Attributes.HasFlag(FileAttributes.ReparsePoint) && source.Length < 1 << 20)
                {
                    await File.WriteAllBytesAsync(Path.Combine(root, PanelDetector.OverlayFile), await File.ReadAllBytesAsync(source.FullName, cancellationToken), cancellationToken);
                    return;
                }
            }
            await Task.Delay(500, cancellationToken);
        }
        throw new GizletException(GizletError.DiskWriteFailed, "panel 4 overlay");
    }, cancellationToken);
}

/// <summary>The card was written and verified, but the Panel 4 overlay could not be selected.</summary>
sealed class Panel4Exception(Exception inner) : Exception("panel 4 overlay", inner);
