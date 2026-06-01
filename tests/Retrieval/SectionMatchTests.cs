using FreeAiSsd.Shared.Documents;
using Xunit;

namespace FreeAiSsd.Tests.Retrieval;

/// <summary>
/// Unit coverage for <see cref="RetrievalEvalHarness.SectionMatches"/> — the section
/// constraint applied to golden-set hits. This is the one piece of Stage 2 eval logic that
/// runs without a live Ollama, so it gets dedicated coverage here rather than only inside the
/// Ollama-gated harness.
/// </summary>
public sealed class SectionMatchTests
{
    private static DocumentChunk Chunk(string section, string headingPath) =>
        new() { Section = section, HeadingPath = headingPath };

    [Theory]
    // No constraint → always a pass (Stage 1 back-compat).
    [InlineData(null, "Start", "Engines > Start", true)]
    [InlineData("", "Start", "Engines > Start", true)]
    [InlineData("   ", "Start", "Engines > Start", true)]
    // Leaf section match.
    [InlineData("Start", "Start", "Engines > Start", true)]
    // Parent heading match via the breadcrumb.
    [InlineData("Engines", "Start", "Engines > Start", true)]
    // Case-insensitive.
    [InlineData("engines", "Start", "Engines > Start", true)]
    // Whitespace around the needle is trimmed.
    [InlineData("  Start  ", "Start", "Engines > Start", true)]
    // Substring within a heading title is enough.
    [InlineData("Engine", "Start", "Engines > Start", true)]
    // Wrong section is a miss.
    [InlineData("Avionics", "Start", "Engines > Start", false)]
    // Constraint set but chunk has no section attribution → miss.
    [InlineData("Start", "", "", false)]
    public void SectionMatches_RespectsConstraint(string? expected, string section, string headingPath, bool shouldMatch)
    {
        Assert.Equal(shouldMatch, RetrievalEvalHarness.SectionMatches(expected, Chunk(section, headingPath)));
    }
}
