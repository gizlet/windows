namespace Gizlet.Core.Store;

public sealed record StorePackage
{
    public string File { get; init; } = "";
    public string Target { get; init; } = "";
    public string Tier { get; init; } = "";
    public string Entry { get; init; } = "";
    public long? Size { get; init; }
    public string? Sha256 { get; init; }
    public Uri? Url { get; init; }
}

public sealed record StoreControl
{
    public string Button { get; init; } = "";
    public string Action { get; init; } = "";
    public Dictionary<string, string> Actions { get; init; } = [];
}

/// <summary>A gizlet as the Store API describes it.</summary>
public sealed record StoreGizlet
{
    public string Repo { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Dictionary<string, string> Names { get; init; } = [];
    public string? Description { get; init; }
    public Dictionary<string, string> Descriptions { get; init; } = [];
    public string Kind { get; init; } = "app";
    public string? Category { get; init; }
    public string? Color { get; init; }
    public Uri? IconUrl { get; init; }
    public Uri? CoverUrl { get; init; }
    public string? Version { get; init; }
    public string? Tag { get; init; }
    public List<string> Targets { get; init; } = [];
    public List<string> Tiers { get; init; } = [];
    public List<Uri> Screenshots { get; init; } = [];
    public Dictionary<string, Uri> AboutUrls { get; init; } = [];
    public List<StoreControl> Controls { get; init; } = [];
    public string? License { get; init; }
    public string? Owner { get; init; }
    public Uri? ShareUrl { get; init; }
    public Uri? GithubUrl { get; init; }

    public string NameIn(string language) => Names.TryGetValue(language, out var name) ? name : Name;
    public string? DescriptionIn(string language) => Descriptions.TryGetValue(language, out var text) ? text : Description;
}

public sealed record StoreSection
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public Dictionary<string, string> Titles { get; init; } = [];
    public string Layout { get; init; } = "row";
    public List<StoreGizlet> Gizlets { get; init; } = [];

    public string TitleIn(string language) => Titles.TryGetValue(language, out var title) ? title : Title;
}

public sealed record StoreFeatured
{
    /// <summary>The hero shelf's gizlets, the first shown large.</summary>
    public List<StoreGizlet> Hero { get; init; } = [];
    public List<StoreSection> Sections { get; init; } = [];
}

public sealed record StoreSearch
{
    public List<StoreGizlet> Gizlets { get; init; } = [];
    public string? NextCursor { get; init; }
}

/// <summary>The package to install, from <c>/install</c>.</summary>
public sealed record StoreInstall
{
    public string Repo { get; init; } = "";
    public long RepositoryId { get; init; }
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "app";
    public string Version { get; init; } = "";
    public string Tag { get; init; } = "";
    public StorePackage? Package { get; init; }
}

public sealed record StoreInstalled(string Repo, long RepositoryId, string Id, string Version);

/// <summary>One installed gizlet's answer from <c>POST /v1/updates</c>.</summary>
public sealed record StoreUpdate
{
    public string Repo { get; init; } = "";
    public long? RepositoryId { get; init; }
    public string Id { get; init; } = "";
    public string? Installed { get; init; }
    public string Status { get; init; } = "";
    public string? Version { get; init; }
    public string? Tag { get; init; }
    public StorePackage? Package { get; init; }

    public bool HasUpdate => Status == "update" && Package?.Url is not null;
}
