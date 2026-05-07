using FreeAiSsd.Shared.Services;
using Xunit;

namespace FreeAiSsd.Tests;

public class DiskutilFormatCommandTests
{
    [Theory]
    [InlineData("disk2", "disk2")]
    [InlineData("disk20", "disk20")]
    [InlineData("disk2s1", "disk2s1")]
    [InlineData("/dev/disk2", "disk2")]
    [InlineData("/dev/disk2s1", "disk2s1")]
    [InlineData("  disk3  ", "disk3")]
    public void Build_ParsesDiskIdentifier_FromCommonForms(string identifier, string expected)
    {
        var built = DiskutilFormatCommand.Build(identifier, "FREEAI", "ExFAT");
        Assert.Equal(expected, built.DiskIdentifier);
        Assert.Contains(expected, built.Arguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("disk")]
    [InlineData("disks2")]
    [InlineData("disk2s")]
    [InlineData("disk2sX")]
    [InlineData("disk2x1")]
    [InlineData("/dev/sda")]
    [InlineData("hd0")]
    public void Build_RejectsInvalidIdentifier(string identifier)
    {
        Assert.Throws<System.ArgumentException>(() =>
            DiskutilFormatCommand.Build(identifier, "FREEAI", "ExFAT"));
    }

    [Theory]
    [InlineData("ExFAT")]
    [InlineData("EXFAT")]
    [InlineData("exfat")]
    [InlineData("exFAT")]
    public void Build_AcceptsExFat_AndEmitsCanonicalCasing(string input)
    {
        var built = DiskutilFormatCommand.Build("disk2", "FREEAI", input);
        // diskutil's eraseDisk format token is the literal "ExFAT" (mixed case).
        Assert.Equal("ExFAT", built.Arguments[1]);
    }

    [Theory]
    [InlineData("APFS")]
    [InlineData("apfs")]
    public void Build_RejectsApfs_DeferredPerMac17Mvp(string fs)
    {
        var ex = Assert.Throws<System.ArgumentException>(() =>
            DiskutilFormatCommand.Build("disk2", "FREEAI", fs));
        Assert.Contains("APFS", ex.Message);
    }

    [Theory]
    [InlineData("NTFS")]
    [InlineData("ntfs")]
    public void Build_RejectsNtfs_WindowsOnly(string fs)
    {
        var ex = Assert.Throws<System.ArgumentException>(() =>
            DiskutilFormatCommand.Build("disk2", "FREEAI", fs));
        Assert.Contains("NTFS", ex.Message);
    }

    [Theory]
    [InlineData("FAT32")]
    [InlineData("HFS+")]
    [InlineData("MSDOS")]
    public void Build_RejectsUnsupportedFileSystems(string fs)
    {
        Assert.Throws<System.ArgumentException>(() =>
            DiskutilFormatCommand.Build("disk2", "FREEAI", fs));
    }

    [Fact]
    public void Build_DefaultsMissingFileSystemToExFat()
    {
        var built = DiskutilFormatCommand.Build("disk2", "FREEAI", "");
        Assert.Equal("ExFAT", built.Arguments[1]);
    }

    [Fact]
    public void Build_UsesAbsoluteDiskutilPath()
    {
        var built = DiskutilFormatCommand.Build("disk2", "FREEAI", "ExFAT");
        Assert.Equal("/usr/sbin/diskutil", built.FileName);
    }

    [Fact]
    public void Build_EmitsExpectedArgvShape()
    {
        // The destructive command shape is: eraseDisk <fs> <label> MBR <id>
        // This is the parity-pin reviewers (and Windows CI) inspect to verify
        // what Swift will send to /usr/sbin/diskutil. Any drift here fails CI
        // before the Swift code even compiles.
        var built = DiskutilFormatCommand.Build("disk2", "FREEAI", "ExFAT");
        Assert.Equal(new[] { "eraseDisk", "ExFAT", "FREEAI", "MBR", "disk2" }, built.Arguments);
    }

    [Fact]
    public void Build_PinsMbrPartitionScheme_NotGptOrApm()
    {
        // MBR is the cross-platform-safe default for exFAT external drives.
        // Drift toward GPT or APM here would silently break some Windows
        // readers; pin the choice in the argv test.
        var built = DiskutilFormatCommand.Build("disk2", "FREEAI", "ExFAT");
        Assert.Contains("MBR", built.Arguments);
        Assert.DoesNotContain("GPT", built.Arguments);
        Assert.DoesNotContain("APM", built.Arguments);
    }

    [Theory]
    [InlineData("FREEAI", "FREEAI")]
    [InlineData("  FREEAI  ", "FREEAI")]
    [InlineData("Free AI SSD", "Free AI SSD")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "abcdefghij")]
    public void SanitizeLabel_RemovesMetacharactersAndTrims(string input, string expected)
    {
        Assert.Equal(expected, DiskutilFormatCommand.SanitizeLabel(input));
    }

    [Fact]
    public void SanitizeLabel_StripsControlCharacters()
    {
        Assert.Equal("AB", DiskutilFormatCommand.SanitizeLabel("AB"));
    }

    [Fact]
    public void SanitizeLabel_CapsAt15Characters()
    {
        var input = new string('X', 32);
        var sanitized = DiskutilFormatCommand.SanitizeLabel(input);
        Assert.Equal(15, sanitized.Length);
    }

    [Fact]
    public void Build_SubstitutesSingleSpace_WhenLabelSanitizesToEmpty()
    {
        // diskutil refuses an empty label argument. Build substitutes a single
        // space so the user can rename the volume after format rather than
        // having the whole format call fail on empty input.
        var built = DiskutilFormatCommand.Build("disk2", "///\\\\:::", "ExFAT");
        Assert.Equal(" ", built.Arguments[2]);
    }

    [Fact]
    public void Build_SanitizedLabel_StaysUnder15Chars_EvenForOversizedInput()
    {
        var built = DiskutilFormatCommand.Build("disk2", new string('X', 32), "ExFAT");
        Assert.True(built.Arguments[2].Length <= 15);
    }

    [Fact]
    public void Describe_RendersHumanReadableForm_WithoutLeakingSecrets()
    {
        var built = DiskutilFormatCommand.Build("disk2", "FREEAI", "ExFAT");
        var description = DiskutilFormatCommand.Describe(built);
        Assert.Contains("/usr/sbin/diskutil", description);
        Assert.Contains("disk2", description);
        Assert.Contains("eraseDisk ExFAT FREEAI MBR disk2", description);
    }
}
