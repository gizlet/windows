using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Gizlet.Windows;

/// <summary>Authenticode checks with WinVerifyTrust, for the update installer and the flash helper.</summary>
public static unsafe partial class Authenticode
{
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint window, in Guid action, WintrustData* data);

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint File;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public WintrustFileInfo* File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    /// <summary>Whether the file carries a valid Authenticode signature that chains to a trusted root.</summary>
    public static bool IsTrusted(string path)
    {
        var name = Marshal.StringToHGlobalUni(path);
        try
        {
            var file = new WintrustFileInfo { Size = (uint)sizeof(WintrustFileInfo), FilePath = name };
            var data = new WintrustData
            {
                Size = (uint)sizeof(WintrustData),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE: an offline machine can still update
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = &file,
                StateAction = 1, // WTD_STATEACTION_VERIFY
                ProvFlags = 0x80, // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var result = WinVerifyTrust(-1, GenericVerifyV2, &data);
            data.StateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(-1, GenericVerifyV2, &data);
            return result == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    /// <summary>The subject of the file's signing certificate, or null when it is unsigned.</summary>
    public static string? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is trusted and signed by the same publisher as this program.
    /// Null when this program is not signed itself (a development build), which cannot vouch for anything.
    /// </summary>
    public static bool? SamePublisherAsSelf(string path)
    {
        var self = Environment.ProcessPath is { } own ? Signer(own) : null;
        if (self is null) return null;
        return IsTrusted(path) && Signer(path) == self;
    }
}
