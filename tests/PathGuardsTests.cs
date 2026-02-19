using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for PathGuards — the path traversal prevention utility.
/// Validates that sibling directories with overlapping prefixes are not
/// treated as children (e.g., /app vs /app2), and that case sensitivity
/// is correctly enforced per platform.
/// </summary>
public sealed class PathGuardsTests
{
    /// <summary>
    /// Ensures /app2/file.bin is NOT considered under /app even though
    /// the path starts with the same prefix. The trailing separator check
    /// must prevent this false positive.
    /// </summary>
    [Fact]
    public void IsPathUnderRoot_DoesNotTreatSiblingAsChild_UnixStyle()
    {
        Assert.False(PathGuards.IsPathUnderRoot("/Users/me/app", "/Users/me/app2/file.bin", isWindows: false));
    }

    /// <summary>
    /// Same sibling boundary test for Windows-style paths (C:\Root vs C:\Root2).
    /// Also verifies that actual children (C:\Root\file) are correctly detected.
    /// </summary>
    [Fact]
    public void IsPathUnderRoot_WindowsBoundaryIsRespected()
    {
        Assert.False(PathGuards.IsPathUnderRoot("C:\\Root", "C:\\Root2\\file", isWindows: true));
        Assert.True(PathGuards.IsPathUnderRoot("C:\\Root", "C:\\Root\\file", isWindows: true));
    }

    /// <summary>
    /// On non-Windows (Linux/macOS), path comparison must be case-sensitive.
    /// /Users/me/App is NOT the same as /Users/me/app.
    /// </summary>
    [Fact]
    public void IsPathUnderRoot_IsCaseSensitiveOnNonWindows()
    {
        Assert.False(PathGuards.IsPathUnderRoot("/Users/me/App", "/Users/me/app/file.bin", isWindows: false));
    }
}
