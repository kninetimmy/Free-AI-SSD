using FreeAiSsd.PrepApp;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC31: pin the ANSI strip + progress-line detection that
/// <see cref="ModelOperations.Consume"/> relies on to coalesce the
/// pull TUI's per-tick rewrite escapes into a single "in-place"
/// progress channel.
///
/// The v1.3.10 mac field-test symptom was a log pane that filled with
/// <c>pulling abc123def456... 43%[?25h[?25l[2K[1G</c>-shaped lines —
/// looked like a restart loop, was actually one progress display
/// with embedded ANSI cursor moves not being stripped. Pinning here
/// because <see cref="OllamaPullProgressFilter"/> is the seam every
/// pull path passes through.
/// </summary>
public sealed class OllamaPullProgressFilterTests
{
    [Fact]
    public void Clean_StripsAnsiCursorRewrites_FromProgressLine()
    {
        // Real Ollama TUI output: hide-cursor + erase-line + cursor-to-col-1
        // around a progress update, plus a show-cursor at the end.
        var raw = "\x1b[?25l\x1b[2K\x1b[1Gpulling abc123def456... 43%\x1b[?25h";

        var cleaned = OllamaPullProgressFilter.Clean(raw);

        Assert.Equal("pulling abc123def456... 43%", cleaned);
    }

    [Fact]
    public void Clean_TakesLastSegmentAcrossCarriageReturnRewrites()
    {
        // ReadLineAsync only splits on \n. If a buffered chunk carries
        // multiple \r-separated rewrites (which Ollama's TUI does when
        // ticks arrive faster than the pipe flushes), surface the latest
        // segment so the user sees the current state, not the start of
        // the chunk.
        var raw = "pulling deadbeef000... 10%\rpulling deadbeef000... 25%\rpulling deadbeef000... 42%";

        var cleaned = OllamaPullProgressFilter.Clean(raw);

        Assert.Equal("pulling deadbeef000... 42%", cleaned);
    }

    [Fact]
    public void Clean_HandlesAnsiAndCarriageReturnsTogether()
    {
        var raw = "\x1b[?25lpulling cafe1234... 15%\r\x1b[2K\x1b[1Gpulling cafe1234... 30%\x1b[?25h";

        var cleaned = OllamaPullProgressFilter.Clean(raw);

        Assert.Equal("pulling cafe1234... 30%", cleaned);
    }

    [Fact]
    public void Clean_LeavesNonProgressLinesAlone()
    {
        var raw = "pulling manifest... done.";

        var cleaned = OllamaPullProgressFilter.Clean(raw);

        Assert.Equal("pulling manifest... done.", cleaned);
    }

    [Fact]
    public void Clean_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, OllamaPullProgressFilter.Clean(string.Empty));
        Assert.Equal(string.Empty, OllamaPullProgressFilter.Clean("   "));
        Assert.Equal(string.Empty, OllamaPullProgressFilter.Clean("\x1b[?25l"));
    }

    [Theory]
    [InlineData("pulling abc123def456... 0%", true)]
    [InlineData("pulling abc123def456... 43%", true)]
    [InlineData("pulling abc123def456... 100%", true)]
    [InlineData("pulling abc123def456...   12%", true)]
    public void IsProgressLine_TruePerOllamaShape(string cleaned, bool expected)
    {
        Assert.Equal(expected, OllamaPullProgressFilter.IsProgressLine(cleaned));
    }

    [Theory]
    [InlineData("pulling manifest... done.")]
    [InlineData("pulling manifest")]
    [InlineData("verifying sha256 digest")]
    [InlineData("writing manifest")]
    [InlineData("Error: file not found")]
    [InlineData("[ollama serve stderr] download.go:370 part 5 stalled; retrying")]
    [InlineData("")]
    public void IsProgressLine_FalseForNonProgressShapes(string cleaned)
    {
        // Conservative pattern: anything that doesn't match
        // `pulling <hex>... NN%` falls through to the log surface so
        // shape drift fails open to verbose logging rather than silent
        // loss. The MAC28 "[ollama serve stderr] ..." case is
        // particularly important — those are real diagnostics that must
        // never get suppressed by the progress filter.
        Assert.False(OllamaPullProgressFilter.IsProgressLine(cleaned));
    }
}
