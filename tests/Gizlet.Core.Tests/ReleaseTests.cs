using System.Text.Json;
using Gizlet.Core.Signing;
using Gizlet.Core.Updates;

namespace Gizlet.Core.Tests;

/// <summary>The Launcher accepts what devops/scripts/release.py publishes.</summary>
public class ReleaseTests
{
    static readonly Uri From = new("https://gizlet.io/assets/windows/update.json");

    static string RepoFile(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, relative);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(relative);
    }

    static UpdateChecker.Envelope Envelope() =>
        JsonSerializer.Deserialize<UpdateChecker.Envelope>(Fixtures.Text("release-v0.1.0.update.json"), JsonFile.Options)!;

    [Fact]
    public void TheBuiltInKeyIsTheReleaseKey()
    {
        var published = File.ReadAllText(RepoFile("devops/update-key.pub")).Split(' ', StringSplitOptions.RemoveEmptyEntries)[..2];
        Assert.Equal(published, UpdateManifest.TrustedKey.Split(' ')[..2]);
    }

    [Fact]
    public void AcceptsTheReleaseScriptsManifest()
    {
        var checker = new UpdateChecker(new HttpClient(), new SemVersion(0, 0, 0), "x64", SshPublicKey.Parse(UpdateManifest.TrustedKey), From);
        var update = checker.Accept(Envelope(), From);
        Assert.NotNull(update);
        Assert.Equal(new SemVersion(0, 1, 0), update.Version);
        Assert.Equal("https://gizlet.io/assets/windows/releases/v0.1.0/GizletSetup-0.1.0-x64.exe", update.InstallerUrl.ToString());
        Assert.Equal(12345, update.Installer.Size);
        Assert.StartsWith("第一版", update.Manifest.NotesIn("zh-CN"), StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesTheManifestWhenAnyByteChanges()
    {
        var checker = new UpdateChecker(new HttpClient(), new SemVersion(0, 0, 0), "x64", SshPublicKey.Parse(UpdateManifest.TrustedKey), From);
        var envelope = Envelope();
        var tampered = envelope with { Manifest = envelope.Manifest!.Replace("\"size\": 12345", "\"size\": 12346", StringComparison.Ordinal) };
        Assert.NotEqual(envelope.Manifest, tampered.Manifest);
        Assert.Equal(GizletError.SignatureInvalid, Assert.Throws<GizletException>(() => checker.Accept(tampered, From)).Error);
    }
}
