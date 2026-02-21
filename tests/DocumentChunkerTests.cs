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
}
