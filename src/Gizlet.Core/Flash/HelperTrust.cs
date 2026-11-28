namespace Gizlet.Core.Flash;

/// <summary>
/// Whether the Launcher may start the flash helper beside it through UAC. The helper sits in the
/// user-writable install folder, so before elevating it the Launcher opens it, denying writes and
/// renames until the helper has connected, and checks its SHA-256 against the one
/// scripts/package.ps1 built into the Launcher, and, when the Launcher is signed, its publisher.
/// A development build, which has no built-in hash, starts whatever helper it was built beside.
/// </summary>
public static class HelperTrust
{
    /// <param name="samePublisher">Whether the helper is trusted and signed by the Launcher's publisher; null when the Launcher is unsigned.</param>
    /// <param name="expectedSha256">The helper's SHA-256 built into the Launcher, if any.</param>
    /// <param name="actualSha256">The SHA-256 of the helper, read from the handle held open.</param>
    /// <param name="packaged">Whether the Launcher was built by scripts/package.ps1.</param>
    public static bool Allows(bool? samePublisher, string? expectedSha256, string actualSha256, bool packaged)
    {
        if (samePublisher is false) return false;
        if (string.IsNullOrEmpty(expectedSha256)) return !packaged;
        return string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase);
    }
}
