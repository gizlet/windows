using System.Text;
using Gizlet.Core.Flash;
using Gizlet.Core.Signing;
using Gizlet.Core.Updates;

namespace Gizlet.Core.Tests;

public class SignatureTests
{
    static readonly SshPublicKey TestKey = SshPublicKey.Parse(Fixtures.Text("test-key.pub"));
    static readonly SshPublicKey OsKey = SshPublicKey.Parse(OsImages.ReleaseKey);

    [Fact]
    public void VerifiesAGizletOsRelease()
    {
        var manifest = Fixtures.Bytes("os-v0.4.4-manifest.json");
        var signature = Fixtures.Text("os-v0.4.4-manifest.json.sig");
        Assert.True(SshSignature.Verify(manifest, signature, OsKey, "gizlet-launcher"));
        Assert.False(SshSignature.Verify(manifest, signature, OsKey, "gizlet-windows-update"));
        Assert.False(SshSignature.Verify(manifest, signature, TestKey, "gizlet-launcher"));
        var tampered = (byte[])manifest.Clone();
        tampered[10] ^= 1;
        Assert.False(SshSignature.Verify(tampered, signature, OsKey, "gizlet-launcher"));
        Assert.False(SshSignature.Verify(manifest, "not a signature", OsKey, "gizlet-launcher"));
    }

    [Fact]
    public void ReadsTheR36sImageOfASignedRelease()
    {
        using var http = new HttpClient();
        var images = new OsImages(http, OsKey, Path.GetTempPath());
        var at = new Uri("https://gizlet.io/assets/releases/v0.4.4/manifest.json");
        var release = images.Parse(Fixtures.Bytes("os-v0.4.4-manifest.json"), Fixtures.Text("os-v0.4.4-manifest.json.sig"), at);
        Assert.Equal("v0.4.4", release.Tag);
        Assert.Equal("https://gizlet.io/assets/releases/v0.4.4/boards/gameconsole/r36s/gizlet-os-r36s.img.gz", release.Image.ToString());
        Assert.Equal("https://gizlet.io/assets/releases/v0.4.4/boards/gameconsole/r36s/SHA256SUMS", release.Checksums.ToString());
        // A signed manifest served from another version's directory is refused.
        Assert.Throws<GizletException>(() => images.Parse(Fixtures.Bytes("os-v0.4.4-manifest.json"), Fixtures.Text("os-v0.4.4-manifest.json.sig"), new Uri("https://gizlet.io/assets/releases/v0.4.5/manifest.json")));
    }

    [Fact]
    public void FindsAnImagesChecksum()
    {
        var sums = "0596c0d7ae51acaf0997a9021a4fc3b4d65cb67ba3c2a6af28a9e173d15e153b  gizlet-os-r36s.img.gz\n";
        Assert.Equal("0596c0d7ae51acaf0997a9021a4fc3b4d65cb67ba3c2a6af28a9e173d15e153b", OsImages.ChecksumFor(sums, "gizlet-os-r36s.img.gz"));
        Assert.Null(OsImages.ChecksumFor(sums, "other.img.gz"));
    }

    static UpdateChecker.Envelope Envelope(string signatureFile) => new()
    {
        Manifest = Fixtures.Text("update-v0.2.0.json"),
        Signature = Fixtures.Text(signatureFile),
    };

    static UpdateChecker Checker(string version) =>
        new(new HttpClient(), SemVersion.Parse(version), "x64", TestKey, new Uri("https://gizlet.io/assets/windows/update.json"));

    [Fact]
    public void OffersANewerSignedRelease()
    {
        var update = Checker("0.1.0").Accept(Envelope("update-v0.2.0.json.sig"), new Uri("https://gizlet.io/assets/windows/update.json"));
        Assert.NotNull(update);
        Assert.Equal(new SemVersion(0, 2, 0), update.Version);
        Assert.Equal("https://gizlet.io/assets/windows/releases/v0.2.0/GizletSetup-0.2.0-x64.exe", update.InstallerUrl.ToString());
        Assert.Equal("商店更快了。", update.Manifest.NotesIn("zh-CN"));
        Assert.Equal("Faster Store.", update.Manifest.NotesIn("ja"));
    }

    [Fact]
    public void OffersNothingOlderOrForAnotherArchitecture()
    {
        var from = new Uri("https://gizlet.io/assets/windows/update.json");
        Assert.Null(Checker("0.2.0").Accept(Envelope("update-v0.2.0.json.sig"), from));
        Assert.Null(Checker("1.0.0").Accept(Envelope("update-v0.2.0.json.sig"), from));
        var arm = new UpdateChecker(new HttpClient(), SemVersion.Parse("0.1.0"), "arm64", TestKey, from);
        Assert.Null(arm.Accept(Envelope("update-v0.2.0.json.sig"), from));
    }

    [Fact]
    public void RefusesBadSignatures()
    {
        var from = new Uri("https://gizlet.io/assets/windows/update.json");
        Assert.Equal(GizletError.SignatureInvalid, Assert.Throws<GizletException>(() => Checker("0.1.0").Accept(Envelope("update-v0.2.0.wrong-namespace.sig"), from)).Error);
        var tampered = Envelope("update-v0.2.0.json.sig") with { Manifest = Fixtures.Text("update-v0.2.0.json").Replace("0.2.0", "9.2.0", StringComparison.Ordinal) };
        Assert.Equal(GizletError.SignatureInvalid, Assert.Throws<GizletException>(() => Checker("0.1.0").Accept(tampered, from)).Error);
        var other = new UpdateChecker(new HttpClient(), SemVersion.Parse("0.1.0"), "x64", OsKey, from);
        Assert.Equal(GizletError.SignatureInvalid, Assert.Throws<GizletException>(() => other.Accept(Envelope("update-v0.2.0.json.sig"), from)).Error);
    }

    [Fact]
    public async Task ChecksADownloadedInstaller()
    {
        var installer = new UpdateInstaller { Path = "x.exe", Size = 4, Sha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08" };
        Assert.True(await UpdateChecker.MatchesAsync(new MemoryStream(Encoding.ASCII.GetBytes("test")), installer));
        Assert.False(await UpdateChecker.MatchesAsync(new MemoryStream(Encoding.ASCII.GetBytes("tesT")), installer));
        Assert.False(await UpdateChecker.MatchesAsync(new MemoryStream(Encoding.ASCII.GetBytes("tests")), installer));
    }
}
