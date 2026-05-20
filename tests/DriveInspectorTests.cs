using FreeAiSsd.Shared;
using Xunit;

namespace FreeAiSsd.Tests;

// Task #47: pure-logic coverage for the OS-drive / self-drive exclusion that
// gates the Fixed-drive fallback. The WMI enumeration paths themselves stay
// untested per existing convention (they're system-side, validated by the
// user's repro run against real hardware), but the eligibility filter is
// pure and worth pinning so a future change to the safety rail is loud.
public class DriveInspectorTests
{
    [Fact]
    public void IsExcludedRoot_MatchesOsRoot_CaseInsensitive()
    {
        Assert.True(DriveInspector.IsExcludedRoot("C:\\", "c:\\", selfRoot: null));
        Assert.True(DriveInspector.IsExcludedRoot("c:\\", "C:\\", selfRoot: null));
    }

    [Fact]
    public void IsExcludedRoot_MatchesSelfRoot()
    {
        Assert.True(DriveInspector.IsExcludedRoot("D:\\", osRoot: "C:\\", selfRoot: "D:\\"));
    }

    [Fact]
    public void IsExcludedRoot_ReturnsFalseForUnrelatedDrive()
    {
        Assert.False(DriveInspector.IsExcludedRoot("E:\\", osRoot: "C:\\", selfRoot: "D:\\"));
    }

    [Fact]
    public void IsExcludedRoot_ReturnsFalseWhenBothExclusionsAreNull()
    {
        Assert.False(DriveInspector.IsExcludedRoot("E:\\", osRoot: null, selfRoot: null));
    }

    [Fact]
    public void IsExcludedRoot_ReturnsFalseForEmptyInput()
    {
        Assert.False(DriveInspector.IsExcludedRoot(string.Empty, osRoot: "C:\\", selfRoot: "D:\\"));
    }
}
