using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

/// <summary>
/// Covers the X19 lexical arm: the FTS5 index, BM25 search, MATCH-query hardening,
/// hybrid fusion, and the M4 migration that backfills the lexical index from stored
/// chunk text (no re-embedding). All run in CI — lexical retrieval needs only stored
/// text, so no Ollama is required.
/// </summary>
public sealed class VectorIndexLexicalTests : IDisposable
{
    private readonly string _root;

    public VectorIndexLexicalTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vi-lex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static DocumentChunk Chunk(int index, string text, float[] embedding)
        => new()
        {
            LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt",
            ChunkIndex = index, Text = text, TextLength = text.Length, Sha256 = "x",
            Embedding = embedding,
        };

    // ---- BuildFtsMatchQuery -------------------------------------------------

    [Fact]
    public void BuildFtsMatchQuery_QuotesTokensAndOrsThem()
    {
        Assert.Equal("\"engine\" OR \"start\"", VectorIndex.BuildFtsMatchQuery("engine start"));
    }

    [Fact]
    public void BuildFtsMatchQuery_DropsPurePunctuationTokens()
    {
        // "?" carries no letter/digit and must not produce an empty phrase.
        Assert.Equal("\"wingspan\"", VectorIndex.BuildFtsMatchQuery("wingspan ?"));
    }

    [Fact]
    public void BuildFtsMatchQuery_EscapesEmbeddedQuotes()
    {
        Assert.Equal("\"a\"\"b\"", VectorIndex.BuildFtsMatchQuery("a\"b"));
    }

