using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gizlet.Core.Packages;

/// <summary>A package's <c>manifest.json</c>, as gizlet/gizlet's docs/gizlet-toml.md specifies it.</summary>
public sealed partial record PackageManifest
{
    public required int Schema { get; init; }
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();
    public required string Version { get; init; }
    public string? Description { get; init; }
    public IReadOnlyDictionary<string, string> Descriptions { get; init; } = new Dictionary<string, string>();
    public string? Color { get; init; }
    public int Runtime { get; init; } = 1;
    public required string Target { get; init; }
    public required string Tier { get; init; }
    public required string Entry { get; init; }
    public string? Icon { get; init; }

    [GeneratedRegex(@"^[a-z0-9_-]+(\.[a-z0-9_-]+)+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z0-9_]+(-[a-z0-9_.]+){2,3}$")]
    private static partial Regex TargetPattern();

    [GeneratedRegex(@"^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguagePattern();

    public static bool IsValidId(string id) => id.Length <= 200 && IdPattern().IsMatch(id);

    /// <summary>The name in <paramref name="language"/> (such as <c>zh-CN</c>), else the default.</summary>
    public string NameIn(string language) => Names.TryGetValue(language, out var name) ? name : Name;

    public string? DescriptionIn(string language) => Descriptions.TryGetValue(language, out var text) ? text : Description;

    public static PackageManifest Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException e)
        {
            throw Bad($"not JSON: {e.Message}");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("not a JSON object");
            var schema = root.TryGetProperty("schema", out var s) ? (s.TryGetInt32(out var n) ? n : throw Bad("schema is not a number")) : 1;
            string? kind = OptionalString(root, "kind");
            switch (schema)
            {
                case 1 when kind is null or "app":
                    kind = "app";
                    break;
                case 2 when kind is "app" or "service":
                    break;
                default:
                    throw Bad($"schema {schema} with kind {kind ?? "(none)"} is not supported");
            }
            var manifest = new PackageManifest
            {
                Schema = schema,
                Kind = kind,
                Id = RequiredString(root, "id"),
                Name = RequiredString(root, "name"),
                Names = Languages(root, "names"),
                Version = RequiredString(root, "version"),
                Description = OptionalString(root, "description"),
                Descriptions = Languages(root, "descriptions"),
                Color = OptionalString(root, "color"),
                Runtime = root.TryGetProperty("runtime", out var r) ? (r.TryGetInt32(out var rt) ? rt : throw Bad("runtime is not a number")) : 1,
                Target = RequiredString(root, "target"),
                Tier = RequiredString(root, "tier"),
                Entry = RequiredString(root, "entry"),
                Icon = OptionalString(root, "icon"),
            };
            if (!IsValidId(manifest.Id)) throw Bad($"id {manifest.Id} is not reverse-DNS");
            if (manifest.Name.Length is 0 or > 80) throw Bad("name must have 1 to 80 characters");
            if (!VersionPattern().IsMatch(manifest.Version)) throw Bad($"version {manifest.Version} is invalid");
            if (!TargetPattern().IsMatch(manifest.Target)) throw Bad($"target {manifest.Target} is invalid");
            if (manifest.Tier is not ("lite" or "full")) throw Bad($"tier {manifest.Tier} is invalid");
            if (manifest.Color is not null && !ColorPattern().IsMatch(manifest.Color)) throw Bad($"color {manifest.Color} is invalid");
            if (!PackagePath.IsValid(manifest.Entry)) throw Bad($"entry {manifest.Entry} is not a canonical relative path");
            if (manifest.Icon is not null && !PackagePath.IsValid(manifest.Icon)) throw Bad($"icon {manifest.Icon} is not a canonical relative path");
            if (manifest.Runtime < 1) throw Bad("runtime must be at least 1");
            return manifest;
        }
    }

    static GizletException Bad(string detail) => new(GizletError.PackageBadManifest, detail);

    static string RequiredString(JsonElement root, string name) =>
        OptionalString(root, name) ?? throw Bad($"{name} is missing");

    static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : throw Bad($"{name} is not a string");
    }

    static Dictionary<string, string> Languages(JsonElement root, string name)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return result;
        if (value.ValueKind != JsonValueKind.Object) throw Bad($"{name} is not an object");
        foreach (var item in value.EnumerateObject())
        {
            if (!LanguagePattern().IsMatch(item.Name) || item.Value.ValueKind != JsonValueKind.String) throw Bad($"{name}.{item.Name} is invalid");
            result[item.Name] = item.Value.GetString()!;
        }
        return result;
    }
}
