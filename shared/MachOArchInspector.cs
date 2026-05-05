using System.Buffers.Binary;
using System.Collections.Generic;

namespace FreeAiSsd.Shared;

/// <summary>
/// Pure-managed Mach-O header inspector. Reads the magic bytes and (for fat
/// universal binaries) the architecture table to determine which CPU types
/// are present without shelling out to <c>lipo</c>. This lets the Windows-side
/// PrepApp validate that the bundled macOS Ollama payload contains an arm64
/// slice before it is staged onto the SSD, and lets the runtime trust gate
/// reject pure-x86_64 payloads on Apple Silicon.
/// </summary>
public static class MachOArchInspector
{
    // CPU type constants from <mach/machine.h>. The CPU_ARCH_ABI64 (0x01000000)
    // bit distinguishes 64-bit variants from their 32-bit predecessors.
    private const uint CpuArchAbi64 = 0x01000000;
    private const uint CpuTypeX86 = 7;
    private const uint CpuTypeArm = 12;
    private const uint CpuTypeX86_64 = CpuArchAbi64 | CpuTypeX86;
    private const uint CpuTypeArm64 = CpuArchAbi64 | CpuTypeArm;

    // Magic constants below are the values produced by reading the file's
    // first four bytes as BIG-ENDIAN. The file's *internal* byte order is
    // implied by which magic matched:
    //
    //   raw bytes "CA FE BA BE" -> BE-read 0xCAFEBABE  -> fat header (always BE on disk)
    //   raw bytes "CF FA ED FE" -> BE-read 0xCFFAEDFE  -> thin Mach-O 64, internal LE  (modern arm64/x86_64)
    //   raw bytes "FE ED FA CF" -> BE-read 0xFEEDFACF  -> thin Mach-O 64, internal BE  (legacy)
    //   raw bytes "CE FA ED FE" -> BE-read 0xCEFAEDFE  -> thin Mach-O 32, internal LE
    //   raw bytes "FE ED FA CE" -> BE-read 0xFEEDFACE  -> thin Mach-O 32, internal BE
    private const uint FatMagic = 0xCAFEBABE;
    private const uint FatMagic64 = 0xCAFEBABF;
    private const uint ThinMagic64Le = 0xCFFAEDFE;
    private const uint ThinMagic64Be = 0xFEEDFACF;
    private const uint ThinMagic32Le = 0xCEFAEDFE;
    private const uint ThinMagic32Be = 0xFEEDFACE;

    // Cap on the number of fat slices we'll iterate through. Real universal
    // binaries have 2-3 architectures; anything past this is almost certainly
    // a misidentified file (e.g. a Java class file, which also starts with
    // 0xCAFEBABE) and should be rejected rather than processed.
    private const int MaxFatArchitectures = 16;

    /// <summary>
    /// Returns true if the file at <paramref name="path"/> is a Mach-O binary
    /// (or fat universal binary) that contains an arm64 slice. Pure x86_64,
    /// non-Mach-O files, and unreadable files all return false.
    /// </summary>
    public static bool ContainsArm64Slice(string path)
    {
        foreach (var cpuType in ReadCpuTypes(path))
        {
            if (cpuType == CpuTypeArm64) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns the set of CPU types declared in the Mach-O header(s) at
    /// <paramref name="path"/>. Empty for non-Mach-O or unreadable files.
    /// </summary>
    public static IReadOnlyList<uint> ReadCpuTypes(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadCpuTypes(stream);
        }
        catch
        {
            return Array.Empty<uint>();
        }
    }

    internal static IReadOnlyList<uint> ReadCpuTypes(Stream stream)
    {
        if (!stream.CanRead) return Array.Empty<uint>();

        Span<byte> magicBytes = stackalloc byte[4];
        if (!TryReadExactly(stream, magicBytes)) return Array.Empty<uint>();

        // Magic is the first 4 bytes. We read it as big-endian to identify
        // which kind of Mach-O we're looking at; each variant below re-reads
        // the cputype (and fat_arch entries) using the correct endianness
        // for that format. Fat headers are always BE on disk; thin Mach-Os
        // carry their own byte order in their magic.
        var magicBe = BinaryPrimitives.ReadUInt32BigEndian(magicBytes);

        return magicBe switch
        {
            FatMagic => ReadFatArchitectures(stream, sixtyFourBit: false),
            FatMagic64 => ReadFatArchitectures(stream, sixtyFourBit: true),
            ThinMagic64Le => ReadThinCpuType(stream, bigEndian: false),
            ThinMagic64Be => ReadThinCpuType(stream, bigEndian: true),
            ThinMagic32Le => ReadThinCpuType(stream, bigEndian: false),
            ThinMagic32Be => ReadThinCpuType(stream, bigEndian: true),
            _ => Array.Empty<uint>(),
        };
    }

    private static IReadOnlyList<uint> ReadThinCpuType(Stream stream, bool bigEndian)
    {
        // mach_header layout: magic (read), cputype, cpusubtype, filetype, ...
        Span<byte> cpuBytes = stackalloc byte[4];
        if (!TryReadExactly(stream, cpuBytes)) return Array.Empty<uint>();
        var cpuType = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(cpuBytes)
            : BinaryPrimitives.ReadUInt32LittleEndian(cpuBytes);
        return new[] { cpuType };
    }

    private static IReadOnlyList<uint> ReadFatArchitectures(Stream stream, bool sixtyFourBit)
    {
        // fat_header layout: magic (read), nfat_arch. Fat headers are always
        // big-endian on disk, regardless of host byte order, per the Mach-O
        // ABI documentation.
        Span<byte> nFatBytes = stackalloc byte[4];
        if (!TryReadExactly(stream, nFatBytes)) return Array.Empty<uint>();

        var nFat = BinaryPrimitives.ReadUInt32BigEndian(nFatBytes);
        if (nFat == 0 || nFat > MaxFatArchitectures) return Array.Empty<uint>();

        // fat_arch (32-bit fat): cputype, cpusubtype, offset, size, align (5 * uint32 = 20 bytes).
        // fat_arch_64: cputype, cpusubtype, offset (uint64), size (uint64), align, reserved (32 bytes).
        var entrySize = sixtyFourBit ? 32 : 20;
        var buffer = new byte[entrySize];
        var results = new uint[nFat];
        for (var i = 0; i < nFat; i++)
        {
            if (!TryReadExactly(stream, buffer)) return Array.Empty<uint>();
            results[i] = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4));
        }
        return results;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }
}
