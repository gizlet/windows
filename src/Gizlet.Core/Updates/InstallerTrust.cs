namespace Gizlet.Core.Updates;

public enum InstallerVerdict { Install, DevelopmentBuild, WrongPublisher }

/// <summary>
/// Whether a downloaded update installer may run, once its size and SHA-256 match the signed
/// manifest. A signed Launcher runs only an installer from its own publisher, so once releases are
/// signed they stay signed. An unsigned packaged Launcher (a release built before a code-signing
/// certificate existed) trusts the manifest's signature and the hash in it. A development build
/// never installs updates.
/// </summary>
public static class InstallerTrust
{
    /// <param name="samePublisher">
    /// Whether the installer is trusted and signed by this Launcher's publisher; null when this
    /// Launcher is not signed itself.
    /// </param>
    /// <param name="packaged">Whether this Launcher was built by scripts/package.ps1.</param>
    public static InstallerVerdict Judge(bool? samePublisher, bool packaged) => samePublisher switch
    {
        true => InstallerVerdict.Install,
        false => InstallerVerdict.WrongPublisher,
        null => packaged ? InstallerVerdict.Install : InstallerVerdict.DevelopmentBuild,
    };
}
