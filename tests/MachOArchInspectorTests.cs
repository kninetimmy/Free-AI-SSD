using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for the cross-platform Mach-O inspector. Critical for the MAC4 trust
/// gate: the Apple Silicon (arm64) check runs from Windows-side PrepApp, so
/// it must not depend on <c>lipo</c> or any Mac-only tooling.
/// </summary>
public sealed class MachOArchInspectorTests
{
    [Fact]
    public void ContainsArm64_SingleMachO_Arm64_True()
    {
        using var temp = new TempBinary(MachOFixtures.SingleMachO64(x86_64: false));
        Assert.True(MachOArchInspector.ContainsArm64Slice(temp.Path));
    }

    [Fact]
    public void ContainsArm64_SingleMachO_X86_64_False()
    {
        using var temp = new TempBinary(MachOFixtures.SingleMachO64(x86_64: true));
        Assert.False(MachOArchInspector.ContainsArm64Slice(temp.Path));
    }

    [Fact]
    public void ContainsArm64_FatUniversalWithArm64_True()
    {
        using var temp = new TempBinary(MachOFixtures.FatUniversalArm64AndX86());
        Assert.True(MachOArchInspector.ContainsArm64Slice(temp.Path));
    }

    [Fact]
    public void ContainsArm64_FatUniversalX86Only_False()
    {
        using var temp = new TempBinary(MachOFixtures.FatUniversalX86Only());
        Assert.False(MachOArchInspector.ContainsArm64Slice(temp.Path));
    }

    [Fact]
    public void ContainsArm64_Fat64WithArm64_True()
    {
        using var temp = new TempBinary(MachOFixtures.FatUniversal64Arm64());
        Assert.True(MachOArchInspector.ContainsArm64Slice(temp.Path));
    }

    [Fact]
    public void ContainsArm64_NonMachOFile_False()
    {
        using var temp = new TempBinary(System.Text.Encoding.UTF8.GetBytes("plain text content not a mach-o"));
        Assert.False(MachOArchInspector.ContainsArm64Slice(temp.Path));
    }

    [Fact]
    public void ContainsArm64_JavaClassFile_False()
    {
        // Java class files share the 0xCAFEBABE magic; the inspector must
        // refuse to interpret them as fat Mach-O headers, otherwise it could
        // misidentify a .class file with major-version 12 as containing arm64.
        using var temp = new TempBinary(MachOFixtures.BogusJavaClassFile());
        Assert.False(MachOArchInspector.ContainsArm64Slice(temp.Path));
    }

    [Fact]
    public void ContainsArm64_MissingFile_False()
    {
        var path = Path.Combine(Path.GetTempPath(), $"freeaissd-missing-{Guid.NewGuid():N}");
        Assert.False(MachOArchInspector.ContainsArm64Slice(path));
    }
}
