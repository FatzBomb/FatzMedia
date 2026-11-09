using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace FatzMedia.Video.Core.Files;

public enum CpuType { Unknown, X86, X64, Arm64 }

public enum FileCpuType
{
    Unknown,
    NonPe,

    // Native
    NativeX86,
    NativeX64,
    NativeArm64,

    // Managed (IL)
    ManagedAnyCpu,
    ManagedAnyCpuPrefer32,
    ManagedX86Required,
    ManagedX64,
    ManagedArm64
}

public readonly struct CompatibilityInfo
{
    public CpuType CurrentProcess { get; }
    public FileCpuType FileCpu { get; }
    public bool IsMatch { get; }        // exact arch match (not just compatible)
    public bool IsCompatible { get; }   // safe to load/use in this process

    public CompatibilityInfo(CpuType proc, FileCpuType file, bool isMatch, bool isCompat)
        => (CurrentProcess, FileCpu, IsMatch, IsCompatible) = (proc, file, isMatch, isCompat);
}

public static class BinaryCompatibility
{
    public static CompatibilityInfo Analyze(string path)
    {
        var proc = GetProcessCpu();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new(proc, FileCpuType.Unknown, isMatch: false, isCompat: false);

        // Non-Windows: we only understand PE; treat files as not system-dependent
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new(proc, FileCpuType.Unknown, isMatch: false, isCompat: true);

        FileCpuType fileCpu;
        try
        {
            if (!IsLikelyPe(path))
                return new(proc, FileCpuType.NonPe, isMatch: true, isCompat: true); // not system-dependent

            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            var h = pe.PEHeaders;
            var machine = h.CoffHeader.Machine;
            var isPE32Plus = h.PEHeader?.Magic == PEMagic.PE32Plus; // 64-bit image?
            var cor = h.CorHeader;

            if (cor is not null)
            {
                var flags = cor.Flags;
                var ilOnly = (flags & CorFlags.ILOnly) != 0;
                var req32 = (flags & CorFlags.Requires32Bit) != 0;
                var pref32 = (flags & CorFlags.Prefers32Bit) != 0;

                fileCpu = ilOnly && !req32 && !isPE32Plus
                    ? pref32 ? FileCpuType.ManagedAnyCpuPrefer32 : FileCpuType.ManagedAnyCpu
                    : req32
                        ? FileCpuType.ManagedX86Required
                        : isPE32Plus
                            ? machine switch
                            {
                                Machine.Amd64 => FileCpuType.ManagedX64,
                                Machine.Arm64 => FileCpuType.ManagedArm64,
                                _ => FileCpuType.Unknown
                            }
                            : FileCpuType.Unknown;
            }
            else
            {
                fileCpu = machine switch
                {
                    Machine.I386 => FileCpuType.NativeX86,
                    Machine.Amd64 => FileCpuType.NativeX64,
                    Machine.Arm64 => FileCpuType.NativeArm64,
                    _ => FileCpuType.Unknown
                };
            }
        }
        catch
        {
            return new(proc, FileCpuType.Unknown, isMatch: false, isCompat: false);
        }

        // Decide compatibility + exact match
        (var isCompat, var isMatch) = Evaluate(proc, fileCpu);
        return new(proc, fileCpu, isMatch, isCompat);
    }

    // --- helpers ---

    static (bool compat, bool match) Evaluate(CpuType proc, FileCpuType file) => file switch
    {
        FileCpuType.NonPe => (true, true),  // not system-dependent
        FileCpuType.ManagedAnyCpu => (true, false), // compatible everywhere; not exact
        FileCpuType.ManagedAnyCpuPrefer32 => (true, false),

        FileCpuType.ManagedX86Required => (proc == CpuType.X86, proc == CpuType.X86),
        FileCpuType.ManagedX64 => (proc == CpuType.X64, proc == CpuType.X64),
        FileCpuType.ManagedArm64 => (proc == CpuType.Arm64, proc == CpuType.Arm64),

        FileCpuType.NativeX86 => (proc == CpuType.X86, proc == CpuType.X86),
        FileCpuType.NativeX64 => (proc == CpuType.X64, proc == CpuType.X64),
        FileCpuType.NativeArm64 => (proc == CpuType.Arm64, proc == CpuType.Arm64),

        _ => (false, false)
    };

    static CpuType GetProcessCpu() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X86 => CpuType.X86,
        Architecture.X64 => CpuType.X64,
        Architecture.Arm64 => CpuType.Arm64,
        _ => CpuType.Unknown
    };

    /// <summary>Cheap check for Windows PE magic ("MZ" + "PE\0\0").</summary>
    static bool IsLikelyPe(string path)
    {
        using var fs = File.OpenRead(path);
        if (fs.Length < 64) return false;

        Span<byte> b = stackalloc byte[4];

        // "MZ"
        fs.ReadExactly(b[..2]);
        if (b[0] != (byte)'M' || b[1] != (byte)'Z') return false;

        // e_lfanew -> "PE\0\0"
        fs.Position = 0x3C;
        fs.ReadExactly(b);
        var peOff = BitConverter.ToInt32(b);
        if (peOff < 0 || peOff > fs.Length - 4) return false;

        fs.Position = peOff;
        fs.ReadExactly(b);
        return b[0] == (byte)'P' && b[1] == (byte)'E' && b[2] == 0 && b[3] == 0;
    }
}
