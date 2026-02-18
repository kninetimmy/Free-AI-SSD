using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class PathGuardsTests
{
    [Fact]
    public void IsPathUnderRoot_DoesNotTreatSiblingAsChild_UnixStyle()
    {
        Assert.False(PathGuards.IsPathUnderRoot("/Users/me/app", "/Users/me/app2/file.bin", isWindows: false));
    }

    [Fact]
    public void IsPathUnderRoot_WindowsBoundaryIsRespected()
    {
        Assert.False(PathGuards.IsPathUnderRoot("C:\\Root", "C:\\Root2\\file", isWindows: true));
        Assert.True(PathGuards.IsPathUnderRoot("C:\\Root", "C:\\Root\\file", isWindows: true));
    }

    [Fact]
    public void IsPathUnderRoot_IsCaseSensitiveOnNonWindows()
    {
        Assert.False(PathGuards.IsPathUnderRoot("/Users/me/App", "/Users/me/app/file.bin", isWindows: false));
    }
}
