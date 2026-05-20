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

    // ── #49: friendly-label overload ────────────────────────────────

    [Fact]
    public void ToDisplayString_WithParent_ReplacesBlobHashWithModelName()
    {
        // The user-reported case: "pulling e73cc17c7181" surfaced raw as
        // a 6.9 GB "undisclosed" download. With parent context the line
        // names the model instead of the opaque blob digest.
        var layer = new OllamaPullProgress(
            "pulling e73cc17c7181",
            "sha256:e73cc17c7181",
            Total: 6_900_000_000L,
            Completed: 3_450_000_000L);

        var rendered = layer.ToDisplayString("llama3.1:8b", layerIndex: 1, layerCount: 3);

        Assert.DoesNotContain("e73cc17c7181", rendered);
        Assert.Contains("llama3.1:8b", rendered);
        Assert.Contains("layer 1 of 3", rendered);
        Assert.Contains("50%", rendered);
    }

    [Fact]
    public void ToDisplayString_WithParent_OmitsLayerCounterWhenSingleLayer()
    {
        var layer = new OllamaPullProgress(
            "pulling abcdef123456",
            "sha256:abcdef123456",
            Total: 4_700_000_000L,
            Completed: 3_900_000_000L);

        var rendered = layer.ToDisplayString("llama3.2:3b", layerIndex: 1, layerCount: 1);

        // Single-layer pulls don't need the "layer 1 of 1" noise.
        Assert.DoesNotContain("layer", rendered);
        Assert.Contains("llama3.2:3b", rendered);
        Assert.Contains("83%", rendered);
    }

    [Fact]
    public void ToDisplayString_WithParent_PrefixesStageFrames()
    {
        // Stage frames at the tail of a pull (verifying, writing manifest,
        // success) should name the model so a multi-model batch's tail
        // still reads clearly.
        var verifying = new OllamaPullProgress("verifying sha256 digest", null, null, null);

        var rendered = verifying.ToDisplayString("llama3.1:8b", null, null);

        Assert.StartsWith("llama3.1:8b", rendered);
        Assert.Contains("verifying sha256 digest", rendered);
    }

    [Fact]
    public void ToDisplayString_WithoutParent_PreservesLegacyBehavior()
    {
        // Mac path on non-upgraded callers and legacy unit tests still
        // call the zero-arg overload — must render exactly as before.
        var layer = new OllamaPullProgress(
            "pulling 96c415656d37",
            "sha256:96c415656d37",
            Total: 4_700_000_000L,
            Completed: 3_900_000_000L);

        Assert.Equal(layer.ToDisplayString(), layer.ToDisplayString(null, null, null));
        Assert.Contains("96c415656d37", layer.ToDisplayString(null, null, null));
    }

    [Fact]
    public void ToDisplayString_WithParent_OnNonStandardStatus_AddsModelPrefix()
    {
        // Defensive: if a future Ollama frame doesn't match the
        // "pulling <hex>" prefix, we still want the parent name shown
        // so the user knows what's in flight rather than seeing a bare
        // unfamiliar word.
        var weird = new OllamaPullProgress(
            "downloading layers",
            "sha256:abc",
            Total: 1000L,
            Completed: 500L);

        var rendered = weird.ToDisplayString("llama3.2:3b", 2, 4);

        Assert.Contains("llama3.2:3b", rendered);
        Assert.Contains("downloading layers", rendered);
    }
}
