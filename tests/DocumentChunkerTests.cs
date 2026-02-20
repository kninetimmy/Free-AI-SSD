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
}