    [Fact]
    public void BuildFtsMatchQuery_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, VectorIndex.BuildFtsMatchQuery("   "));
        Assert.Equal(string.Empty, VectorIndex.BuildFtsMatchQuery("?! -"));
    }

    // ---- LexicalSearch ------------------------------------------------------

    [Fact]
    public void LexicalSearch_FindsChunkByExactToken()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk(0, "Engine start procedure", new float[] { 1, 0, 0 }),
            Chunk(1, "The wingspan is twelve meters", new float[] { 0, 1, 0 }),
        });

        var hits = index.LexicalSearch("lib1", VectorIndex.BuildFtsMatchQuery("wingspan"), limit: 10);

        Assert.Single(hits);
        Assert.Equal(1, hits[0].Chunk.ChunkIndex);
    }

    [Fact]
    public void LexicalSearch_PorterStemming_MatchesInflectedForms()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk(0, "Procedure to start the engine", new float[] { 1, 0, 0 }),
        });

        // Query "starting" stems to "start" under the porter tokenizer and matches.
        var hits = index.LexicalSearch("lib1", VectorIndex.BuildFtsMatchQuery("starting"), limit: 10);

        Assert.Single(hits);
        Assert.Equal(0, hits[0].Chunk.ChunkIndex);
    }

    [Fact]
    public void LexicalSearch_ScopesToLibrary()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk> { Chunk(0, "shared keyword here", new float[] { 1, 0, 0 }) });
        index.UpsertFileChunks("lib2", "files/b.txt", new List<DocumentChunk>
        {
            new() { LibraryId = "lib2", SourceFileName = "b.txt", StoredRelativePath = "files/b.txt", ChunkIndex = 0, Text = "shared keyword here", TextLength = 18, Sha256 = "y", Embedding = new float[] { 1, 0, 0 } },
        });

        var hits = index.LexicalSearch("lib1", VectorIndex.BuildFtsMatchQuery("keyword"), limit: 10);

        Assert.Single(hits);
        Assert.Equal("files/a.txt", hits[0].Chunk.StoredRelativePath);
    }

    // ---- SearchHybrid -------------------------------------------------------

    [Fact]
    public void SearchHybrid_SurfacesLexicalHit_DenseRatesBelowThreshold()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk(0, "Engine start procedure", new float[] { 1, 0, 0 }),
            Chunk(1, "The wingspan is twelve meters", new float[] { 0, 1, 0 }),
        });

        // Query embedding points at chunk 0; chunk 1 is orthogonal (cosine 0, below 0.3).
        var query = new float[] { 1, 0, 0 };

        // Dense-only would drop the wingspan chunk.
        var denseOnly = index.Search("lib1", query, topK: 5, minimumSimilarity: 0.3, logger: null);
        Assert.DoesNotContain(denseOnly, r => r.Chunk.ChunkIndex == 1);

        // Hybrid surfaces it via the lexical arm despite the sub-threshold cosine.
        var hybrid = index.SearchHybrid("lib1", query, "wingspan", topK: 5, minimumSimilarity: 0.3, logger: null);
        Assert.Contains(hybrid, r => r.Chunk.ChunkIndex == 1);
    }

    [Fact]
    public void SearchHybrid_BothArmsEmpty_ReturnsEmpty()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk(0, "Engine start procedure", new float[] { 0, 1, 0 }),
        });

        // Query is orthogonal (dense filtered by threshold) and shares no token with the text.
        var hybrid = index.SearchHybrid("lib1", new float[] { 1, 0, 0 }, "wingspan", topK: 5, minimumSimilarity: 0.3, logger: null);

        Assert.Empty(hybrid);
    }

    [Fact]
    public void SearchHybrid_ChunkStrongInBothArms_RanksFirst()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk(0, "wingspan dimensions of the airframe", new float[] { 1, 0, 0 }), // dense + lexical
            Chunk(1, "unrelated landing gear text", new float[] { 0, 1, 0 }),         // neither
        });

        var hybrid = index.SearchHybrid("lib1", new float[] { 1, 0, 0 }, "wingspan", topK: 5, minimumSimilarity: 0.0, logger: null);

        Assert.Equal(0, hybrid[0].Chunk.ChunkIndex);
    }

    // ---- M4 migration (no-reindex backfill) --------------------------------

    [Fact]
    public void Migration_ExistingDbWithoutFts_BackfillsLexicalIndexFromStoredText()
    {
        // Build a pre-FTS v3 database by hand (full column set, schema_version '3', no chunks_fts).
        var dbPath = Path.Combine(_root, "vectors.db");
        var blob = EmbeddingSerializer.ToBlob(new float[] { 1f, 0f, 0f });

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var setup = conn.CreateCommand();
            setup.CommandText = @"
CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE chunks (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    library_id TEXT NOT NULL,
    source_file_name TEXT NOT NULL,
    stored_relative_path TEXT NOT NULL,
    page INTEGER NULL,
    chunk_index INTEGER NOT NULL,
    text TEXT NOT NULL,
    text_length INTEGER NOT NULL,
    sha256 TEXT NOT NULL,
    embedding BLOB NOT NULL,
    embedding_model TEXT NOT NULL DEFAULT 'unknown',
    embedding_dimension INTEGER NOT NULL DEFAULT 0,
    parser_version TEXT NOT NULL DEFAULT 'unknown',
    chunker_version TEXT NOT NULL DEFAULT 'unknown',
    section TEXT NULL,
    heading_path TEXT NULL,
    char_offset_start INTEGER NULL,
    char_offset_end INTEGER NULL,
    content_type TEXT NULL
);
INSERT INTO meta (key, value) VALUES ('embeddings_normalized', '1');
INSERT INTO meta (key, value) VALUES ('schema_version', '3');
";
            setup.ExecuteNonQuery();

            using var ins = conn.CreateCommand();
            ins.CommandText = @"INSERT INTO chunks
(library_id, source_file_name, stored_relative_path, page, chunk_index, text, text_length, sha256, embedding, embedding_dimension)
VALUES ('lib1','a.txt','files/a.txt',NULL,0,'preexisting wingspan content',27,'abc',$emb,3)";
            ins.Parameters.AddWithValue("$emb", blob);
            ins.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        // Constructing VectorIndex runs the M4 migration and backfills the FTS index.
        var index = new VectorIndex(_root);

        var hits = index.LexicalSearch("lib1", VectorIndex.BuildFtsMatchQuery("wingspan"), limit: 10);

        Assert.Single(hits);
        Assert.Equal("preexisting wingspan content", hits[0].Chunk.Text);
    }

    [Fact]
    public void UpsertFileChunks_ReingestSameFile_LexicalIndexHasNoStaleEntries()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk(0, "original alpha content", new float[] { 1, 0, 0 }),
        });

        // Re-ingest the same stored path (DELETE + INSERT) — the delete trigger must
        // remove the old FTS entry so the stale token no longer matches.
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            Chunk(0, "replacement beta content", new float[] { 1, 0, 0 }),
        });

        Assert.Empty(index.LexicalSearch("lib1", VectorIndex.BuildFtsMatchQuery("alpha"), limit: 10));
        Assert.Single(index.LexicalSearch("lib1", VectorIndex.BuildFtsMatchQuery("beta"), limit: 10));
    }
}
