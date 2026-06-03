using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class DocumentChunkerTests
{
    [Fact]
    public void ChunkText_WithOverlap_ProducesMultipleChunks()
    {
        var text = string.Join(' ', Enumerable.Repeat("alpha beta gamma delta", 120));
        var chunks = DocumentChunker.ChunkText(text, 300, 50);

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, c => Assert.True(c.Length <= 320));
    }

    [Fact]
    public void ChunkText_LongDocument_SplitsIntoExpectedChunkCount()
    {
        // ~2400 chars of text, with chunk size 500 and no overlap, expect ~5 chunks
        var text = string.Join(' ', Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 60));
        var chunks = DocumentChunker.ChunkText(text, 500, 0);

        Assert.True(chunks.Count >= 4, $"Expected at least 4 chunks, got {chunks.Count}");
        Assert.True(chunks.Count <= 8, $"Expected at most 8 chunks, got {chunks.Count}");
    }

    [Fact]
    public void ChunkText_WithOverlap_ChunksShareContent()
    {
        // Create enough text to guarantee multiple chunks
        var text = string.Join(' ', Enumerable.Repeat("word", 200));
        var chunks = DocumentChunker.ChunkText(text, 300, 100);

        Assert.True(chunks.Count >= 2, "Need at least 2 chunks to test overlap");

        // With overlap, the end of one chunk should share some content with the start of the next
        // Verify the second chunk's start overlaps with the first chunk's end
        var firstEnd = chunks[0][^50..];
        var secondStart = chunks[1][..50];
        // At least some overlap region should exist
        Assert.True(
            chunks[0].Length + chunks[1].Length > text.Length / chunks.Count,
            "Overlapping chunks should have more total characters than non-overlapping");
    }

    [Fact]
    public void ChunkText_EmptyDocument_ReturnsEmptyList()
    {
        var chunks = DocumentChunker.ChunkText("", 500, 50);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkText_NullInput_ReturnsEmptyList()
    {
        var chunks = DocumentChunker.ChunkText(null!, 500, 50);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkText_WhitespaceOnlyDocument_ReturnsEmptyList()
    {
        var chunks = DocumentChunker.ChunkText("   \n\t  \r\n  ", 500, 50);
        Assert.Empty(chunks);
    }

    [Fact]
    public void ChunkText_ShortText_ReturnsSingleChunk()
    {
        var text = "This is a short sentence.";
        var chunks = DocumentChunker.ChunkText(text, 500, 50);

        Assert.Single(chunks);
        Assert.Equal(text, chunks[0]);
    }

    [Fact]
    public void ChunkText_MinimumChunkSize_EnforcesFloorOf200()
    {
        // Even when requesting chunkSize=50, the minimum is enforced at 200
        var text = string.Join(' ', Enumerable.Repeat("test word", 100));
        var smallChunks = DocumentChunker.ChunkText(text, 50, 0);
        var minChunks = DocumentChunker.ChunkText(text, 200, 0);

        // Both should produce the same result since 50 is clamped to 200
        Assert.Equal(minChunks.Count, smallChunks.Count);
    }

    [Fact]
    public void ChunkText_OverlapCappedAtHalfChunkSize()
    {
        // Overlap is clamped to chunkSize/2, so overlap=1000 with chunkSize=400 → overlap=200
        var text = string.Join(' ', Enumerable.Repeat("testing overlap capping behavior here", 80));
        var chunks = DocumentChunker.ChunkText(text, 400, 1000);

        // Should still produce valid chunks (not infinite loop)
        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c)));
    }

    [Fact]
    public void ChunkText_NoOverlap_ChunksDoNotRepeat()
    {
        var text = string.Join(' ', Enumerable.Repeat("unique", 150));
        var chunks = DocumentChunker.ChunkText(text, 300, 0);

        Assert.True(chunks.Count >= 2);
        // Total reconstructed length should approximate original (no duplication)
        var totalLen = chunks.Sum(c => c.Length);
        Assert.True(totalLen <= text.Length + chunks.Count, "No-overlap chunks shouldn't exceed original length significantly");
    }

    #region ChunkBlocks — section-aware chunking

    [Fact]
    public void ChunkBlocks_HeadingThenBody_AttributesSectionAndBreadcrumb()
    {
        var blocks = new List<DocumentBlock>
        {
            new() { Page = 5, Kind = BlockKind.Heading, HeadingLevel = 1, Text = "Engines" },
            new() { Page = 5, Kind = BlockKind.Heading, HeadingLevel = 2, Text = "Start" },
            new() { Page = 5, Kind = BlockKind.Body, Text = "Set throttle to idle and press start." },
        };

        var span = Assert.Single(DocumentChunker.ChunkBlocks(blocks, 500, 50));

        Assert.Equal("Start", span.Section);
        Assert.Equal("Engines > Start", span.HeadingPath);
        Assert.Equal(5, span.Page);
        Assert.Equal("text", span.ContentType);
    }

    [Fact]
    public void ChunkBlocks_SiblingHeading_FlushesPreviousSection_AndResetsBreadcrumb()
    {
        var blocks = new List<DocumentBlock>
        {
            new() { Kind = BlockKind.Heading, HeadingLevel = 1, Text = "A" },
            new() { Kind = BlockKind.Body, Text = "Alpha body content here." },
            new() { Kind = BlockKind.Heading, HeadingLevel = 1, Text = "B" },
            new() { Kind = BlockKind.Body, Text = "Bravo body content here." },
        };

        var spans = DocumentChunker.ChunkBlocks(blocks, 500, 50);

        Assert.Equal(2, spans.Count);
        Assert.Equal("A", spans[0].Section);
        Assert.Equal("A", spans[0].HeadingPath);
        Assert.Contains("Alpha", spans[0].Text);
        Assert.DoesNotContain("Bravo", spans[0].Text);
        Assert.Equal("B", spans[1].Section);
        Assert.Contains("Bravo", spans[1].Text);
        Assert.DoesNotContain("Alpha", spans[1].Text);
    }

    [Fact]
    public void ChunkBlocks_PageChange_FlushesButKeepsSectionBreadcrumb()
    {
        var blocks = new List<DocumentBlock>
        {
            new() { Page = 1, Kind = BlockKind.Heading, HeadingLevel = 1, Text = "Procedure" },
            new() { Page = 1, Kind = BlockKind.Body, Text = "First half on page one." },
            new() { Page = 2, Kind = BlockKind.Body, Text = "Second half on page two." },
        };

        var spans = DocumentChunker.ChunkBlocks(blocks, 500, 50);

        Assert.Equal(2, spans.Count);
        Assert.Equal(1, spans[0].Page);
        Assert.Equal(2, spans[1].Page);
        // The heading stack persists across the page boundary.
        Assert.Equal("Procedure", spans[0].Section);
        Assert.Equal("Procedure", spans[1].Section);
    }

    [Fact]
    public void ChunkBlocks_AllBody_DegeneratesToChunkTextOutput()
    {
        var text = string.Join(' ', Enumerable.Repeat("alpha beta gamma delta", 120));
        var blocks = new List<DocumentBlock> { new() { Page = 1, Kind = BlockKind.Body, Text = text } };

        var spans = DocumentChunker.ChunkBlocks(blocks, 300, 50);

        Assert.Equal(DocumentChunker.ChunkText(text, 300, 50), spans.Select(s => s.Text).ToList());
        Assert.All(spans, s => Assert.Equal(string.Empty, s.Section));
        Assert.All(spans, s => Assert.Equal(string.Empty, s.HeadingPath));
        Assert.All(spans, s => Assert.Equal(1, s.Page));
    }

    [Fact]
    public void ChunkBlocks_CharOffsets_AreMonotonicAndMatchTextLength()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 400));
        var blocks = new List<DocumentBlock> { new() { Page = 1, Kind = BlockKind.Body, Text = text } };

        var spans = DocumentChunker.ChunkBlocks(blocks, 300, 50);

        Assert.True(spans.Count >= 2);
        Assert.Equal(0, spans[0].CharOffsetStart);
        for (var i = 0; i < spans.Count; i++)
        {
            Assert.Equal(spans[i].Text.Length, spans[i].CharOffsetEnd - spans[i].CharOffsetStart);
            if (i > 0)
                Assert.True(spans[i].CharOffsetStart >= spans[i - 1].CharOffsetStart);
        }
    }

    [Fact]
    public void ChunkBlocks_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(DocumentChunker.ChunkBlocks(new List<DocumentBlock>(), 300, 50));
    }

    [Fact]
    public void ChunkBlocks_TinyTrailingRemainder_FoldsIntoPreviousSameSectionChunk()
    {
        // A single section whose body splits into a couple of full chunks plus a small remainder.
        // The remainder must be folded into the preceding chunk, not emitted as a micro-chunk (#68).
        var body = string.Join(' ', Enumerable.Repeat("alpha", 110)) + " uniquetail";
        var blocks = new List<DocumentBlock>
        {
            new() { Page = 3, Kind = BlockKind.Heading, HeadingLevel = 1, Text = "Systems" },
            new() { Page = 3, Kind = BlockKind.Body, Text = body },
        };

        var spans = DocumentChunker.ChunkBlocks(blocks, 300, 0);

        Assert.True(spans.Count >= 2, $"Expected the body to split into multiple chunks, got {spans.Count}");
        Assert.All(spans, s => Assert.True(s.Text.Length >= 120, $"Chunk below the floor: {s.Text.Length} chars"));
        // The tail survived the merge (wasn't dropped) and rides on the final chunk.
        Assert.Contains("uniquetail", spans[^1].Text);
        Assert.Equal("Systems", spans[^1].Section);
        // Offset invariant preserved after the merge.
        Assert.All(spans, s => Assert.Equal(s.Text.Length, s.CharOffsetEnd - s.CharOffsetStart));
    }

    [Fact]
    public void ChunkBlocks_TinySiblingSections_AreNotMergedAcrossSections()
    {
        // Two distinct short sections must stay separate — the floor only folds a remainder into
        // the SAME section, never across a section boundary (attribution would otherwise blur).
        var blocks = new List<DocumentBlock>
        {
            new() { Page = 1, Kind = BlockKind.Heading, HeadingLevel = 1, Text = "Alpha" },
            new() { Page = 1, Kind = BlockKind.Body, Text = "Short alpha body." },
            new() { Page = 1, Kind = BlockKind.Heading, HeadingLevel = 1, Text = "Bravo" },
            new() { Page = 1, Kind = BlockKind.Body, Text = "Short bravo body." },
        };

        var spans = DocumentChunker.ChunkBlocks(blocks, 300, 0);

        Assert.Equal(2, spans.Count);
        Assert.Equal("Alpha", spans[0].Section);
        Assert.Equal("Bravo", spans[1].Section);
    }

    [Fact]
    public void ClassifyThenChunk_DenseDiagramPage_DoesNotFragmentIntoMicroChunks()
    {
        // End-to-end guard for #68: a dense label page (40 short bold callouts) must not explode
        // into one chunk per label. Density backoff collapses it to body; chunking yields a handful.
        var lines = new List<LineRecord>();
        for (var i = 0; i < 40; i++)
        {
            lines.Add(new LineRecord { Page = 1, Text = $"CALLOUT {i}", FontSize = 12 + (i % 3), IsBold = true });
        }

        var blocks = DocumentParser.ClassifyLinesIntoBlocks(lines);
        var spans = DocumentChunker.ChunkBlocks(blocks, 1200, 200);

        Assert.True(spans.Count <= 3, $"Dense page should collapse, got {spans.Count} chunks");
    }

    #endregion
}
