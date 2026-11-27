using System.Text;

namespace Gizlet.Core.Packages;

/// <summary>
/// Paths inside a package: canonical, relative, <c>/</c>-separated, and safe to create on Windows.
/// </summary>
public static class PackagePath
{
    const int MaxLength = 200;

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Whether <paramref name="path"/> names a file inside a package (no trailing <c>/</c>).</summary>
    public static bool IsValid(string path)
    {
        if (path.Length == 0 || path.Length > MaxLength || !path.IsNormalized(NormalizationForm.FormC)) return false;
        foreach (var part in path.Split('/'))
        {
            if (!IsValidPart(part)) return false;
        }
        return true;
    }

    static bool IsValidPart(string part)
    {
        if (part.Length == 0 || part is "." or "..") return false;
        if (part[^1] is '.' or ' ' || part[0] == ' ') return false;
        foreach (var c in part)
        {
            if (c < 0x20 || c == 0x7f || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        }
        var stem = part.Split('.')[0].TrimEnd(' ');
        return !Reserved.Contains(stem);
    }

    /// <summary>The key two paths collide under on Windows: case-insensitive, Unicode form C.</summary>
    public static string CollisionKey(string path) => path.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    /// <summary>
    /// The full path of <paramref name="path"/> under <paramref name="root"/>, checked to stay inside it.
    /// </summary>
    public static string Resolve(string root, string path)
    {
        if (!IsValid(path)) throw new GizletException(GizletError.PackageBadPath, path);
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new GizletException(GizletError.PackageBadPath, path);
        return full;
    }
}
