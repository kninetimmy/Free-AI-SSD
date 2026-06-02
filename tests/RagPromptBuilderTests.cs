using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class RagPromptBuilderTests
{
    private static RetrievalResult MakeResult(string text, double score, string fileName = "doc.txt", int? page = null)
    {
        return new RetrievalResult
        {
            Score = score,
            Chunk = new DocumentChunk
            {
                LibraryId = "lib1",
                SourceFileName = fileName,
                StoredRelativePath = $"files/{fileName}",
                Text = text,
                TextLength = text.Length,
                Sha256 = "abc",
                Page = page
            }
        };
    }

    [Fact]
    public void Build_WithRetrievalResults_InsertsContextIntoPrompt()
    {
        var results = new List<RetrievalResult>
        {
            MakeResult("Plants use sunlight for energy.", 0.9)
        };

        var output = RagPromptBuilder.Build("What is photosynthesis?", results);

        Assert.True(output.UsedContext);
        Assert.Contains("Reference context:", output.Prompt);
        Assert.Contains("Plants use sunlight for energy.", output.Prompt);
        Assert.Contains("What is photosynthesis?", output.Prompt);
    }

    [Fact]
    public void Build_WithExcessiveContext_TruncatesToMaxLength()
    {
        // Create results that exceed maxContextChars
        var longText = new string('A', 3000);
        var results = new List<RetrievalResult>
        {
            MakeResult(longText, 0.9, "a.txt"),
            MakeResult(longText, 0.8, "b.txt"),
            MakeResult(longText, 0.7, "c.txt"),
        };

        var output = RagPromptBuilder.Build("query", results, maxContextChars: 4000);

        Assert.True(output.UsedContext);
        // Only the first result should fit within 4000 chars (3000 text + citation overhead)
        Assert.Single(output.Sources);
    }

    [Fact]
    public void Build_WithEmptyRetrieval_NoLibrarySearched_ReturnsOriginalPrompt()
    {
        var results = new List<RetrievalResult>();

        var output = RagPromptBuilder.Build("What is AI?", results, librarySearched: false);

        Assert.Equal("What is AI?", output.Prompt);
        Assert.False(output.UsedContext);
        Assert.Empty(output.Sources);
    }

    [Fact]
    public void Build_WithEmptyRetrieval_LibrarySearched_ReturnsNoRelevantDocsNote()
    {
        var results = new List<RetrievalResult>();

        var output = RagPromptBuilder.Build("What is AI?", results, librarySearched: true);

        Assert.Contains("No relevant documents found", output.Prompt);
        Assert.Contains("What is AI?", output.Prompt);
        Assert.False(output.UsedContext);
    }

    [Fact]
    public void Build_WithResults_IncludesCitationSourcesInOutput()
    {
        var results = new List<RetrievalResult>
        {
            MakeResult("Content from report.", 0.9, "report.pdf", page: 3),
            MakeResult("Content from notes.", 0.8, "notes.txt")
        };

        var output = RagPromptBuilder.Build("query", results);

        Assert.True(output.UsedContext);
        Assert.Contains("[report.pdf p.3]", output.Sources);
        Assert.Contains("[notes.txt]", output.Sources);
    }

    [Fact]
    public void Build_PreservesRetrievalOrder()
    {
        // The retriever owns ranking (dense Search returns score-descending; hybrid
        // SearchHybrid returns fused-rank order), so Build must emit context in the
        // supplied order and must NOT re-sort by Score. Scores here are deliberately
        // non-monotonic — a re-sort would reorder them and fail this test.
        var results = new List<RetrievalResult>
        {
            MakeResult("First by rank.", 0.30, "first.txt"),
            MakeResult("Second by rank.", 0.95, "second.txt"),
            MakeResult("Third by rank.", 0.60, "third.txt")
        };

        var output = RagPromptBuilder.Build("query", results);

        var firstIdx = output.Prompt.IndexOf("First by rank.");
        var secondIdx = output.Prompt.IndexOf("Second by rank.");
        var thirdIdx = output.Prompt.IndexOf("Third by rank.");

        Assert.True(firstIdx < secondIdx, "Context must follow the supplied retrieval order, not re-sort by score");
        Assert.True(secondIdx < thirdIdx, "Context must follow the supplied retrieval order, not re-sort by score");
    }

    [Fact]
    public void Build_AllChunksTooLargeForContext_ReturnsOriginalPrompt()
    {
        // Each chunk citation + text exceeds the tiny max context
        var results = new List<RetrievalResult>
        {
            MakeResult(new string('X', 200), 0.9, "big.txt")
        };

        var output = RagPromptBuilder.Build("query", results, maxContextChars: 10);

        Assert.False(output.UsedContext);
        Assert.Equal("query", output.Prompt);
    }
}
