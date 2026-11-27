using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Gizlet.Core;

/// <summary>A <c>MAJOR.MINOR.PATCH</c> version, as Gizlet packages and Launcher releases use.</summary>
public readonly record struct SemVersion(int Major, int Minor, int Patch) : IComparable<SemVersion>
{
    public static bool TryParse(string? text, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text)) return false;
        var parts = text.Split('.');
        if (parts.Length != 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > 9 || (part.Length > 1 && part[0] == '0')) return false;
            foreach (var c in part)
            {
                if (c is < '0' or > '9') return false;
            }
            numbers[i] = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        version = new SemVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var version) ? version.Value : throw new FormatException($"Not a MAJOR.MINOR.PATCH version: {text}");

    /// <summary>Parses a release tag, <c>v</c> followed by the version.</summary>
    public static bool TryParseTag(string? tag, [NotNullWhen(true)] out SemVersion? version)
    {
        version = null;
        return tag is ['v', .. var rest] && TryParse(rest, out version);
    }

    public int CompareTo(SemVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(SemVersion left, SemVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemVersion left, SemVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemVersion left, SemVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemVersion left, SemVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
