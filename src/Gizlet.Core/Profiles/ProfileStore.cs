using System.Security.Cryptography;

namespace Gizlet.Core.Profiles;

public sealed record Profile(string Id, string Name, DateTimeOffset CreatedAt);

public enum ThemeChoice { System, Light, Dark }

/// <summary>What one profile keeps besides its saves.</summary>
public sealed record ProfileSettings
{
    /// <summary><c>system</c>, <c>en</c> or <c>zh-CN</c>.</summary>
    public string Language { get; init; } = "system";
    public ThemeChoice Theme { get; init; } = ThemeChoice.System;
    public IReadOnlyList<string> Favorites { get; init; } = [];
    /// <summary>App IDs, most recently started first.</summary>
    public IReadOnlyList<string> Recent { get; init; } = [];
}

sealed record DeviceSettings
{
    public string? CurrentProfile { get; init; }
}

/// <summary>
/// Local profiles: each person on this Windows account keeps their own saves, favourites and
/// settings over the shared apps. No sign-in. Switching only changes which profile is current; deleting
/// one removes its folder whole and is refused for the current one.
/// </summary>
public sealed class ProfileStore(DataLayout layout)
{
    public const int MaxNameLength = 40;
    readonly Lock gate = new();

    public event EventHandler? Changed;

    public IReadOnlyList<Profile> List()
    {
        lock (gate) return ListUnlocked();
    }

    /// <summary>The current profile; the first start creates one called <paramref name="defaultName"/>.</summary>
    public Profile Current(string defaultName = "Default")
    {
        lock (gate)
        {
            var profiles = ListUnlocked();
            var chosen = JsonFile.Read<DeviceSettings>(layout.DeviceSettings)?.CurrentProfile;
            var current = profiles.FirstOrDefault(p => p.Id == chosen) ?? profiles.FirstOrDefault();
            if (current is null) current = CreateUnlocked(defaultName, profiles);
            if (current.Id != chosen) JsonFile.Write(layout.DeviceSettings, new DeviceSettings { CurrentProfile = current.Id });
            return current;
        }
    }

    public Profile Create(string name)
    {
        Profile profile;
        lock (gate) profile = CreateUnlocked(name, ListUnlocked());
        Changed?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public void Rename(string id, string name)
    {
        lock (gate)
        {
            var profiles = ListUnlocked();
            var profile = profiles.FirstOrDefault(p => p.Id == id) ?? throw new GizletException(GizletError.ProfileNotFound, id);
            name = CheckName(name, profiles.Where(p => p.Id != id));
            JsonFile.Write(ProfileFile(id), profile with { Name = name });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Switch(string id)
    {
        lock (gate)
        {
            if (ListUnlocked().All(p => p.Id != id)) throw new GizletException(GizletError.ProfileNotFound, id);
            JsonFile.Write(layout.DeviceSettings, new DeviceSettings { CurrentProfile = id });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes a profile that is not current, with all its saves and settings.</summary>
    public void Delete(string id)
    {
        lock (gate)
        {
            var profiles = ListUnlocked();
            if (profiles.All(p => p.Id != id)) throw new GizletException(GizletError.ProfileNotFound, id);
            if (profiles.Count == 1) throw new GizletException(GizletError.ProfileLast, id);
            if (JsonFile.Read<DeviceSettings>(layout.DeviceSettings)?.CurrentProfile == id) throw new GizletException(GizletError.ProfileCurrent, id);
            // Renamed first, so a deletion cut short never leaves a half profile listed.
            var trash = Path.Combine(layout.Profiles, ".trash", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            Directory.Move(layout.Profile(id), trash);
            Directory.Delete(trash, recursive: true);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ProfileSettings Settings(string id)
    {
        var settings = JsonFile.Read<ProfileSettings>(layout.ProfileSettings(id)) ?? new ProfileSettings();
        return settings with
        {
            Language = settings.Language is "en" or "zh-CN" ? settings.Language : "system",
            Favorites = settings.Favorites.Where(Packages.PackageManifest.IsValidId).Distinct().ToList(),
            Recent = settings.Recent.Where(Packages.PackageManifest.IsValidId).Distinct().Take(50).ToList(),
        };
    }

    public void SaveSettings(string id, ProfileSettings settings)
    {
        lock (gate)
        {
            if (!Directory.Exists(layout.Profile(id))) throw new GizletException(GizletError.ProfileNotFound, id);
            JsonFile.Write(layout.ProfileSettings(id), settings);
        }
    }

    /// <summary>The app's save folder in the profile, created if needed.</summary>
    public string SaveDirectory(string profileId, string appId)
    {
        var directory = layout.Saves(profileId, appId);
        Directory.CreateDirectory(directory);
        return directory;
    }

    string ProfileFile(string id) => Path.Combine(layout.Profile(id), "profile.json");

    List<Profile> ListUnlocked()
    {
        var profiles = new List<Profile>();
        if (!Directory.Exists(layout.Profiles)) return profiles;
        foreach (var directory in Directory.EnumerateDirectories(layout.Profiles))
        {
            var id = Path.GetFileName(directory);
            if (id.StartsWith('.')) continue;
            try
            {
                DataLayout.CheckProfileId(id);
            }
            catch (ArgumentException)
            {
                continue;
            }
            if (JsonFile.Read<Profile>(ProfileFile(id)) is { } profile && profile.Id == id && !string.IsNullOrWhiteSpace(profile.Name))
            {
                profiles.Add(profile);
            }
        }
        profiles.Sort((a, b) => a.CreatedAt != b.CreatedAt ? a.CreatedAt.CompareTo(b.CreatedAt) : string.CompareOrdinal(a.Id, b.Id));
        return profiles;
    }

    Profile CreateUnlocked(string name, IEnumerable<Profile> others)
    {
        name = CheckName(name, others);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var profile = new Profile(id, name, DateTimeOffset.UtcNow);
        Directory.CreateDirectory(Path.Combine(layout.Profile(id), "apps"));
        JsonFile.Write(ProfileFile(id), profile);
        return profile;
    }

    static string CheckName(string name, IEnumerable<Profile> others)
    {
        name = name.Trim();
        if (name.Length is 0 or > MaxNameLength || name.Any(char.IsControl)) throw new GizletException(GizletError.ProfileBadName, name);
        if (others.Any(p => StringComparer.CurrentCultureIgnoreCase.Equals(p.Name, name))) throw new GizletException(GizletError.ProfileBadName, name);
        return name;
    }
}
