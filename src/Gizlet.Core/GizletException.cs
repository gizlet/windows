namespace Gizlet.Core;

/// <summary>What went wrong, for the Launcher to say in the user's language.</summary>
public enum GizletError
{
    PackageUnreadable,
    PackageTooLarge,
    PackageBadPath,
    PackageLink,
    PackageEncrypted,
    PackageDuplicatePath,
    PackageNoManifest,
    PackageBadManifest,
    PackageChecksums,
    PackageService,
    PackageWrongPlatform,
    PackageNoEntry,
    PackageEntryNotExecutable,
    PackageDownloadMismatch,
    AppRunning,
    AppNotInstalled,
    AppOtherRepository,
    ProfileNotFound,
    ProfileCurrent,
    ProfileLast,
    ProfileBadName,
    StoreUnavailable,
    StoreNoPackage,
    StoreNotReady,
    SignatureInvalid,
    UpdateBadManifest,
    UpdateDownloadMismatch,
    UpdatePublisher,
    ImageBadChecksum,
    ImageBadLayout,
    ImageTooLarge,
    DiskChanged,
    DiskNotEligible,
    DiskWriteFailed,
    DiskVerifyFailed,
}

public sealed class GizletException(GizletError error, string detail, Exception? inner = null)
    : Exception($"{error}: {detail}", inner)
{
    public GizletError Error { get; } = error;
    public string Detail { get; } = detail;
}
