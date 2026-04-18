using FreeAiSsd.Shared.Services;
using Xunit;

namespace FreeAiSsd.Tests;

public class DriveFormatCommandTests
{
    [Theory]
    [InlineData("D:\\", 'D')]
    [InlineData("d:\\", 'D')]
    [InlineData("E:", 'E')]
    [InlineData("  F:\\  ", 'F')]
    public void Build_ParsesDriveLetter_FromCommonRootForms(string root, char expected)
    {
        var built = DriveFormatCommand.Build(root, "Portable AI", "NTFS");
        Assert.Equal(expected, built.DriveLetter);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("D")]
    [InlineData("1:\\")]
    [InlineData("\\\\server\\share")]
    public void Build_RejectsInvalidRoot(string root)
    {
        Assert.Throws<System.ArgumentException>(() =>
            DriveFormatCommand.Build(root, "Portable AI", "NTFS"));
    }

    [Theory]
    [InlineData("FAT32")]
    [InlineData("exFAT")]
    [InlineData("refs")]
    public void Build_RejectsNonNtfsFileSystem(string fs)
    {
        Assert.Throws<System.ArgumentException>(() =>
            DriveFormatCommand.Build("D:\\", "Portable AI", fs));
    }

    [Fact]
    public void Build_DefaultsMissingFileSystemToNtfs()
    {
        var built = DriveFormatCommand.Build("D:\\", "Portable AI", "");
        Assert.Contains(built.Arguments, a => a.Contains("-FileSystem NTFS"));
    }

    [Fact]
    public void Build_UsesPowerShellExe()
    {
        var built = DriveFormatCommand.Build("D:\\", "AI", "NTFS");
        Assert.Equal("powershell.exe", built.FileName);
    }

    [Fact]
    public void Build_PassesLabelViaEnvironment_NotCommandLine()
    {
        // Label with adversarial characters must never appear inline in the
        // PowerShell command. It must come through $env: only. Keep under
        // MaxLabelLength so the sanitizer doesn't truncate for this test.
        var nastyLabel = "'; rm C:\\ #";
        var built = DriveFormatCommand.Build("D:\\", nastyLabel, "NTFS");

        Assert.Equal(nastyLabel, built.Environment[DriveFormatCommand.LabelEnvVar]);
        foreach (var arg in built.Arguments)
        {
            Assert.DoesNotContain("rm C:\\", arg);
            Assert.DoesNotContain(nastyLabel, arg);
        }
    }

    [Fact]
    public void Build_CommandReferencesEnvironmentVariable()
    {
        var built = DriveFormatCommand.Build("D:\\", "AI", "NTFS");
        var commandArg = Assert.Single(built.Arguments, a => a.Contains("Format-Volume"));
        Assert.Contains($"$env:{DriveFormatCommand.LabelEnvVar}", commandArg);
    }

    [Fact]
    public void Build_CommandUsesNonInteractiveAndBypassExecutionPolicy()
    {
        var built = DriveFormatCommand.Build("D:\\", "AI", "NTFS");
        Assert.Contains("-NoProfile", built.Arguments);
        Assert.Contains("-NonInteractive", built.Arguments);
        Assert.Contains("-ExecutionPolicy", built.Arguments);
        Assert.Contains("Bypass", built.Arguments);
    }

    [Fact]
    public void Build_CommandUsesConfirmFalseAndForce()
    {
        var built = DriveFormatCommand.Build("D:\\", "AI", "NTFS");
        var commandArg = Assert.Single(built.Arguments, a => a.Contains("Format-Volume"));
        Assert.Contains("-Confirm:$false", commandArg);
        Assert.Contains("-Force", commandArg);
    }

    [Fact]
    public void SanitizeLabel_TrimsAndCapsLength()
    {
        var longLabel = new string('A', 100);
        var built = DriveFormatCommand.Build("D:\\", longLabel, "NTFS");
        var stored = built.Environment[DriveFormatCommand.LabelEnvVar];
        Assert.Equal(DriveFormatCommand.MaxLabelLength, stored.Length);
    }

    [Fact]
    public void SanitizeLabel_StripsControlCharacters()
    {
        // Use \0 (null) rather than \x00 here — C# \x is variable-length
        // hex and would greedily consume the 'a' that follows.
        var built = DriveFormatCommand.Build("D:\\", "Port\0able\nAI\r", "NTFS");
        var stored = built.Environment[DriveFormatCommand.LabelEnvVar];
        Assert.Equal("PortableAI", stored);
    }

    [Fact]
    public void SanitizeLabel_EmptyInputAllowed()
    {
        var built = DriveFormatCommand.Build("D:\\", "", "NTFS");
        Assert.Equal(string.Empty, built.Environment[DriveFormatCommand.LabelEnvVar]);
    }
}
