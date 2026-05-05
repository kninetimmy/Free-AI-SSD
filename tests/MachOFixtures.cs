using System.Buffers.Binary;

namespace FreeAiSsd.Tests;

/// <summary>
/// Synthesized Mach-O header bytes for testing. We don't need real executables
/// — only enough of the header for <c>MachOArchInspector</c> to identify the
/// architecture(s). Building these by hand keeps the tests self-contained
/// (no fixture binaries checked into the repo).
/// </summary>
internal static class MachOFixtures
{
    private const uint CpuArchAbi64 = 0x01000000;
    private const uint CpuTypeX86 = 7;
    private const uint CpuTypeArm = 12;
    public const uint CpuTypeX86_64 = CpuArchAbi64 | CpuTypeX86;
    public const uint CpuTypeArm64 = CpuArchAbi64 | CpuTypeArm;

    public static byte[] SingleMachO64(bool x86_64)
    {
        var bytes = new byte[28];
        // mach_header_64: magic (4), cputype (4), cpusubtype (4), filetype (4),
        // ncmds (4), sizeofcmds (4), flags (4), reserved (4). We only fill in
        // magic + cputype; the rest stays zero.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0xFEEDFACF); // MH_MAGIC_64
        var cpuType = x86_64 ? CpuTypeX86_64 : CpuTypeArm64;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), cpuType);
        return bytes;
    }

    public static byte[] FatUniversalArm64AndX86()
    {
        // FAT_MAGIC + nfat_arch=2, then two fat_arch entries (20 bytes each).
        // Big-endian on disk per the Mach-O spec.
        var bytes = new byte[8 + 20 + 20];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 2u);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), CpuTypeX86_64);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28, 4), CpuTypeArm64);
        return bytes;
    }

    public static byte[] FatUniversalX86Only()
    {
        var bytes = new byte[8 + 20];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), CpuTypeX86_64);
        return bytes;
    }

    public static byte[] FatUniversal64Arm64()
    {
        // FAT_MAGIC_64 — fat_arch_64 entries are 32 bytes each.
        var bytes = new byte[8 + 32];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xCAFEBABF);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 1u);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), CpuTypeArm64);
        return bytes;
    }

    public static byte[] BogusJavaClassFile()
    {
        // Java class files also start with 0xCAFEBABE, but the next four bytes
        // are minor/major version (e.g. 0x0000_003C for Java 8). The Mach-O
        // inspector should treat anything where nfat_arch is implausibly large
        // as not-a-fat-Mach-O.
        var bytes = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0xCAFEBABE);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 0x0000003C);
        return bytes;
    }
}
