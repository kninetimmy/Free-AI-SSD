using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

/// <summary>
/// Covers the X19 Stage 3 neighbor expansion: ExpandNeighbors pulls chunk_index ± radius
/// for contiguous context, bounded to the hit's section (else page), clamped at file edges,
/// deduped, and ordered into contiguous runs by hit rank. All run in CI — expansion reads
/// only stored chunk text, so no Ollama is required.
/// </summary>
public sealed class VectorIndexNeighborTests : IDisposable
{
    private readonly string _root;

    public VectorIndexNeighborTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vi-nbr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static DocumentChunk Chunk(
        string path, int index, string text, string section = "", int? page = null)
        => new()
        {
            LibraryId = "lib1",
            SourceFileName = Path.GetFileName(path),
            StoredRelativePath = path,
            ChunkIndex = index,
            Text = text,
            TextLength = text.Length,
            Sha256 = "x",
            Embedding = new float[] { 1, 0, 0 },
            Section = section,
            Page = page,
        };

    private static RetrievalResult Hit(string path, int index, double score = 0.9, string section = "", int? page = null)
        => new()
        {
            Score = score,
            Chunk = new DocumentChunk
            {
                LibraryId = "lib1",
                SourceFileName = Path.GetFileName(path),
                StoredRelativePath = path,
                ChunkIndex = index,
                Section = section,
                Page = page,
            },
        };

    private static (string Path, int Index, bool IsNeighbor) Key(RetrievalResult r)
        => (r.Chunk.StoredRelativePath, r.Chunk.ChunkIndex, r.IsNeighbor);

