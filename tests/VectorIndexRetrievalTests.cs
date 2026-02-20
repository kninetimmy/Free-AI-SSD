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

        // topK=1 with threshold 0.1: gamma filtered out, then top 1 from {alpha, beta} -> alpha
        var result = index.Search("lib1", new float[] { 1f, 0f, 0f }, 1, minimumSimilarity: 0.1, logger: null);
        Assert.Single(result);
        Assert.Equal("alpha", result[0].Chunk.Text);
    }

    [Fact]
    public void DotProductSimd_MatchesScalarDotProduct()
    {
        var a = new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1.0f, 1.1f, 1.2f, 1.3f, 1.4f, 1.5f, 1.6f, 1.7f };
        var b = new float[] { 1.7f, 1.6f, 1.5f, 1.4f, 1.3f, 1.2f, 1.1f, 1.0f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 0.4f, 0.3f, 0.2f, 0.1f };

        double expected = 0;
        for (int i = 0; i < a.Length; i++) expected += a[i] * b[i];

        var result = VectorIndex.DotProductSimd(a, b);
        Assert.Equal(expected, result, precision: 4);
    }

    [Fact]
    public void DotProductSimd_NormalizedVectorsMatchCosineSimilarity()
    {
        var a = new float[] { 3f, 4f, 0f };
        var b = new float[] { 1f, 2f, 2f };

        var cosine = VectorIndex.CosineSimilarity(a, b);

        EmbeddingSerializer.NormalizeInPlace(a);
        EmbeddingSerializer.NormalizeInPlace(b);

        var dot = VectorIndex.DotProductSimd(a, b);
        Assert.Equal(cosine, dot, precision: 5);
    }

    [Fact]
    public void DotProductSimd_EmptyArrays_ReturnsZero()
    {
        Assert.Equal(0, VectorIndex.DotProductSimd(Array.Empty<float>(), Array.Empty<float>()));
    }

    [Fact]
    public void DotProductSimd_MismatchedLengths_ReturnsZero()
    {
        Assert.Equal(0, VectorIndex.DotProductSimd(new float[] { 1f }, new float[] { 1f, 2f }));
    }

    [Fact]
    public void NormalizeInPlace_ProducesUnitVector()
    {
        var v = new float[] { 3f, 4f, 0f };
        EmbeddingSerializer.NormalizeInPlace(v);

        float mag = 0;
        for (int i = 0; i < v.Length; i++) mag += v[i] * v[i];

        Assert.Equal(1.0f, mag, precision: 5);
        Assert.Equal(0.6f, v[0], precision: 5);
        Assert.Equal(0.8f, v[1], precision: 5);
    }

    [Fact]
    public void NormalizeInPlace_ZeroVector_Unchanged()
    {
        var v = new float[] { 0f, 0f, 0f };
        EmbeddingSerializer.NormalizeInPlace(v);
        Assert.All(v, f => Assert.Equal(0f, f));
    }

    [Fact]
    public void NormalizeInPlace_AlreadyNormalized_Idempotent()
    {
        var v = new float[] { 1f, 0f, 0f };
        EmbeddingSerializer.NormalizeInPlace(v);
        Assert.Equal(1f, v[0], precision: 5);
        Assert.Equal(0f, v[1], precision: 5);
    }

    [Fact]
    public void Normalize_ReturnsNewArray()
    {
        var original = new float[] { 3f, 4f, 0f };
        var normalized = EmbeddingSerializer.Normalize(original);

        // Original unchanged.
        Assert.Equal(3f, original[0]);
        Assert.Equal(4f, original[1]);

        // Normalized copy is unit length.
        Assert.Equal(0.6f, normalized[0], precision: 5);
        Assert.Equal(0.8f, normalized[1], precision: 5);
    }

    [Fact]
    public void UpsertFileChunks_NormalizesEmbeddings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rag-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var index = new VectorIndex(root);

        // Insert a non-unit-length embedding.
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 0, Text = "test", TextLength = 4, Sha256 = "x", Embedding = new float[]{3f, 4f, 0f} },
        });

        // Search with a query in the same direction — should get high similarity (~1.0).
        var result = index.Search("lib1", new float[] { 3f, 4f, 0f }, 1);
        Assert.Single(result);
        Assert.True(result[0].Score > 0.99, $"Expected score > 0.99 for same-direction vectors, got {result[0].Score}");
    }

    [Fact]
    public void GetChunkCount_ReturnsCorrectCount()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rag-index-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var index = new VectorIndex(root);

        Assert.Equal(0, index.GetChunkCount("lib1"));

        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 0, Text = "a", TextLength = 1, Sha256 = "x", Embedding = new float[]{1,0,0} },
            new() { LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt", ChunkIndex = 1, Text = "b", TextLength = 1, Sha256 = "x", Embedding = new float[]{0,1,0} },
        });

        Assert.Equal(2, index.GetChunkCount("lib1"));
        Assert.Equal(0, index.GetChunkCount("lib2"));
    }

    [Fact]
    public void NormalizeInPlace_LargeVector_SimdPath()
    {
        // Use a vector large enough to exercise SIMD lanes (typically 8+ floats).
        var v = new float[768];
        var rng = new Random(42);
        for (int i = 0; i < v.Length; i++) v[i] = (float)(rng.NextDouble() * 2 - 1);

        EmbeddingSerializer.NormalizeInPlace(v);

        float mag = 0;
        for (int i = 0; i < v.Length; i++) mag += v[i] * v[i];

        Assert.Equal(1.0f, mag, precision: 4);
    }

    [Fact]
    public void DotProductSimd_LargeVectors_Accurate()
    {
        // Test with realistic embedding dimensions to exercise SIMD paths.
        var a = new float[768];
        var b = new float[768];
        var rng = new Random(123);
        double expected = 0;
        for (int i = 0; i < 768; i++)
        {
            a[i] = (float)(rng.NextDouble() * 2 - 1);
            b[i] = (float)(rng.NextDouble() * 2 - 1);
            expected += a[i] * b[i];
        }

        var result = VectorIndex.DotProductSimd(a, b);
        Assert.Equal(expected, result, precision: 2);
    }
}
