using System.Runtime.InteropServices;
using Gizlet.Core.Flash;

namespace Gizlet.Windows.Tests;

/// <summary>Runs against the real Windows APIs on the CI runner; skipped on other systems.</summary>
public class WindowsTests
{
    static void OnlyOnWindows() => Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows only");

    [Fact]
    public void TheWindowsDiskHoldsTheSystemAndIsNeverWritable()
    {
        OnlyOnWindows();
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        var disks = DiskEnumerator.List();
        Assert.NotEmpty(disks);
        var windows = disks.Where(d => d.Volumes.Any(v => string.Equals(v.MountPoint, system, StringComparison.OrdinalIgnoreCase))).ToList();
        Assert.Single(windows);
        Assert.True(windows[0].HoldsSystem);
        Assert.NotNull(DiskRules.Refusal(windows[0]));
        Assert.All(disks.Where(d => d.Bus == DiskBus.Other), d => Assert.NotNull(DiskRules.Refusal(d)));
        Assert.All(disks, d => Assert.StartsWith(@"\\?\", d.DevicePath, StringComparison.Ordinal));
    }

    [Fact]
    public void TheDiskHoldingTheLauncherIsProtected()
    {
        OnlyOnWindows();
        var here = Path.GetPathRoot(AppContext.BaseDirectory)!;
        var disk = DiskEnumerator.List([AppContext.BaseDirectory])
            .Single(d => d.Volumes.Any(v => string.Equals(v.MountPoint, here, StringComparison.OrdinalIgnoreCase)));
        Assert.True(disk.HoldsSystem);
    }

    [Fact]
    public void TrustsMicrosoftSignedFilesOnly()
    {
        OnlyOnWindows();
        var signed = typeof(object).Assembly.Location;
        Assert.True(Authenticode.IsTrusted(signed));
        Assert.Contains("Microsoft", Authenticode.Signer(signed), StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), "gizlet-authenticode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tampered = Path.Combine(directory, "tampered.dll");
            var bytes = File.ReadAllBytes(signed);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(tampered, bytes);
            Assert.False(Authenticode.IsTrusted(tampered));

            var unsigned = Path.Combine(directory, "unsigned.exe");
            File.WriteAllBytes(unsigned, [(byte)'M', (byte)'Z', 0, 0]);
            Assert.False(Authenticode.IsTrusted(unsigned));
            Assert.Null(Authenticode.Signer(unsigned));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
