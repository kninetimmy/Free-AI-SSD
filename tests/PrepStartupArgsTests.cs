using FreeAiSsd.PrepApp;
using Xunit;

namespace FreeAiSsd.Tests;

public class PrepStartupArgsTests
{
    [Fact]
    public void Parse_Null_ReturnsEmpty()
    {
        var parsed = PrepStartupArgs.Parse(null);
        Assert.False(parsed.HasAutoResumeIntent);
        Assert.False(parsed.DiagEnabled);
    }

    [Fact]
    public void Parse_EmptyArray_ReturnsEmpty()
    {
        var parsed = PrepStartupArgs.Parse(Array.Empty<string>());
        Assert.False(parsed.HasAutoResumeIntent);
    }

    [Fact]
    public void Parse_ValidAutoResumeFormatAndLabel_NormalizesRoot()
    {
        var parsed = PrepStartupArgs.Parse(new[]
        {
            "--autoresume-format=E:\\",
            "--autoresume-label=Portable AI"
        });

        Assert.True(parsed.HasAutoResumeIntent);
        Assert.Equal("E:\\", parsed.AutoResumeFormatRoot);
        Assert.Equal("Portable AI", parsed.AutoResumeLabel);
    }

    [Fact]
    public void Parse_LowercaseDriveLetter_NormalizesToUppercase()
    {
        var parsed = PrepStartupArgs.Parse(new[]
        {
            "--autoresume-format=e:",
            "--autoresume-label=test"
        });

        Assert.Equal("E:\\", parsed.AutoResumeFormatRoot);
    }

    [Fact]
    public void Parse_InvalidDriveLetter_DropsIntent()
    {
        // Numeric "drive letter" must fail the ParseDriveLetter guard.
        var parsed = PrepStartupArgs.Parse(new[]
        {
            "--autoresume-format=1:\\",
            "--autoresume-label=test"
        });

        Assert.False(parsed.HasAutoResumeIntent);
        Assert.Null(parsed.AutoResumeFormatRoot);
    }

    [Fact]
    public void Parse_NonColonPath_DropsIntent()
    {
        var parsed = PrepStartupArgs.Parse(new[]
        {
            "--autoresume-format=/etc/passwd",
            "--autoresume-label=test"
        });

        Assert.False(parsed.HasAutoResumeIntent);
    }

    [Fact]
    public void Parse_LabelWithControlChars_StripsThem()
    {
        var parsed = PrepStartupArgs.Parse(new[]
        {
            "--autoresume-format=E:",
            "--autoresume-label=abc\0\ndef"
        });

        Assert.Equal("abcdef", parsed.AutoResumeLabel);
    }

    [Fact]
    public void Parse_LabelOverMaxLength_Truncates()
    {
        var longLabel = new string('x', 100);
        var parsed = PrepStartupArgs.Parse(new[]
        {
            "--autoresume-format=E:",
            $"--autoresume-label={longLabel}"
        });

        Assert.Equal(32, parsed.AutoResumeLabel.Length);
    }

    [Fact]
    public void Parse_DiagFlag_SetsDiagEnabled()
    {
        var parsed = PrepStartupArgs.Parse(new[] { "--diag" });
        Assert.True(parsed.DiagEnabled);
        Assert.False(parsed.HasAutoResumeIntent);
    }

    [Fact]
    public void Parse_DiagWithAutoResume_BothSet()
    {
        var parsed = PrepStartupArgs.Parse(new[]
        {
            "--autoresume-format=G:",
            "--autoresume-label=Portable AI",
            "--diag"
        });

        Assert.True(parsed.HasAutoResumeIntent);
        Assert.True(parsed.DiagEnabled);
    }

    [Fact]
    public void Parse_UnknownArg_IsIgnored()
    {
        var parsed = PrepStartupArgs.Parse(new[]
        {
            "--autoresume-format=E:",
            "--unknown=whatever",
            "--autoresume-label=ok"
        });

        Assert.True(parsed.HasAutoResumeIntent);
        Assert.Equal("ok", parsed.AutoResumeLabel);
    }

    [Fact]
    public void BuildRelaunchArgs_IncludesBothFlags()
    {
        var args = PrepStartupArgs.BuildRelaunchArgs("E:\\", "my label", includeDiag: false);
        Assert.Contains("--autoresume-format=E:\\", args);
        Assert.Contains("--autoresume-label=my label", args);
        Assert.DoesNotContain("--diag", args);
    }

    [Fact]
    public void BuildRelaunchArgs_IncludeDiag_AppendsFlag()
    {
        var args = PrepStartupArgs.BuildRelaunchArgs("E:\\", "lbl", includeDiag: true);
        Assert.Contains("--diag", args);
    }

    [Fact]
    public void BuildRelaunchArgs_EmptyLabel_StillBoundToFlag()
    {
        var args = PrepStartupArgs.BuildRelaunchArgs("E:\\", "", includeDiag: false);
        // The label arg must always be present — parser relies on it
        // being paired with the format flag. Empty value is fine.
        Assert.Contains("--autoresume-label=", args);
    }
}
