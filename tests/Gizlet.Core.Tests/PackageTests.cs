using System.Runtime.InteropServices;
using Gizlet.Core.Packages;

namespace Gizlet.Core.Tests;

public class PackageTests
{
    static readonly WindowsTarget X64 = new(Architecture.X64);

    static UnpackedPackage Unpack(TestPackage package, WindowsTarget? target = null)
    {
        using var temp = new TempDirectory();
        using var stream = new MemoryStream(package.Build());
        return PackageReader.Unpack(stream, temp.Combine("out"), target ?? X64);
    }

    static GizletError Refused(TestPackage package, WindowsTarget? target = null) =>
        Assert.Throws<GizletException>(() => Unpack(package, target)).Error;

    [Fact]
    public void UnpacksAValidPackage()
    {
        using var temp = new TempDirectory();
        var package = new TestPackage();
        package.Files["data/level 1.txt"] = "hello"u8.ToArray();
        using var stream = new MemoryStream(package.Build());
        var unpacked = PackageReader.Unpack(stream, temp.Combine("out"), X64);
        Assert.Equal("io.gizlet.test", unpacked.Manifest.Id);
        Assert.Equal("测试", unpacked.Manifest.NameIn("zh-CN"));
        Assert.True(File.Exists(temp.Combine("out", "bin", "test.exe")));
        Assert.Equal("hello", File.ReadAllText(temp.Combine("out", "data", "level 1.txt")));
    }

    [Fact]
    public void RefusesMissingOrWrongChecksums()
    {
        Assert.Equal(GizletError.PackageChecksums, Refused(new TestPackage { OmitChecksums = true }));
        var unlisted = new TestPackage();
        unlisted.Files["extra.txt"] = [1];
        unlisted.Unlisted.Add("extra.txt");
        Assert.Equal(GizletError.PackageChecksums, Refused(unlisted));
        var wrong = new TestPackage();
        wrong.WrongChecksums["bin/test.exe"] = new string('0', 64);
        Assert.Equal(GizletError.PackageChecksums, Refused(wrong));
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData("/abs.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("a\\b.txt")]
    [InlineData("con.txt")]
    [InlineData("data/LPT1")]
    [InlineData("trailing.")]
    [InlineData("c:evil")]
    public void RefusesUnsafePaths(string path)
    {
        var package = new TestPackage();
        package.Files[path] = [1];
        Assert.Equal(GizletError.PackageBadPath, Refused(package));
    }

    [Fact]
    public void RefusesPathsThatCollideOnWindows()
    {
        var package = new TestPackage();
        package.Files["Data.txt"] = [1];
        package.Files["data.TXT"] = [2];
        Assert.Equal(GizletError.PackageDuplicatePath, Refused(package));
        var nested = new TestPackage();
        nested.Files["bin"] = [1];
        Assert.Equal(GizletError.PackageDuplicatePath, Refused(nested));
    }

    [Fact]
    public void RefusesLinks()
    {
        var package = new TestPackage();
        package.Files["link"] = "bin/test.exe"u8.ToArray();
        package.UnixModes["link"] = 0xA1FF;
        Assert.Equal(GizletError.PackageLink, Refused(package));
    }

    [Fact]
    public void RefusesOtherPlatformsAndMachines()
    {
        Assert.Equal(GizletError.PackageWrongPlatform, Refused(new TestPackage { Target = "aarch64-unknown-linux-gnu" }));
        Assert.Equal(GizletError.PackageWrongPlatform, Refused(new TestPackage { Target = "aarch64-pc-windows-msvc", Machine = WindowsTarget.MachineArm64 }));
        Assert.Equal(GizletError.PackageEntryNotExecutable, Refused(new TestPackage { Machine = WindowsTarget.MachineArm64 }));
        var notPe = new TestPackage();
        notPe.Files["bin/test.exe"] = "#!/bin/sh"u8.ToArray();
        Assert.Equal(GizletError.PackageEntryNotExecutable, Refused(notPe));
    }

    [Fact]
    public void ArmRunsNativeAndEmulatedPackages()
    {
        var arm = new WindowsTarget(Architecture.Arm64);
        Assert.Equal("aarch64-pc-windows-msvc", arm.Preferred);
        Assert.Equal("io.gizlet.test", Unpack(new TestPackage { Target = "aarch64-pc-windows-msvc", Machine = WindowsTarget.MachineArm64 }, arm).Manifest.Id);
        Assert.Equal("io.gizlet.test", Unpack(new TestPackage(), arm).Manifest.Id);
    }

    [Fact]
    public void RefusesServicesAndBadManifests()
    {
        Assert.Equal(GizletError.PackageService, Refused(new TestPackage { Kind = "service" }));
        Assert.Equal(GizletError.PackageBadManifest, Refused(new TestPackage { Id = "NotReverseDns" }));
        var escaping = new TestPackage();
        escaping.Files["manifest.json"] = System.Text.Encoding.UTF8.GetBytes(escaping.Manifest().Replace("bin/test.exe", "../x.exe", StringComparison.Ordinal));
        Assert.Equal(GizletError.PackageBadManifest, Refused(escaping));
    }

    [Fact]
    public void RefusesAPackageWithoutAManifest()
    {
        var package = new TestPackage();
        package.Files["manifest.json"] = "[]"u8.ToArray();
        Assert.Equal(GizletError.PackageBadManifest, Refused(package));
    }

    [Fact]
    public void ReadsOnlyExecutablePeImages()
    {
        Assert.Equal(WindowsTarget.MachineAmd64, PortableExecutable.Machine(TestPackage.Pe(WindowsTarget.MachineAmd64)));
        Assert.Null(PortableExecutable.Machine(TestPackage.Pe(WindowsTarget.MachineAmd64, 0x2022)));
        Assert.Null(PortableExecutable.Machine(new byte[100]));
    }

    [Fact]
    public void ParsesSchemaOneAsAnApp()
    {
        var manifest = PackageManifest.Parse("""{"id":"io.gizlet.a","name":"A","version":"0.1.0","target":"x86_64-pc-windows-msvc","tier":"lite","entry":"a.exe"}"""u8);
        Assert.Equal("app", manifest.Kind);
        Assert.Equal(1, manifest.Schema);
        Assert.Throws<GizletException>(() => PackageManifest.Parse("""{"schema":2,"id":"io.gizlet.a","name":"A","version":"0.1.0","target":"x","tier":"lite","entry":"a.exe"}"""u8));
    }
}
