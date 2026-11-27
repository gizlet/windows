using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Gizlet.Core.Signing;

/// <summary>An Ed25519 public key as OpenSSH writes it: <c>ssh-ed25519 AAAA...</c>.</summary>
public sealed class SshPublicKey
{
    SshPublicKey(byte[] blob, byte[] key)
    {
        Blob = blob;
        Key = key;
    }

    internal byte[] Blob { get; }
    internal byte[] Key { get; }

    /// <summary>Parses <c>ssh-ed25519 BASE64 [comment]</c>, or the line of an <c>allowed_signers</c> file.</summary>
    public static SshPublicKey Parse(string text)
    {
        var fields = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(fields, "ssh-ed25519");
        if (at < 0 || at + 1 >= fields.Length) throw new FormatException("Not an ssh-ed25519 public key");
        var blob = Convert.FromBase64String(fields[at + 1]);
        var reader = new SshReader(blob);
        if (reader.String() != "ssh-ed25519") throw new FormatException("Not an ssh-ed25519 public key");
        var key = reader.Bytes();
        if (key.Length != 32 || !reader.AtEnd) throw new FormatException("Not an ssh-ed25519 public key");
        return new SshPublicKey(blob, key);
    }
}

/// <summary>
/// Verifies OpenSSH's detached signatures (<c>ssh-keygen -Y sign</c>, PROTOCOL.sshsig) made with
/// one trusted Ed25519 key under one namespace.
/// </summary>
public static class SshSignature
{
    static readonly byte[] Magic = "SSHSIG"u8.ToArray();

    public static bool Verify(ReadOnlySpan<byte> message, string armored, SshPublicKey trusted, string ns)
    {
        try
        {
            var blob = Dearmor(armored);
            if (!blob.AsSpan().StartsWith(Magic)) return false;
            var reader = new SshReader(blob, Magic.Length);
            if (reader.UInt32() != 1) return false;
            var publicKey = reader.Bytes();
            var signedNamespace = reader.String();
            var reserved = reader.Bytes();
            var hashAlgorithm = reader.String();
            var signature = reader.Bytes();
            if (!reader.AtEnd || !publicKey.AsSpan().SequenceEqual(trusted.Blob) || signedNamespace != ns) return false;
            var hash = hashAlgorithm switch
            {
                "sha512" => SHA512.HashData(message),
                "sha256" => SHA256.HashData(message),
                _ => null,
            };
            if (hash is null) return false;
            var signatureReader = new SshReader(signature);
            if (signatureReader.String() != "ssh-ed25519") return false;
            var raw = signatureReader.Bytes();
            if (raw.Length != 64 || !signatureReader.AtEnd) return false;

            using var signed = new MemoryStream();
            signed.Write(Magic);
            WriteString(signed, Encoding.ASCII.GetBytes(ns));
            WriteString(signed, reserved);
            WriteString(signed, Encoding.ASCII.GetBytes(hashAlgorithm));
            WriteString(signed, hash);
            var data = signed.ToArray();
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(trusted.Key, 0));
            verifier.BlockUpdate(data, 0, data.Length);
            return verifier.VerifySignature(raw);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }

    static byte[] Dearmor(string armored)
    {
        const string begin = "-----BEGIN SSH SIGNATURE-----";
        const string end = "-----END SSH SIGNATURE-----";
        var start = armored.IndexOf(begin, StringComparison.Ordinal);
        var stop = armored.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop < start) throw new FormatException("Not an SSH signature");
        var body = armored[(start + begin.Length)..stop];
        return Convert.FromBase64String(string.Concat(body.Where(c => !char.IsWhiteSpace(c))));
    }

    static void WriteString(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}

sealed class SshReader(byte[] data, int position = 0)
{
    int position = position;

    public bool AtEnd => position == data.Length;

    public uint UInt32()
    {
        if (data.Length - position < 4) throw new FormatException("Truncated");
        var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
        position += 4;
        return value;
    }

    public byte[] Bytes()
    {
        var length = UInt32();
        if (length > data.Length - position) throw new FormatException("Truncated");
        var value = data.AsSpan(position, (int)length).ToArray();
        position += (int)length;
        return value;
    }

    public string String() => Encoding.ASCII.GetString(Bytes());
}
