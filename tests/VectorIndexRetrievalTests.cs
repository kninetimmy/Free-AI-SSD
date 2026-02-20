using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class VectorIndexRetrievalTests
{
    [Fact]
    public void Search_ReturnsBestMatchFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rag-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var index = new VectorIndex(root);

        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 0, Text = "cats", TextLength = 4, Sha256 = "x", Embedding = new float[]{1,0,0} },
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 1, Text = "dogs", TextLength = 4, Sha256 = "x", Embedding = new float[]{0,1,0} },
        });

        var result = index.Search("lib1", new float[] { 0.9f, 0.1f, 0f }, 1);
        Assert.Single(result);
        Assert.Equal("cats", result[0].Chunk.Text);
    }
}
