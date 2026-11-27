using System.Runtime.InteropServices;

namespace Gizlet.Core.Packages;

/// <summary>Which package targets this Windows machine runs, best first.</summary>
public sealed class WindowsTarget
{
    public const ushort MachineAmd64 = 0x8664;
    public const ushort MachineArm64 = 0xAA64;

    static readonly string[] Environments = ["msvc", "gnullvm", "gnu"];

    public WindowsTarget(Architecture architecture)
    {
        Architecture = architecture;
        Architectures = architecture switch
        {
            Architecture.X64 => ["x86_64"],
            // Windows 11 on Arm runs x64 programs under emulation.
            Architecture.Arm64 => ["aarch64", "x86_64"],
            _ => [],
        };
    }

    public static WindowsTarget Current { get; } = new(RuntimeInformation.OSArchitecture);

    public Architecture Architecture { get; }

    /// <summary>Target triple architectures this machine runs, native first.</summary>
    public IReadOnlyList<string> Architectures { get; }

    /// <summary>The triple the Store is asked for first.</summary>
    public string Preferred => Architectures.Count > 0 ? $"{Architectures[0]}-pc-windows-msvc" : "unsupported";

    /// <summary>Every triple the Store may be asked for, best first.</summary>
    public IEnumerable<string> Triples => Architectures.SelectMany(arch => Environments.Select(env => $"{arch}-pc-windows-{env}"));

    /// <summary>Whether a package built for <paramref name="target"/> runs here.</summary>
    public bool Runs(string target) => Machine(target) is not null;

    /// <summary>The PE machine type a package for <paramref name="target"/> must carry; null when it does not run here.</summary>
    public ushort? Machine(string target)
    {
        var parts = target.Split('-');
        if (parts.Length != 4 || parts[1] != "pc" || parts[2] != "windows" || !Environments.Contains(parts[3])) return null;
        if (!Architectures.Contains(parts[0])) return null;
        return parts[0] switch
        {
            "x86_64" => MachineAmd64,
            "aarch64" => MachineArm64,
            _ => null,
        };
    }
}
