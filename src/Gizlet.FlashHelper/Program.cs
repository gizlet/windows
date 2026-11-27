using System.IO.Pipes;
using System.Security.Principal;
using Gizlet.Core;
using Gizlet.Core.Flash;
using Gizlet.Windows;

namespace Gizlet.FlashHelper;

/// <summary>
/// Writes a Gizlet OS image over a whole card, elevated. The Launcher starts it with one argument,
/// an encoded <see cref="FlashRequest"/>, which it treats as untrusted: the disk is identified
/// again and must match what the user confirmed, must be a USB, SD or MMC disk holding no system
/// volume, no system partition and not the image itself; the image is hashed from the open,
/// write-locked file the write then reads. Progress goes to the Launcher's named pipe, one JSON
/// line each; when the pipe goes away before writing starts, nothing is written. It never touches
/// a filesystem, and it is compiled ahead of time into one executable, so everything it runs is
/// covered by its signature.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        FlashRequest request;
        try
        {
            if (args.Length != 1) return 2;
            request = FlashRequest.Decode(args[0]);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException)
        {
            return 2;
        }
        using var pipe = new NamedPipeClientStream(".", request.Pipe, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(10_000);
        }
        catch (Exception e) when (e is TimeoutException or IOException)
        {
            return 3;
        }
        using var writer = new StreamWriter(pipe, Utf8NoBom) { AutoFlush = true, NewLine = "\n" };
        using var cancel = new CancellationTokenSource();
        var report = new Reporter(writer, cancel);
        WatchForCancel(pipe, cancel);
        try
        {
            new Flasher(request, report).Run(cancel.Token);
            report.Send(new FlashEvent { Stage = FlashStage.Done });
            return 0;
        }
        catch (GizletException e)
        {
            report.Send(new FlashEvent { Stage = FlashStage.Failed, Error = e.Error, Detail = e.Detail });
            return 1;
        }
        catch (OperationCanceledException)
        {
            report.Send(new FlashEvent { Stage = FlashStage.Failed, Detail = "cancelled" });
            return 1;
        }
        catch (Exception e)
        {
            report.Send(new FlashEvent { Stage = FlashStage.Failed, Error = GizletError.DiskWriteFailed, Detail = e.Message });
            return 1;
        }
    }

    static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

    /// <summary>The Launcher writes <c>cancel</c>, or closes the pipe, to stop before the write begins.</summary>
    static void WatchForCancel(NamedPipeClientStream pipe, CancellationTokenSource cancel)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(pipe, Utf8NoBom, false, 1024, leaveOpen: true);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Trim() == "cancel") break;
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            cancel.Cancel();
        })
        { IsBackground = true };
        thread.Start();
    }
}

sealed class Reporter(StreamWriter writer, CancellationTokenSource cancel)
{
    readonly Lock gate = new();
    long lastTicks;

    public void Send(FlashEvent flashEvent)
    {
        lock (gate)
        {
            try
            {
                writer.WriteLine(flashEvent.ToLine());
            }
            catch (IOException)
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public IProgress<(long Done, long Total)> Progress(FlashStage stage) => new Callback(p =>
    {
        var now = Environment.TickCount64;
        if (p.Done < p.Total && now - Interlocked.Read(ref lastTicks) < 200) return;
        Interlocked.Exchange(ref lastTicks, now);
        Send(new FlashEvent { Stage = stage, Done = p.Done, Total = p.Total });
    });

    sealed class Callback(Action<(long Done, long Total)> action) : IProgress<(long Done, long Total)>
    {
        public void Report((long Done, long Total) value) => action(value);
    }
}

sealed class Flasher(FlashRequest request, Reporter report)
{
    public void Run(CancellationToken cancellationToken)
    {
        report.Send(new FlashEvent { Stage = FlashStage.Checking });
        var image = OpenImage();
        using (image)
        {
            var disk = Identify(image.Name);
            var plan = CardWriter.Prepare(image, request.ImageSize, request.ImageSha256, disk.Size, report.Progress(FlashStage.Checking), cancellationToken);

            // The checks again, as late as possible: the card may have been swapped while the image was hashed.
            disk = Identify(image.Name);
            cancellationToken.ThrowIfCancellationRequested();
            using (var locked = LockedVolumes.Lock(disk.Volumes, TimeSpan.FromSeconds(10)))
            using (var device = RawDisk.Open(disk.Number))
            {
                if (device.Length != disk.Size) throw new GizletException(GizletError.DiskChanged, "size changed");
                if (device.MediaChangeCount() != request.Disk.MediaChangeCount) throw new GizletException(GizletError.DiskChanged, "card taken out or put in");
                if (DiskEnumerator.HasSystemPartitions(device.Handle)) throw new GizletException(GizletError.DiskNotEligible, "holds an EFI, reserved or recovery partition");
                cancellationToken.ThrowIfCancellationRequested();
                // From here a stop would leave a broken card: the write runs to its end.
                CardWriter.Write(plan, image, device, report.Progress(FlashStage.Writing), CancellationToken.None);
                CardWriter.Verify(plan, image, device, report.Progress(FlashStage.Verifying), CancellationToken.None);
                try
                {
                    device.UpdateProperties();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    /// <summary>The image, open so that nothing can change it until the write is done.</summary>
    FileStream OpenImage()
    {
        var path = request.ImagePath;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new GizletException(GizletError.ImageBadLayout, "image path must be a local file");
        var info = new FileInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Attributes.HasFlag(FileAttributes.Directory))
        {
            throw new GizletException(GizletError.ImageBadLayout, "image is not a regular file");
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        if (stream.Length != request.ImageSize)
        {
            stream.Dispose();
            throw new GizletException(GizletError.ImageBadChecksum, "image size changed");
        }
        return stream;
    }

    /// <summary>The disk the user confirmed, read again here; anything different is refused.</summary>
    DiskInfo Identify(string imagePath)
    {
        var disks = DiskEnumerator.List([imagePath, AppContext.BaseDirectory]);
        var disk = disks.FirstOrDefault(d => d.Number == request.Disk.Number);
        if (disk is null || !DiskIdentity.Of(disk).SameAs(request.Disk)) throw new GizletException(GizletError.DiskChanged, $"disk {request.Disk.Number}");
        if (DiskRules.Refusal(disk, request.ImageSize) is { } refusal) throw new GizletException(GizletError.DiskNotEligible, refusal);
        return disk;
    }
}
