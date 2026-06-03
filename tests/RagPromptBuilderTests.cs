using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class RagPromptBuilderTests
{
    private static RetrievalResult MakeResult(
        string text, double score, string fileName = "doc.txt", int? page = null,
        int chunkIndex = 0, bool isNeighbor = false)
    {
        return new RetrievalResult
        {
            Score = score,
            IsNeighbor = isNeighbor,
            Chunk = new DocumentChunk
            {
                LibraryId = "lib1",
                SourceFileName = fileName,
                StoredRelativePath = $"files/{fileName}",
                Text = text,
                TextLength = text.Length,
                Sha256 = "abc",
                Page = page,
                ChunkIndex = chunkIndex
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

    [Fact]
    public void Build_HitPriority_NeighborNeverDisplacesItsHit()
    {
        // Reading order puts a large preceding neighbor before the small matched chunk.
        // The neighbor appears first, but matched chunks claim the budget first, so the
        // hit must survive and the oversized neighbor must be dropped.
        var results = new List<RetrievalResult>
        {
            MakeResult(new string('N', 3000), 0, "a.txt", chunkIndex: 0, isNeighbor: true),
            MakeResult("THE ANSWER", 0.9, "a.txt", chunkIndex: 1, isNeighbor: false),
        };

        var output = RagPromptBuilder.Build("query", results, maxContextChars: 2000);

        Assert.True(output.UsedContext);
        Assert.Contains("THE ANSWER", output.Prompt);
        Assert.DoesNotContain(new string('N', 3000), output.Prompt);
        Assert.Single(output.Sources); // only the matched chunk is a source
    }

    [Fact]
    public void Build_NeighborsStayAdjacentToHit_InReadingOrder()
    {
        var results = new List<RetrievalResult>
        {
            MakeResult("preceding context", 0, "a.txt", chunkIndex: 0, isNeighbor: true),
            MakeResult("matched chunk", 0.9, "a.txt", chunkIndex: 1, isNeighbor: false),
            MakeResult("following context", 0, "a.txt", chunkIndex: 2, isNeighbor: true),
        };

        var output = RagPromptBuilder.Build("query", results, maxContextChars: 5000);

        var pre = output.Prompt.IndexOf("preceding context");
        var hit = output.Prompt.IndexOf("matched chunk");
        var post = output.Prompt.IndexOf("following context");
        Assert.True(pre >= 0 && hit >= 0 && post >= 0);
        Assert.True(pre < hit && hit < post, "neighbors must stay adjacent to their hit in reading order");
        // Neighbors are context, not matches — only the hit is cited.
        Assert.Single(output.Sources);
    }

    [Fact]
    public void Build_NeighborsCountTowardBudget_DropOverflowNeighbors()
    {
        // The hit fits; one neighbor fits alongside it; the second overflows the budget.
        var results = new List<RetrievalResult>
        {
            MakeResult("hit", 0.9, "a.txt", chunkIndex: 1, isNeighbor: false),
            MakeResult(new string('A', 800), 0, "a.txt", chunkIndex: 2, isNeighbor: true),
            MakeResult(new string('B', 800), 0, "a.txt", chunkIndex: 3, isNeighbor: true),
        };

        var output = RagPromptBuilder.Build("query", results, maxContextChars: 1100);

        Assert.True(output.UsedContext);
        Assert.Contains("hit", output.Prompt);
        var hasA = output.Prompt.Contains(new string('A', 800));
        var hasB = output.Prompt.Contains(new string('B', 800));
        Assert.True(hasA ^ hasB, "exactly one neighbor should fit within the budget");
    }

    [Fact]
    public void Build_NeighborWithoutAFittingHit_FallsBackToBarePrompt()
    {
        // The only matched chunk is too large; a neighbor must never be shown on its own.
        var results = new List<RetrievalResult>
        {
            MakeResult(new string('H', 3000), 0.9, "a.txt", chunkIndex: 1, isNeighbor: false),
            MakeResult("small neighbor", 0, "a.txt", chunkIndex: 2, isNeighbor: true),
        };

        var output = RagPromptBuilder.Build("query", results, maxContextChars: 100);

        Assert.False(output.UsedContext);
        Assert.Equal("query", output.Prompt);
        Assert.DoesNotContain("small neighbor", output.Prompt);
    }

    [Fact]
    public void Build_WithContext_RestrictsAnswerToProvidedContext()
    {
        var results = new List<RetrievalResult> { MakeResult("Plants use sunlight for energy.", 0.9) };

        var output = RagPromptBuilder.Build("What is photosynthesis?", results);

        Assert.True(output.UsedContext);
        Assert.Contains("using ONLY the reference context", output.Prompt);
    }

    [Fact]
    public void Build_DefaultMode_OmitsInlineCitationInstruction()
    {
        var results = new List<RetrievalResult> { MakeResult("Plants use sunlight for energy.", 0.9) };

        var output = RagPromptBuilder.Build("What is photosynthesis?", results);

        // Concise default: no inline-cite instruction, and the model is told not to emit
        // bracketed labels (so TTS never reads them and answers stay short).
        Assert.DoesNotContain("cite its source inline", output.Prompt);
        Assert.Contains("Do not include source labels", output.Prompt);
    }

    [Fact]
    public void Build_InlineCitationsEnabled_RequiresInlineCitations()
    {
        var results = new List<RetrievalResult> { MakeResult("Plants use sunlight for energy.", 0.9) };

        var output = RagPromptBuilder.Build("What is photosynthesis?", results, inlineCitations: true);

        Assert.Contains("cite its source inline", output.Prompt);
        Assert.DoesNotContain("Do not include source labels", output.Prompt);
    }

    [Fact]
    public void Build_BothModes_InstructConciseAnswer()
    {
        var results = new List<RetrievalResult> { MakeResult("Plants use sunlight for energy.", 0.9) };

        Assert.Contains("Be concise", RagPromptBuilder.Build("q", results).Prompt);
        Assert.Contains("Be concise", RagPromptBuilder.Build("q", results, inlineCitations: true).Prompt);
    }

    [Fact]
    public void Build_BothModes_AlwaysListSourcesSeparately()
    {
        // The Sources list (Sources panel) is independent of the inline-citation toggle —
        // it is always populated from the matched chunks.
        var results = new List<RetrievalResult> { MakeResult("Content.", 0.9, "report.pdf", page: 3) };

        Assert.Contains("[report.pdf p.3]", RagPromptBuilder.Build("q", results).Sources);
        Assert.Contains("[report.pdf p.3]", RagPromptBuilder.Build("q", results, inlineCitations: true).Sources);
    }

    [Fact]
    public void Build_WithContext_InstructsExactRefusalWhenAnswerAbsent()
    {
        // The refusal phrase is asserted verbatim so it can't drift out of sync with
        // the grounding contract (a future reword would fail this test deliberately).
        var results = new List<RetrievalResult> { MakeResult("Plants use sunlight for energy.", 0.9) };

        var output = RagPromptBuilder.Build("What is photosynthesis?", results);

        Assert.Contains("That information is not in the provided context.", output.Prompt);
    }
}
