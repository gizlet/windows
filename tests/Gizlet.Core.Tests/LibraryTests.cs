using System.Runtime.InteropServices;
using Gizlet.Core.Library;
using Gizlet.Core.Packages;
using Gizlet.Core.Profiles;

namespace Gizlet.Core.Tests;

public class LibraryTests
{
    static bool NotRunning(string id) => false;

    [Fact]
    public void InstallsReplacesAndUninstallsKeepingEveryProfilesSaves()
    {
        using var temp = new TempDirectory();
        var layout = new DataLayout(temp.Combine("data"));
        var library = new AppLibrary(layout, new WindowsTarget(Architecture.X64));
        var profiles = new ProfileStore(layout);
        var first = profiles.Current();
        var second = profiles.Create("Second");

        var installed = library.Install(new TestPackage().WriteTo(temp.Combine("in")), new AppOrigin("github.com/a/b", 1, "v1.0.0"), NotRunning);
        Assert.Equal("1.0.0", installed.Manifest.Version);
        Assert.Equal(1, installed.Origin?.RepositoryId);
        File.WriteAllText(Path.Combine(profiles.SaveDirectory(first.Id, installed.Id), "save.txt"), "first");
        File.WriteAllText(Path.Combine(profiles.SaveDirectory(second.Id, installed.Id), "save.txt"), "second");

        var updated = library.Install(new TestPackage { Version = "1.1.0" }.WriteTo(temp.Combine("in")), new AppOrigin("github.com/a/b", 1, "v1.1.0"), NotRunning);
        Assert.Equal("1.1.0", updated.Manifest.Version);
        Assert.Single(library.List());

        library.Uninstall(installed.Id, NotRunning);
        Assert.Empty(library.List());
        Assert.Null(library.Find(installed.Id));
        Assert.Equal("first", File.ReadAllText(Path.Combine(layout.Saves(first.Id, installed.Id), "save.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(layout.Saves(second.Id, installed.Id), "save.txt")));
    }

    [Fact]
    public void ABadPackageLeavesTheInstalledAppAlone()
    {
        using var temp = new TempDirectory();
        var layout = new DataLayout(temp.Combine("data"));
        var library = new AppLibrary(layout, new WindowsTarget(Architecture.X64));
        library.Install(new TestPackage().WriteTo(temp.Combine("in")), null, NotRunning);
        var broken = new TestPackage { Version = "2.0.0" };
        broken.WrongChecksums["bin/test.exe"] = new string('0', 64);
        Assert.Throws<GizletException>(() => library.Install(broken.WriteTo(temp.Combine("in")), null, NotRunning));
        Assert.Equal("1.0.0", library.Find("io.gizlet.test")?.Manifest.Version);
        Assert.False(Directory.Exists(Path.Combine(layout.Apps, ".staging")) && Directory.EnumerateFileSystemEntries(Path.Combine(layout.Apps, ".staging")).Any());
    }

    [Fact]
    public void RefusesAnotherRepositoryAndRunningApps()
    {
        using var temp = new TempDirectory();
        var library = new AppLibrary(new DataLayout(temp.Combine("data")), new WindowsTarget(Architecture.X64));
        library.Install(new TestPackage().WriteTo(temp.Combine("in")), new AppOrigin("github.com/a/b", 1, "v1.0.0"), NotRunning);
        var other = Assert.Throws<GizletException>(() =>
            library.Install(new TestPackage().WriteTo(temp.Combine("in")), new AppOrigin("github.com/evil/b", 2, "v9.0.0"), NotRunning));
        Assert.Equal(GizletError.AppOtherRepository, other.Error);
        var running = Assert.Throws<GizletException>(() =>
            library.Install(new TestPackage().WriteTo(temp.Combine("in")), new AppOrigin("github.com/a/b", 1, "v1.0.0"), _ => true));
        Assert.Equal(GizletError.AppRunning, running.Error);
        Assert.Equal(GizletError.AppRunning, Assert.Throws<GizletException>(() => library.Uninstall("io.gizlet.test", _ => true)).Error);

        // A file install replaces it and forgets where it came from.
        var local = library.Install(new TestPackage { Version = "3.0.0" }.WriteTo(temp.Combine("in")), null, NotRunning);
        Assert.Null(local.Origin);
    }
}

public class ProfileTests
{
    [Fact]
    public void CreatesTheFirstProfileAndKeepsSettingsApart()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataLayout(temp.Path));
        var first = store.Current("Default");
        Assert.Equal("Default", first.Name);
        Assert.Equal(first, store.Current("ignored"));

        var second = store.Create("Lin");
        store.SaveSettings(first.Id, new ProfileSettings { Language = "zh-CN", Theme = ThemeChoice.Dark, Favorites = ["io.gizlet.a"] });
        Assert.Empty(store.Settings(second.Id).Favorites);
        Assert.Equal("system", store.Settings(second.Id).Language);

        store.Switch(second.Id);
        Assert.Equal(second.Id, store.Current().Id);
        Assert.Equal(["io.gizlet.a"], store.Settings(first.Id).Favorites);
        Assert.Equal(ThemeChoice.Dark, store.Settings(first.Id).Theme);
    }

    [Fact]
    public void ProtectsTheCurrentAndLastProfile()
    {
        using var temp = new TempDirectory();
        var store = new ProfileStore(new DataLayout(temp.Path));
        var first = store.Current();
        Assert.Equal(GizletError.ProfileLast, Assert.Throws<GizletException>(() => store.Delete(first.Id)).Error);
        var second = store.Create("Second");
        Assert.Equal(GizletError.ProfileCurrent, Assert.Throws<GizletException>(() => store.Delete(first.Id)).Error);
        Assert.Equal(GizletError.ProfileBadName, Assert.Throws<GizletException>(() => store.Create("second")).Error);
        Assert.Equal(GizletError.ProfileBadName, Assert.Throws<GizletException>(() => store.Create("  ")).Error);
        store.Rename(second.Id, "Renamed");
        store.Delete(second.Id);
        Assert.Single(store.List());
    }

    [Fact]
    public void IgnoresUnsafeIdsInSettings()
    {
        using var temp = new TempDirectory();
        var layout = new DataLayout(temp.Path);
        var store = new ProfileStore(layout);
        var profile = store.Current();
        File.WriteAllText(layout.ProfileSettings(profile.Id), """{"language":"fr","favorites":["../../x","io.gizlet.ok"]}""");
        var settings = store.Settings(profile.Id);
        Assert.Equal("system", settings.Language);
        Assert.Equal(["io.gizlet.ok"], settings.Favorites);
        Assert.Throws<ArgumentException>(() => layout.Saves(profile.Id, "..\\..\\x"));
        Assert.Throws<ArgumentException>(() => layout.Profile(".."));
    }
}
