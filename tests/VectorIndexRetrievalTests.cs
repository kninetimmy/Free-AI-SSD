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

    [Fact]
    public void Search_WithThreshold_FiltersLowSimilarityChunks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rag-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var index = new VectorIndex(root);

        // Embedding [1,0,0] has cosine similarity ~0.99 to query [0.99,0.1,0]
        // Embedding [0,1,0] has cosine similarity ~0.10 to query [0.99,0.1,0]
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 0, Text = "cats", TextLength = 4, Sha256 = "x", Embedding = new float[]{1,0,0} },
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 1, Text = "dogs", TextLength = 4, Sha256 = "x", Embedding = new float[]{0,1,0} },
        });

        // Threshold 0.5 should keep "cats" (high similarity) and filter out "dogs" (low similarity)
        var result = index.Search("lib1", new float[] { 0.99f, 0.1f, 0f }, 5, minimumSimilarity: 0.5, logger: null);
        Assert.Single(result);
        Assert.Equal("cats", result[0].Chunk.Text);
    }

    [Fact]
    public void Search_WithThreshold_ReturnsEmptyWhenAllBelowThreshold()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rag-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var index = new VectorIndex(root);

        // Both embeddings are orthogonal to the query, so similarity ~0
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 0, Text = "cats", TextLength = 4, Sha256 = "x", Embedding = new float[]{0,1,0} },
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 1, Text = "dogs", TextLength = 4, Sha256 = "x", Embedding = new float[]{0,0,1} },
        });

        var result = index.Search("lib1", new float[] { 1f, 0f, 0f }, 5, minimumSimilarity: 0.5, logger: null);
        Assert.Empty(result);
    }

    [Fact]
    public void Search_WithZeroThreshold_BehavesLikeOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rag-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var index = new VectorIndex(root);

        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 0, Text = "cats", TextLength = 4, Sha256 = "x", Embedding = new float[]{1,0,0} },
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 1, Text = "dogs", TextLength = 4, Sha256 = "x", Embedding = new float[]{0,1,0} },
        });

        // With threshold 0, all results should be included (backward-compatible behavior)
        var result = index.Search("lib1", new float[] { 0.9f, 0.1f, 0f }, 5, minimumSimilarity: 0, logger: null);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Search_WithThreshold_RespectsTopKAfterFiltering()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rag-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var index = new VectorIndex(root);

        // alpha: cosine(query, [1,0,0]) = 1.0  (perfect match)
        // beta:  cosine(query, [0.5,0.5,0]) ≈ 0.71 (moderate match, above threshold)
        // gamma: cosine(query, [0,0,1]) = 0.0  (orthogonal, below threshold)
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 0, Text = "alpha", TextLength = 5, Sha256 = "x", Embedding = new float[]{1,0,0} },
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 1, Text = "beta", TextLength = 4, Sha256 = "x", Embedding = new float[]{0.5f,0.5f,0} },
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 2, Text = "gamma", TextLength = 5, Sha256 = "x", Embedding = new float[]{0,0,1} },
        });

        // topK=1 with threshold 0.1: gamma filtered out, then top 1 from {alpha, beta} → alpha
        var result = index.Search("lib1", new float[] { 1f, 0f, 0f }, 1, minimumSimilarity: 0.1, logger: null);
        Assert.Single(result);
        Assert.Equal("alpha", result[0].Chunk.Text);
    }
}
