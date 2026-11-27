using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gizlet.Core.Flash;

/// <summary>
/// What the Launcher asks the elevated helper to write, passed on its command line. The helper
/// trusts none of it: it identifies the disk again, hashes the image itself, and applies its own rules.
/// It only ever writes the raw disk; anything done through the card's filesystem afterwards (the
/// panel overlay) is done by the unelevated Launcher.
/// </summary>
public sealed record FlashRequest
{
    public int Schema { get; init; } = 1;
    public DiskIdentity Disk { get; init; } = new();
    public string ImagePath { get; init; } = "";
    public long ImageSize { get; init; }
    public string ImageSha256 { get; init; } = "";
    /// <summary>The named pipe the Launcher listens on for progress.</summary>
    public string Pipe { get; init; } = "";
    public string Language { get; init; } = "en";

    public string Encode() => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, FlashJson.Default.FlashRequest))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static FlashRequest Decode(string text)
    {
        if (text.Length > 16_000) throw new FormatException("Request too long");
        var base64 = text.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        var request = JsonSerializer.Deserialize(Convert.FromBase64String(base64), FlashJson.Default.FlashRequest)
            ?? throw new FormatException("Empty request");
        if (request.Schema != 1) throw new FormatException($"Request schema {request.Schema}");
        if (request.ImageSha256.Length != 64 || !request.ImageSha256.All(Uri.IsHexDigit)) throw new FormatException("Bad image hash");
        if (!request.Pipe.StartsWith("gizlet-flash-", StringComparison.Ordinal) || request.Pipe.Length > 64 || !request.Pipe.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new FormatException("Bad pipe name");
        }
        return request;
    }
}

public enum FlashStage { Checking, Writing, Verifying, Configuring, Done, Failed }

/// <summary>One line of progress from the helper, as JSON.</summary>
public sealed record FlashEvent
{
    public FlashStage Stage { get; init; }
    public long Done { get; init; }
    public long Total { get; init; }
    public GizletError? Error { get; init; }
    public string? Detail { get; init; }

    public string ToLine() => JsonSerializer.Serialize(this, FlashJson.Default.FlashEvent);

    public static FlashEvent? FromLine(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, FlashJson.Default.FlashEvent);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Source-generated, so the helper can be compiled ahead of time.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FlashRequest))]
[JsonSerializable(typeof(FlashEvent))]
internal sealed partial class FlashJson : JsonSerializerContext;
