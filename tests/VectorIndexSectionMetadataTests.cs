using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

/// <summary>
/// Covers the M3 schema migration (section metadata) added in RAG overhaul Stage 2
/// sub-stage A: column addition, no-backfill of legacy rows, the full migration ladder
/// from a pre-provenance DB, and persist/hydrate round-tripping. The current
/// schema_version is "4" — the X19 M4 (FTS lexical index) rung runs after M3, so a
/// fully-migrated DB lands on 4 even though these tests only assert the M3 columns.
/// </summary>
public sealed class VectorIndexSectionMetadataTests : IDisposable
{
    private readonly string _root;

    public VectorIndexSectionMetadataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"vi-sect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static HashSet<string> ColumnNames(SqliteConnection conn, string table)
    {
        var cols = new HashSet<string>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            cols.Add(reader.GetString(1));
        return cols;
    }

    private static string? SchemaVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
        return cmd.ExecuteScalar() as string;
    }

    [Fact]
    public void FreshDatabase_HasSectionColumnsAndSchemaVersion4()
    {
        _ = new VectorIndex(_root);

        using var conn = new SqliteConnection($"Data Source={Path.Combine(_root, "vectors.db")}");
        conn.Open();

        var cols = ColumnNames(conn, "chunks");
        Assert.Contains("section", cols);
        Assert.Contains("heading_path", cols);
        Assert.Contains("char_offset_start", cols);
        Assert.Contains("char_offset_end", cols);
        Assert.Contains("content_type", cols);
        Assert.Equal("4", SchemaVersion(conn));
    }

    [Fact]
    public void EnsureSchema_SchemaVersion2Database_AddsSectionColumns_LegacyRowsNull()
    {
        var dbPath = Path.Combine(_root, "vectors.db");
        var blob = EmbeddingSerializer.ToBlob(new float[] { 1f, 0f, 0f });

        // Build the M2 schema manually: provenance columns present, no section columns.
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
    chunker_version TEXT NOT NULL DEFAULT 'unknown'
);
INSERT INTO meta (key, value) VALUES ('embeddings_normalized', '1');
INSERT INTO meta (key, value) VALUES ('schema_version', '2');
";
            setup.ExecuteNonQuery();

            using var ins = conn.CreateCommand();
            ins.CommandText = @"INSERT INTO chunks
(library_id, source_file_name, stored_relative_path, page, chunk_index, text, text_length, sha256, embedding,
 embedding_model, embedding_dimension, parser_version, chunker_version)
VALUES ('lib1','a.txt','files/a.txt',NULL,0,'hello',5,'abc',$emb,'nomic-embed-text',3,'1','1')";
            ins.Parameters.AddWithValue("$emb", blob);
            ins.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();

        // Constructing VectorIndex triggers the M3 migration.
        _ = new VectorIndex(_root);

        using var verify = new SqliteConnection($"Data Source={dbPath}");
        verify.Open();

        var cols = ColumnNames(verify, "chunks");
        Assert.Contains("section", cols);
        Assert.Contains("heading_path", cols);
        Assert.Contains("char_offset_start", cols);
        Assert.Contains("char_offset_end", cols);
        Assert.Contains("content_type", cols);
        Assert.Equal("4", SchemaVersion(verify));

        // No backfill — the pre-existing row keeps NULL section metadata.
        using var cmd = verify.CreateCommand();
        cmd.CommandText = "SELECT section, heading_path, char_offset_start, char_offset_end, content_type FROM chunks WHERE library_id='lib1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.IsDBNull(4));
    }

    [Fact]
    public void EnsureSchema_PreProvenanceDatabase_LaddersUpThroughBothMigrations()
    {
        var dbPath = Path.Combine(_root, "vectors.db");
        var blob = EmbeddingSerializer.ToBlob(new float[] { 1f, 0f, 0f });

        // Build a pre-M2 schema: no provenance columns, no section columns, no schema_version meta.
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

        // One construction must walk the full ladder: provenance (M2), section metadata
        // (M3), then the FTS lexical index (M4) — landing on schema_version 4.
        _ = new VectorIndex(_root);

        using var verify = new SqliteConnection($"Data Source={dbPath}");
        verify.Open();

        var cols = ColumnNames(verify, "chunks");
        // M2 columns.
        Assert.Contains("embedding_model", cols);
        Assert.Contains("parser_version", cols);
        // M3 columns.
        Assert.Contains("section", cols);
        Assert.Contains("content_type", cols);
        Assert.Equal("4", SchemaVersion(verify));

        // M2 backfill still ran (dimension from blob length); M3 left section NULL.
        using var cmd = verify.CreateCommand();
        cmd.CommandText = "SELECT embedding_dimension, section FROM chunks WHERE library_id='lib1'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(3, reader.GetInt32(0));
        Assert.True(reader.IsDBNull(1));
    }

    [Fact]
    public void UpsertFileChunks_PersistsSectionMetadata_AndSearchHydratesIt()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/guide.pdf", new List<DocumentChunk>
        {
            new()
            {
                LibraryId = "lib1", SourceFileName = "guide.pdf", StoredRelativePath = "files/guide.pdf",
                Page = 412, ChunkIndex = 0, Text = "Set throttle to idle.", TextLength = 21, Sha256 = "abc",
                Embedding = new float[] { 1f, 0f, 0f },
                EmbeddingModel = "nomic-embed-text", EmbeddingDimension = 3,
                ParserVersion = "1", ChunkerVersion = "2",
                Section = "Start", HeadingPath = "Engines > Start",
                CharOffsetStart = 100, CharOffsetEnd = 121, ContentType = "text",
            },
        });

        var hits = index.Search("lib1", new float[] { 1f, 0f, 0f }, topK: 5);

        var chunk = Assert.Single(hits).Chunk;
        Assert.Equal("Start", chunk.Section);
        Assert.Equal("Engines > Start", chunk.HeadingPath);
        Assert.Equal(100, chunk.CharOffsetStart);
        Assert.Equal(121, chunk.CharOffsetEnd);
        Assert.Equal("text", chunk.ContentType);
    }

    [Fact]
    public void UpsertFileChunks_EmptySection_StoresNullAndHydratesEmpty()
    {
        var index = new VectorIndex(_root);
        index.UpsertFileChunks("lib1", "files/notes.txt", new List<DocumentChunk>
        {
            new()
            {
                LibraryId = "lib1", SourceFileName = "notes.txt", StoredRelativePath = "files/notes.txt",
                ChunkIndex = 0, Text = "plain text", TextLength = 10, Sha256 = "def",
                Embedding = new float[] { 1f, 0f, 0f },
                EmbeddingModel = "nomic-embed-text", EmbeddingDimension = 3,
                ParserVersion = "1", ChunkerVersion = "2",
                Section = "", HeadingPath = "", ContentType = "text",
            },
        });

        // Stored as NULL, not "".
        using (var conn = index.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT section, heading_path FROM chunks WHERE library_id='lib1'";
            using var reader = cmd.ExecuteReader();
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(0));
            Assert.True(reader.IsDBNull(1));
        }

        // Hydrates back to empty string (never null on the model).
        var chunk = Assert.Single(index.Search("lib1", new float[] { 1f, 0f, 0f }, topK: 5)).Chunk;
        Assert.Equal(string.Empty, chunk.Section);
        Assert.Equal(string.Empty, chunk.HeadingPath);
    }
}