    [Fact]
    public void ExpandNeighbors_RadiusZero_ReturnsHitsUnchanged()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk("files/a.txt", 0, "alpha", section: "S"),
            Chunk("files/a.txt", 1, "bravo", section: "S"),
        });

        var hits = new List<RetrievalResult> { Hit("files/a.txt", 1, section: "S") };
        var expanded = index.ExpandNeighbors("lib1", hits, radius: 0);

        Assert.Single(expanded);
        Assert.Equal(1, expanded[0].Chunk.ChunkIndex);
        Assert.False(expanded[0].IsNeighbor);
    }

    [Fact]
    public void ExpandNeighbors_Radius1_PullsAdjacentChunksAndKeepsHitInMiddle()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk("files/a.txt", 0, "before", section: "S"),
            Chunk("files/a.txt", 1, "the hit", section: "S"),
            Chunk("files/a.txt", 2, "after", section: "S"),
        });

        var hits = new List<RetrievalResult> { Hit("files/a.txt", 1, score: 0.77, section: "S") };
        var expanded = index.ExpandNeighbors("lib1", hits, radius: 1);

        Assert.Equal(new[] { 0, 1, 2 }, expanded.Select(r => r.Chunk.ChunkIndex).ToArray());
        Assert.True(expanded[0].IsNeighbor);
        Assert.False(expanded[1].IsNeighbor);
        Assert.True(expanded[2].IsNeighbor);
        // Hit keeps its score; neighbors carry none.
        Assert.Equal(0.77, expanded[1].Score, precision: 12);
        Assert.Equal(0, expanded[0].Score);
        Assert.Equal(0, expanded[2].Score);
        // Neighbor text is hydrated from the store.
        Assert.Equal("before", expanded[0].Chunk.Text);
        Assert.Equal("after", expanded[2].Chunk.Text);
    }

    [Fact]
    public void ExpandNeighbors_ClampsAtFileStart()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk("files/a.txt", 0, "first", section: "S"),
            Chunk("files/a.txt", 1, "second", section: "S"),
        });

        var hits = new List<RetrievalResult> { Hit("files/a.txt", 0, section: "S") };
        var expanded = index.ExpandNeighbors("lib1", hits, radius: 1);

        // No chunk_index -1; only the forward neighbor exists.
        Assert.Equal(new[] { 0, 1 }, expanded.Select(r => r.Chunk.ChunkIndex).ToArray());
        Assert.False(expanded[0].IsNeighbor);
        Assert.True(expanded[1].IsNeighbor);
    }

    [Fact]
    public void ExpandNeighbors_SectionBounding_ExcludesNeighborInDifferentSection()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk("files/a.txt", 0, "same section", section: "Alpha"),
            Chunk("files/a.txt", 1, "the hit", section: "Alpha"),
            Chunk("files/a.txt", 2, "next section", section: "Beta"),
        });

        var hits = new List<RetrievalResult> { Hit("files/a.txt", 1, section: "Alpha") };
        var expanded = index.ExpandNeighbors("lib1", hits, radius: 1);

        // Forward neighbor (idx 2) is a different section and must be excluded.
        Assert.Equal(new[] { 0, 1 }, expanded.Select(r => r.Chunk.ChunkIndex).ToArray());
    }

    [Fact]
    public void ExpandNeighbors_PageBounding_WhenHitHasNoSection()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.pdf", new List<DocumentChunk>
        {
            Chunk("files/a.pdf", 0, "page five a", page: 5),
            Chunk("files/a.pdf", 1, "page five b", page: 5),
            Chunk("files/a.pdf", 2, "page six", page: 6),
        });

        var hits = new List<RetrievalResult> { Hit("files/a.pdf", 1, page: 5) };
        var expanded = index.ExpandNeighbors("lib1", hits, radius: 1);

        // No section → bound by page. idx 2 is on page 6 and is excluded.
        Assert.Equal(new[] { 0, 1 }, expanded.Select(r => r.Chunk.ChunkIndex).ToArray());
    }

    [Fact]
    public void ExpandNeighbors_AdjacentHits_DedupeIntoOneRunWithoutDuplicates()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk("files/a.txt", 0, "c0", section: "S"),
            Chunk("files/a.txt", 1, "c1", section: "S"),
            Chunk("files/a.txt", 2, "c2", section: "S"),
            Chunk("files/a.txt", 3, "c3", section: "S"),
        });

        // Two adjacent hits: windows {0,1,2} and {1,2,3} overlap.
        var hits = new List<RetrievalResult>
        {
            Hit("files/a.txt", 1, section: "S"),
            Hit("files/a.txt", 2, section: "S"),
        };
        var expanded = index.ExpandNeighbors("lib1", hits, radius: 1);

        Assert.Equal(new[] { 0, 1, 2, 3 }, expanded.Select(r => r.Chunk.ChunkIndex).ToArray());
        // The two matched chunks stay hits; only 0 and 3 are neighbors.
        Assert.True(expanded[0].IsNeighbor);
        Assert.False(expanded[1].IsNeighbor);
        Assert.False(expanded[2].IsNeighbor);
        Assert.True(expanded[3].IsNeighbor);
    }

    [Fact]
    public void ExpandNeighbors_Radius2_PullsTwoOnEachSide()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk("files/a.txt", 0, "c0", section: "S"),
            Chunk("files/a.txt", 1, "c1", section: "S"),
            Chunk("files/a.txt", 2, "c2", section: "S"),
            Chunk("files/a.txt", 3, "c3", section: "S"),
            Chunk("files/a.txt", 4, "c4", section: "S"),
        });

        var hits = new List<RetrievalResult> { Hit("files/a.txt", 2, section: "S") };
        var expanded = index.ExpandNeighbors("lib1", hits, radius: 2);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, expanded.Select(r => r.Chunk.ChunkIndex).ToArray());
        Assert.False(expanded[2].IsNeighbor);
    }

    [Fact]
    public void ExpandNeighbors_OrdersRunsByBestHitRank()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk("files/a.txt", 4, "a4", section: "S"),
            Chunk("files/a.txt", 5, "a5", section: "S"),
            Chunk("files/a.txt", 6, "a6", section: "S"),
        });
        index.UpsertFileChunks("lib1", "files/b.txt", new List<DocumentChunk>
        {
            Chunk("files/b.txt", 0, "b0", section: "S"),
            Chunk("files/b.txt", 1, "b1", section: "S"),
        });

        // b.txt@0 is the top hit (rank 0); a.txt@5 is rank 1. b's run must lead.
        var hits = new List<RetrievalResult>
        {
            Hit("files/b.txt", 0, score: 0.95, section: "S"),
            Hit("files/a.txt", 5, score: 0.80, section: "S"),
        };
        var expanded = index.ExpandNeighbors("lib1", hits, radius: 1);

        var keys = expanded.Select(Key).ToArray();
        Assert.Equal(
            new[]
            {
                ("files/b.txt", 0, false),
                ("files/b.txt", 1, true),
                ("files/a.txt", 4, true),
                ("files/a.txt", 5, false),
                ("files/a.txt", 6, true),
            },
            keys);
    }

    [Fact]
    public void ExpandNeighbors_ScopesToLibrary()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk("files/a.txt", 0, "lib1 c0", section: "S"),
            Chunk("files/a.txt", 1, "lib1 c1", section: "S"),
        });
        index.UpsertFileChunks("lib2", "files/a.txt", new List<DocumentChunk>
        {
            new()
            {
                LibraryId = "lib2", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt",
                ChunkIndex = 1, Text = "lib2 c1", TextLength = 7, Sha256 = "y",
                Embedding = new float[] { 1, 0, 0 }, Section = "S",
            },
        });

        var hits = new List<RetrievalResult> { Hit("files/a.txt", 0, section: "S") };
        var expanded = index.ExpandNeighbors("lib1", hits, radius: 1);

        Assert.Equal(2, expanded.Count);
        Assert.Equal("lib1 c1", expanded[1].Chunk.Text);
    }
}
