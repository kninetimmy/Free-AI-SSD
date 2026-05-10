using FreeAiSsd.Shared.Services;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// Pins the rendering of <see cref="OllamaPullProgress"/> frames to
/// the single human-readable line the PrepApp's in-place progress
/// label consumes. Both consumers (mac-prep-host's EmitProgress and
/// the Windows PrepViewModel's SetPullProgressLineSafe) call
/// <see cref="OllamaPullProgress.ToDisplayString"/> directly, so this
/// is the seam the v1.3.21 field test will validate.
/// </summary>
public sealed class OllamaPullProgressTests
{
    [Fact]
    public void ToDisplayString_RendersStageFramesAsStatusOnly()
    {
        var stage = new OllamaPullProgress("pulling manifest", null, null, null);
        Assert.Equal("pulling manifest", stage.ToDisplayString());

        var verifying = new OllamaPullProgress("verifying sha256 digest", null, null, null);
        Assert.Equal("verifying sha256 digest", verifying.ToDisplayString());

        var success = new OllamaPullProgress("success", null, null, null);
        Assert.Equal("success", success.ToDisplayString());
    }

    [Fact]
    public void ToDisplayString_RendersLayerFramesWithPercentAndBytes()
    {
        // 4.7 GB total / 3.9 GB completed = 83% — the exact frame
        // shape the v1.3.20 field test screenshot captured.
        var layer = new OllamaPullProgress(
            "pulling 96c415656d37",
            "sha256:96c415656d37",
            Total: 4_700_000_000L,
            Completed: 3_900_000_000L);

        var rendered = layer.ToDisplayString();

        Assert.Contains("pulling 96c415656d37", rendered);
        Assert.Contains("83%", rendered);
        Assert.Contains("GB", rendered);
    }

    [Fact]
    public void ToDisplayString_DropsBytesWhenTotalIsZero()
    {
        // Defensive: an early frame may report total=0 before the layer
        // size is known. Avoid a divide-by-zero NaN in the percent
        // expression — fall through to the bare status.
        var early = new OllamaPullProgress("pulling 96c415656d37", "sha256:96c4", 0, 0);
        Assert.Equal("pulling 96c415656d37", early.ToDisplayString());
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2.0 KB")]
    [InlineData(5_000_000L, "4.8 MB")]
    [InlineData(2_500_000_000L, "2.3 GB")]
    public void ToDisplayString_FormatsByteScalesWithoutOverflow(long bytes, string expectedSubstring)
    {
        var frame = new OllamaPullProgress("pulling x", "sha256:x", bytes * 2, bytes);
        Assert.Contains(expectedSubstring, frame.ToDisplayString());
    }
}
