using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

public sealed class VectorIndexProvenanceTests : IDisposable
{
    private readonly string _root;

    public VectorIndexProvenanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vi-prov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void CheckProvenance_DimensionMismatch_ThrowsEmbeddingModelMismatchException()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new()
            {
                LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt",
                ChunkIndex = 0, Text = "x", TextLength = 1, Sha256 = "abc",
                Embedding = new float[] { 1f, 0f, 0f },
                EmbeddingModel = "nomic-embed-text", EmbeddingDimension = 3,
                ParserVersion = "1", ChunkerVersion = "1",
            },
        });

        var ex = Assert.Throws<EmbeddingModelMismatchException>(
            () => index.CheckProvenance("lib1", "nomic-embed-text", requestedDimension: 768));

        Assert.Equal("lib1", ex.LibraryId);
        Assert.Equal(3, ex.StoredDimension);
        Assert.Equal(768, ex.RequestedDimension);
    }

    [Fact]
    public void CheckProvenance_DimensionMatches_DoesNotThrow()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new()
            {
                LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt",
                ChunkIndex = 0, Text = "x", TextLength = 1, Sha256 = "abc",
                Embedding = new float[] { 1f, 0f, 0f },
                EmbeddingModel = "nomic-embed-text", EmbeddingDimension = 3,
                ParserVersion = "1", ChunkerVersion = "1",
            },
        });

        // Should not throw — dimension matches.
        index.CheckProvenance("lib1", "nomic-embed-text", requestedDimension: 3);
    }

    [Fact]
    public void CheckProvenance_EmptyLibrary_DoesNotThrow()
    {
        var index = new VectorIndex(_root);
        // No chunks — gate is a no-op for fresh libraries.
        index.CheckProvenance("lib1", "nomic-embed-text", requestedDimension: 768);
    }

    [Fact]
    public void EnsureSchema_ExistingDatabase_BackfillsDimensionFromBlobLength()
    {
        // Build the pre-X21 schema manually (no provenance columns).
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
    embedding BLOB NOT NULL
);
INSERT INTO meta (key, value) VALUES ('embeddings_normalized', '1');
";
            setup.ExecuteNonQuery();

            using var ins = conn.CreateCommand();
            ins.CommandText = @"INSERT INTO chunks
(library_id, source_file_name, stored_relative_path, page, chunk_index, text, text_length, sha256, embedding)
VALUES ('lib1','a.txt','files/a.txt',NULL,0,'hello',5,'abc',$emb)";
            ins.Parameters.AddWithValue("$emb", blob);
            ins.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        // Constructing VectorIndex triggers migration.
        _ = new VectorIndex(_root);

        using var verify = new SqliteConnection($"Data Source={dbPath}");
        verify.Open();
        using var cmd = verify.CreateCommand();
        cmd.CommandText = "SELECT embedding_dimension FROM chunks WHERE library_id='lib1'";
        var dim = Convert.ToInt32(cmd.ExecuteScalar());

        Assert.Equal(3, dim);
    }

    [Fact]
    public void UpsertFileChunks_FreshLibrary_PopulatesAllProvenanceColumns()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/a.txt", new List<DocumentChunk>
        {
            new()
            {
                LibraryId = "lib1", SourceFileName = "a.txt", StoredRelativePath = "files/a.txt",
                ChunkIndex = 0, Text = "hello", TextLength = 5, Sha256 = "abc",
                Embedding = new float[] { 1f, 0f, 0f },
                EmbeddingModel = "nomic-embed-text", EmbeddingDimension = 3,
                ParserVersion = "1", ChunkerVersion = "1",
            },
        });

        using var conn = index.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT embedding_model, embedding_dimension, parser_version, chunker_version FROM chunks WHERE library_id='lib1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("nomic-embed-text", reader.GetString(0));
        Assert.Equal(3, reader.GetInt32(1));
        Assert.Equal("1", reader.GetString(2));
        Assert.Equal("1", reader.GetString(3));
    }
}
