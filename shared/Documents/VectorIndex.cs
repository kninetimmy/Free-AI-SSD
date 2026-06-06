using Microsoft.Data.Sqlite;
using System.Numerics;

namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// SQLite-backed vector index optimized for portable, offline document libraries.
/// <para>
/// <b>Scale target:</b> optimized for up to 10,000 chunks (~500 documents at
/// default chunk settings). The implementation uses a brute-force linear scan
/// with SIMD-accelerated dot-product similarity and pre-normalized embeddings.
/// A warning is logged when a library exceeds the recommended chunk count.
/// </para>
/// <para>
/// Design rationale: An approximate nearest-neighbor index (HNSW, IVF, etc.)
/// would add native dependencies that conflict with the portable SSD deployment
/// model — the app must run as a single self-contained executable on any
/// Windows machine the drive is plugged into. At the target scale, the
/// optimized linear scan completes in single-digit milliseconds on modern
/// hardware, making an ANN index unnecessary.
/// </para>
/// </summary>
public sealed class VectorIndex
{
    private readonly string _dbPath;

    /// <summary>
    /// Recommended maximum number of chunks per library before performance
    /// degrades noticeably on typical consumer hardware. Exceeding this
    /// threshold triggers a warning log.
    /// </summary>
    public const int RecommendedMaxChunks = 10_000;

    /// <summary>
    /// How many candidates each retrieval arm (dense, lexical) contributes to rank
    /// fusion before the fused list is truncated to the caller's topK. Larger than a
    /// typical topK so a chunk ranked modestly by one arm but highly by the other can
    /// still surface; bounded so fusion stays cheap.
    /// </summary>
    private const int HybridCandidatePool = 50;

    public VectorIndex(string indexFolderPath, SsdLogger? logger = null)
    {
        Directory.CreateDirectory(indexFolderPath);
        _dbPath = Path.Combine(indexFolderPath, "vectors.db");
        EnsureSchema(logger);
    }

    internal SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL";
                cmd.ExecuteScalar();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout=5000";
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // A PRAGMA failure here would leak the opened connection — the caller's
            // `using` never binds it because we never return — and with it any
            // -wal/-shm file lock on the SSD. Dispose before rethrowing.
            conn.Dispose();
            throw;
        }
        return conn;
    }

    private void EnsureSchema(SsdLogger? logger)
    {
        using var conn = OpenConnection();

        // Detect whether the table already exists and uses the old TEXT schema.
        var tableExists = false;
        var hasOldSchema = false;
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(chunks)";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                tableExists = true;
                if (reader.GetString(1) == "embedding_json")
                    hasOldSchema = true;
            }
        }

        // Ensure the meta table exists for tracking migration state.
        using (var metaCmd = conn.CreateCommand())
        {
            metaCmd.CommandText = "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
            metaCmd.ExecuteNonQuery();
        }

        if (!tableExists)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS chunks (
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
CREATE INDEX IF NOT EXISTS idx_chunks_library ON chunks(library_id);
CREATE INDEX IF NOT EXISTS idx_chunks_sha ON chunks(sha256);
";
            cmd.ExecuteNonQuery();

            // Fresh database — create the FTS lexical index, mark embeddings
            // normalized, and stamp the current schema version.
            CreateFtsObjects(conn);
            SetMeta(conn, "embeddings_normalized", "1");
            SetMeta(conn, "schema_version", "4");
            return;
        }

        if (hasOldSchema)
        {
            MigrateTextToBlob(conn, logger);
        }

        // Normalize existing embeddings if not already done.
        if (GetMeta(conn, "embeddings_normalized") != "1")
        {
            NormalizeExistingEmbeddings(conn, logger);
        }

        // Migration ladder: walk each pre-current database up one rung at a time.
        // A null/legacy version gets M2 provenance, then M3 section metadata, then
        // the M4 lexical (FTS5) index — each step stamps its own schema_version.
        var schemaVersion = GetMeta(conn, "schema_version");
        if (schemaVersion != "2" && schemaVersion != "3" && schemaVersion != "4")
        {
            MigrateAddProvenance(conn, logger);
            schemaVersion = "2";
        }
        if (schemaVersion == "2")
        {
            MigrateAddSectionMetadata(conn, logger);
            schemaVersion = "3";
        }
        if (schemaVersion == "3")
        {
            MigrateAddFts(conn, logger);
            schemaVersion = "4";
        }
    }

    /// <summary>
    /// Adds embedding provenance columns (M2 migration). Backfills embedding_dimension
    /// from blob length so existing libraries close the silent-zero-score failure mode
    /// without requiring a forced reindex.
    /// </summary>
    private static void MigrateAddProvenance(SqliteConnection conn, SsdLogger? logger)
    {
        logger?.Info("VectorIndex: applying M2 migration — adding provenance columns...");

        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN embedding_model TEXT NOT NULL DEFAULT 'unknown'";
            alter.ExecuteNonQuery();
        }
        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN embedding_dimension INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN parser_version TEXT NOT NULL DEFAULT 'unknown'";
            alter.ExecuteNonQuery();
        }
        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN chunker_version TEXT NOT NULL DEFAULT 'unknown'";
            alter.ExecuteNonQuery();
        }

        // Backfill dimension from blob length. Each float is 4 bytes.
        using (var backfill = conn.CreateCommand())
        {
            backfill.CommandText = "UPDATE chunks SET embedding_dimension = LENGTH(embedding) / 4 WHERE embedding_dimension = 0";
            backfill.ExecuteNonQuery();
        }

        SetMeta(conn, "schema_version", "2");
        logger?.Info("VectorIndex: M2 migration complete.");
    }

    /// <summary>
    /// Adds section-metadata columns (M3 migration). No backfill — legacy rows keep NULL
    /// section/heading_path/offsets/content_type because they were chunked by chunker v1,
    /// which had no structural awareness. New ingests populate these going forward.
    /// </summary>
    private static void MigrateAddSectionMetadata(SqliteConnection conn, SsdLogger? logger)
    {
        logger?.Info("VectorIndex: applying M3 migration — adding section metadata columns...");

        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN section TEXT NULL";
            alter.ExecuteNonQuery();
        }
        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN heading_path TEXT NULL";
            alter.ExecuteNonQuery();
        }
        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN char_offset_start INTEGER NULL";
            alter.ExecuteNonQuery();
        }
        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN char_offset_end INTEGER NULL";
            alter.ExecuteNonQuery();
        }
        using (var alter = conn.CreateCommand())
        {
            alter.CommandText = "ALTER TABLE chunks ADD COLUMN content_type TEXT NULL";
            alter.ExecuteNonQuery();
        }

        SetMeta(conn, "schema_version", "3");
        logger?.Info("VectorIndex: M3 migration complete.");
    }

    /// <summary>
    /// Adds the FTS5 lexical index (M4 migration). Creates a contentless-external
    /// <c>chunks_fts</c> table mirroring <c>chunks.text</c>, the sync triggers, and
    /// backfills the index from the text already stored in <c>chunks</c>. No
    /// re-embedding and no document re-ingest — the lexical arm is built purely from
    /// persisted chunk text, so existing libraries gain hybrid retrieval transparently.
    /// </summary>
    private static void MigrateAddFts(SqliteConnection conn, SsdLogger? logger)
    {
        logger?.Info("VectorIndex: applying M4 migration — building FTS5 lexical index...");
        CreateFtsObjects(conn);
        RebuildFts(conn);
        SetMeta(conn, "schema_version", "4");
        logger?.Info("VectorIndex: M4 migration complete (lexical index built from stored chunk text).");
    }

    /// <summary>
    /// Creates the external-content FTS5 table and the triggers that keep it in sync
    /// with <c>chunks</c>. Uses the <c>porter unicode61</c> tokenizer: porter stemming
    /// helps natural-language queries match manual prose ("start" ↔ "starting"), while
    /// acronyms and codes (AN/APG-79, CCIP) are left un-stemmed so exact-token matches
    /// still land. Idempotent (IF NOT EXISTS) so it is safe on both the fresh-DB and
    /// migration paths.
    /// </summary>
    private static void CreateFtsObjects(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
    text,
    content='chunks',
    content_rowid='id',
    tokenize='porter unicode61'
);
CREATE TRIGGER IF NOT EXISTS chunks_fts_ai AFTER INSERT ON chunks BEGIN
    INSERT INTO chunks_fts(rowid, text) VALUES (new.id, new.text);
END;
CREATE TRIGGER IF NOT EXISTS chunks_fts_ad AFTER DELETE ON chunks BEGIN
    INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
END;
CREATE TRIGGER IF NOT EXISTS chunks_fts_au AFTER UPDATE OF text ON chunks BEGIN
    INSERT INTO chunks_fts(chunks_fts, rowid, text) VALUES ('delete', old.id, old.text);
    INSERT INTO chunks_fts(rowid, text) VALUES (new.id, new.text);
END;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Rebuilds the FTS index from the content table. Used at migration time to
    /// backfill the index for chunks that predate the FTS table.
    /// </summary>
    private static void RebuildFts(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO chunks_fts(chunks_fts) VALUES ('rebuild')";
        cmd.ExecuteNonQuery();
    }

    #region Meta helpers

    private static string? GetMeta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    private static void SetMeta(SqliteConnection conn, string key, string value, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value)";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }

    #endregion

    /// <summary>
    /// Migrates an existing database from JSON TEXT embeddings to binary BLOB storage.
    /// The migration is atomic: either all rows are converted or none are (transaction rollback).
    /// Safe to re-run — a leftover temp table from a previous crashed attempt is cleaned up first.
    /// Embeddings are L2-normalized during migration so subsequent searches can use dot product.
    /// </summary>
    private static void MigrateTextToBlob(SqliteConnection conn, SsdLogger? logger)
    {
        // Clean up any leftover temp table from a previously interrupted migration.
        using (var drop = conn.CreateCommand())
        {
            drop.CommandText = "DROP TABLE IF EXISTS chunks_new";
            drop.ExecuteNonQuery();
        }

        // Read all existing rows into memory so the reader is closed before we do DDL/DML.
        var rows = new List<(int Id, string LibraryId, string Source, string Stored,
            object? Page, int Idx, string Text, int Len, string Sha, string Json)>();
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT id,library_id,source_file_name,stored_relative_path,page,chunk_index,text,text_length,sha256,embedding_json FROM chunks";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : (object)reader.GetInt32(4),
                    reader.GetInt32(5),
                    reader.GetString(6),
                    reader.GetInt32(7),
                    reader.GetString(8),
                    reader.GetString(9)
                ));
            }
        }

        var total = rows.Count;
        logger?.Info($"Migrating embeddings to binary format: 0/{total}...");

        // Perform the entire migration inside a single transaction so it's atomic.
        using var tx = conn.BeginTransaction();

        using (var create = conn.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = @"
CREATE TABLE chunks_new (
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
)";
            create.ExecuteNonQuery();
        }

        var migrated = 0;
        foreach (var row in rows)
        {
            var floats = JsonSerializer.Deserialize<float[]>(row.Json) ?? Array.Empty<float>();
            EmbeddingSerializer.NormalizeInPlace(floats);
            var blob = EmbeddingSerializer.ToBlob(floats);

            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO chunks_new (id,library_id,source_file_name,stored_relative_path,page,chunk_index,text,text_length,sha256,embedding)
VALUES ($id,$libraryId,$source,$stored,$page,$idx,$text,$len,$sha,$emb)";
            ins.Parameters.AddWithValue("$id", row.Id);
            ins.Parameters.AddWithValue("$libraryId", row.LibraryId);
            ins.Parameters.AddWithValue("$source", row.Source);
            ins.Parameters.AddWithValue("$stored", row.Stored);
            ins.Parameters.AddWithValue("$page", row.Page ?? DBNull.Value);
            ins.Parameters.AddWithValue("$idx", row.Idx);
            ins.Parameters.AddWithValue("$text", row.Text);
            ins.Parameters.AddWithValue("$len", row.Len);
            ins.Parameters.AddWithValue("$sha", row.Sha);
            ins.Parameters.AddWithValue("$emb", blob);
            ins.ExecuteNonQuery();

            migrated++;
            if (migrated % 100 == 0 || migrated == total)
                logger?.Info($"Migrating embeddings to binary format: {migrated}/{total}...");
        }

        using (var dropOld = conn.CreateCommand())
        {
            dropOld.Transaction = tx;
            dropOld.CommandText = "DROP TABLE chunks";
            dropOld.ExecuteNonQuery();
        }

        using (var rename = conn.CreateCommand())
        {
            rename.Transaction = tx;
            rename.CommandText = "ALTER TABLE chunks_new RENAME TO chunks";
            rename.ExecuteNonQuery();
        }

        // Recreate indexes on the renamed table.
        using (var idx = conn.CreateCommand())
        {
            idx.Transaction = tx;
            idx.CommandText = @"
CREATE INDEX IF NOT EXISTS idx_chunks_library ON chunks(library_id);
CREATE INDEX IF NOT EXISTS idx_chunks_sha ON chunks(sha256);
";
            idx.ExecuteNonQuery();
        }

        SetMeta(conn, "embeddings_normalized", "1", tx);

        tx.Commit();
        logger?.Info("Embedding migration to binary format complete (embeddings normalized).");
    }

    /// <summary>
    /// One-time migration that L2-normalizes all existing BLOB embeddings in place.
    /// After this, search can use a simple dot product instead of full cosine similarity.
    /// </summary>
    private static void NormalizeExistingEmbeddings(SqliteConnection conn, SsdLogger? logger)
    {
        // Read all (id, embedding) pairs.
        var rows = new List<(int Id, byte[] Blob)>();
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT id, embedding FROM chunks";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt32(0), (byte[])reader[1]));
            }
        }

        if (rows.Count == 0)
        {
            SetMeta(conn, "embeddings_normalized", "1");
            return;
        }

        logger?.Info($"Normalizing {rows.Count} stored embeddings for optimized search...");

        using var tx = conn.BeginTransaction();
        var updated = 0;
        foreach (var (id, blob) in rows)
        {
            var floats = EmbeddingSerializer.FromBlob(blob);
            EmbeddingSerializer.NormalizeInPlace(floats);
            var normalizedBlob = EmbeddingSerializer.ToBlob(floats);

            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE chunks SET embedding=$emb WHERE id=$id";
            upd.Parameters.AddWithValue("$id", id);
            upd.Parameters.AddWithValue("$emb", normalizedBlob);
            upd.ExecuteNonQuery();

            updated++;
            if (updated % 500 == 0 || updated == rows.Count)
                logger?.Info($"Normalizing embeddings: {updated}/{rows.Count}...");
        }

        SetMeta(conn, "embeddings_normalized", "1", tx);
        tx.Commit();
        logger?.Info("Embedding normalization complete.");
    }

    public void UpsertFileChunks(string libraryId, string storedRelativePath, IReadOnlyList<DocumentChunk> chunks)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var delete = conn.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM chunks WHERE library_id=$libraryId AND stored_relative_path=$path";
            delete.Parameters.AddWithValue("$libraryId", libraryId);
            delete.Parameters.AddWithValue("$path", storedRelativePath);
            delete.ExecuteNonQuery();
        }

        foreach (var c in chunks)
        {
            // Pre-normalize so search only needs a dot product.
            var normalized = EmbeddingSerializer.Normalize(c.Embedding);

            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO chunks
(library_id, source_file_name, stored_relative_path, page, chunk_index, text, text_length, sha256, embedding,
 embedding_model, embedding_dimension, parser_version, chunker_version,
 section, heading_path, char_offset_start, char_offset_end, content_type)
VALUES ($libraryId,$source,$stored,$page,$idx,$text,$len,$sha,$emb,$model,$dim,$pv,$cv,
 $section,$headingPath,$offStart,$offEnd,$contentType)";
            ins.Parameters.AddWithValue("$libraryId", c.LibraryId);
            ins.Parameters.AddWithValue("$source", c.SourceFileName);
            ins.Parameters.AddWithValue("$stored", c.StoredRelativePath);
            ins.Parameters.AddWithValue("$page", (object?)c.Page ?? DBNull.Value);
            ins.Parameters.AddWithValue("$idx", c.ChunkIndex);
            ins.Parameters.AddWithValue("$text", c.Text);
            ins.Parameters.AddWithValue("$len", c.TextLength);
            ins.Parameters.AddWithValue("$sha", c.Sha256);
            ins.Parameters.AddWithValue("$emb", EmbeddingSerializer.ToBlob(normalized));
            ins.Parameters.AddWithValue("$model", string.IsNullOrEmpty(c.EmbeddingModel) ? "unknown" : c.EmbeddingModel);
            ins.Parameters.AddWithValue("$dim", c.EmbeddingDimension > 0 ? c.EmbeddingDimension : normalized.Length);
            ins.Parameters.AddWithValue("$pv", string.IsNullOrEmpty(c.ParserVersion) ? "unknown" : c.ParserVersion);
            ins.Parameters.AddWithValue("$cv", string.IsNullOrEmpty(c.ChunkerVersion) ? "unknown" : c.ChunkerVersion);
            // Section/heading absent → store NULL (not "") so legacy and structureless chunks read alike.
            ins.Parameters.AddWithValue("$section", string.IsNullOrEmpty(c.Section) ? DBNull.Value : c.Section);
            ins.Parameters.AddWithValue("$headingPath", string.IsNullOrEmpty(c.HeadingPath) ? DBNull.Value : c.HeadingPath);
            ins.Parameters.AddWithValue("$offStart", c.CharOffsetStart);
            ins.Parameters.AddWithValue("$offEnd", c.CharOffsetEnd);
            ins.Parameters.AddWithValue("$contentType", string.IsNullOrEmpty(c.ContentType) ? "text" : c.ContentType);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void RemoveFile(string libraryId, string storedRelativePath)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chunks WHERE library_id=$libraryId AND stored_relative_path=$path";
        cmd.Parameters.AddWithValue("$libraryId", libraryId);
        cmd.Parameters.AddWithValue("$path", storedRelativePath);
        cmd.ExecuteNonQuery();
    }

    public void UpdateFileName(string libraryId, string storedRelativePath, string newName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chunks SET source_file_name=$name WHERE library_id=$libraryId AND stored_relative_path=$path";
        cmd.Parameters.AddWithValue("$name", newName);
        cmd.Parameters.AddWithValue("$libraryId", libraryId);
        cmd.Parameters.AddWithValue("$path", storedRelativePath);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Verifies that the requested embedding dimension matches the stored provenance for
    /// a library. Throws <see cref="EmbeddingModelMismatchException"/> on dimension mismatch
    /// (silent-zero-score failure mode). Logs a warning when the model name differs from
    /// what was stored but dimension matches — a recoverable semantic drift, not corruption.
    /// No-op when the library has no chunks yet.
    /// </summary>
    public void CheckProvenance(string libraryId, string model, int requestedDimension, SsdLogger? logger = null)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT embedding_dimension, embedding_model FROM chunks WHERE library_id=$libraryId AND embedding_dimension != 0";
        cmd.Parameters.AddWithValue("$libraryId", libraryId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var storedDim = reader.GetInt32(0);
            var storedModel = reader.GetString(1);

            if (storedDim != requestedDimension)
                throw new EmbeddingModelMismatchException(libraryId, storedDim, requestedDimension);

            if (storedModel != "unknown" && storedModel != model)
                logger?.Warn($"VectorIndex: library '{libraryId}' was indexed with model '{storedModel}', current model is '{model}'. Semantic consistency not guaranteed.");
        }
    }

    /// <summary>
    /// Searches for the most similar chunks without applying a similarity threshold.
    /// Preserved for backward compatibility — callers that don't need threshold filtering
    /// can continue using this overload unchanged.
    /// </summary>
    public List<RetrievalResult> Search(string libraryId, float[] queryEmbedding, int topK)
    {
        return Search(libraryId, queryEmbedding, topK, minimumSimilarity: 0, logger: null);
    }

    /// <summary>
    /// Searches for the most similar chunks using SIMD-accelerated dot product on
    /// pre-normalized embeddings. Results below <paramref name="minimumSimilarity"/>
    /// are discarded, then the best <paramref name="topK"/> are returned.
    /// Uses a min-heap (PriorityQueue) for O(N log K) top-K selection instead of
    /// sorting all N results.
    /// Logs a warning when the library exceeds <see cref="RecommendedMaxChunks"/>.
    /// </summary>
    public List<RetrievalResult> Search(string libraryId, float[] queryEmbedding, int topK, double minimumSimilarity, SsdLogger? logger)
    {
        // Normalize the query vector so dot product equals cosine similarity.
        var query = EmbeddingSerializer.Normalize(queryEmbedding);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_file_name,stored_relative_path,page,chunk_index,text,text_length,sha256,embedding,section,heading_path,char_offset_start,char_offset_end,content_type FROM chunks WHERE library_id=$libraryId";
        cmd.Parameters.AddWithValue("$libraryId", libraryId);

        // Min-heap keeps the top K results; the root is the lowest-scoring entry.
        var heap = new PriorityQueue<RetrievalResult, double>();
        int totalChunks = 0;
        int aboveThreshold = 0;
        double bestScore = double.MinValue;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            totalChunks++;
            var blob = (byte[])reader[7];
            var emb = EmbeddingSerializer.FromBlob(blob);
            var score = DotProductSimd(query, emb);

            if (score > bestScore) bestScore = score;

            if (minimumSimilarity > 0 && score < minimumSimilarity)
                continue;

            aboveThreshold++;

            var result = new RetrievalResult
            {
                Score = score,
                Chunk = new DocumentChunk
                {
                    LibraryId = libraryId,
                    SourceFileName = reader.GetString(0),
                    StoredRelativePath = reader.GetString(1),
                    Page = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    ChunkIndex = reader.GetInt32(3),
                    Text = reader.GetString(4),
                    TextLength = reader.GetInt32(5),
                    Sha256 = reader.GetString(6),
                    Section = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    HeadingPath = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    CharOffsetStart = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                    CharOffsetEnd = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                    ContentType = reader.IsDBNull(12) ? string.Empty : reader.GetString(12)
                }
            };

            var effectiveTopK = Math.Max(1, topK);
            if (heap.Count < effectiveTopK)
            {
                heap.Enqueue(result, score);
            }
            else
            {
                heap.EnqueueDequeue(result, score);
            }
        }

        // Warn when the library exceeds the recommended scale.
        if (totalChunks > RecommendedMaxChunks)
        {
            logger?.Warn($"VectorIndex: library '{libraryId}' has {totalChunks} chunks, exceeding the recommended maximum of {RecommendedMaxChunks}. Search performance may degrade. Consider splitting into smaller libraries.");
        }

        // When a threshold is active, return empty if nothing qualifies.
        if (minimumSimilarity > 0 && aboveThreshold == 0)
        {
            logger?.Debug($"VectorIndex: {totalChunks} chunks found, 0 above threshold ({minimumSimilarity:F2}). Top score: {(totalChunks > 0 ? bestScore : 0):F2}");
            return new List<RetrievalResult>();
        }

        // Drain the heap into a list sorted descending by score.
        var results = new List<RetrievalResult>(heap.Count);
        while (heap.Count > 0)
        {
            results.Add(heap.Dequeue());
        }
        results.Reverse();

        if (minimumSimilarity > 0)
        {
            var topScore = results.Count > 0 ? results[0].Score : 0;
            var lowestIncluded = results.Count > 0 ? results[^1].Score : 0;
            logger?.Debug($"VectorIndex: {totalChunks} chunks found, {aboveThreshold} above threshold ({minimumSimilarity:F2}). Top score: {topScore:F2}, lowest included: {lowestIncluded:F2}");
        }

        return results;
    }

    /// <summary>
    /// Hybrid retrieval: fuses the dense (semantic) arm with a BM25 lexical arm via
    /// reciprocal rank fusion, so exact-token facts the embedder rates below
    /// <paramref name="minimumSimilarity"/> are still surfaced. The dense arm reuses
    /// <see cref="Search"/> (the cosine threshold gates it); the lexical arm is bounded
    /// by its own BM25 ranking, not the cosine floor — that is the point. Returns at
    /// most <paramref name="topK"/> results in fused-rank order; empty only when
    /// <em>both</em> arms come back empty, preserving the dense-only "no relevant
    /// documents" behavior.
    /// </summary>
    public List<RetrievalResult> SearchHybrid(
        string libraryId, float[] queryEmbedding, string queryText, int topK,
        double minimumSimilarity, SsdLogger? logger)
    {
        var effectiveTopK = Math.Max(1, topK);
        var candidatePool = Math.Max(effectiveTopK, HybridCandidatePool);

        // Dense arm: existing threshold-filtered SIMD scan.
        var dense = Search(libraryId, queryEmbedding, candidatePool, minimumSimilarity, logger);

        // Lexical arm: BM25 over the stored chunk text.
        var match = BuildFtsMatchQuery(queryText);
        var lexical = string.IsNullOrEmpty(match)
            ? new List<RetrievalResult>()
            : LexicalSearch(libraryId, match, candidatePool);

        if (dense.Count == 0 && lexical.Count == 0)
        {
            return new List<RetrievalResult>();
        }

        var fused = RrfFusion.Fuse(dense, lexical);
        if (fused.Count > effectiveTopK)
        {
            fused = fused.GetRange(0, effectiveTopK);
        }

        logger?.Debug($"VectorIndex: hybrid fused {dense.Count} dense + {lexical.Count} lexical -> top {fused.Count} (topK={effectiveTopK}).");
        return fused;
    }

    /// <summary>
    /// BM25 lexical search over the FTS5 index. Returns up to <paramref name="limit"/>
    /// matches for <paramref name="matchQuery"/> (an FTS5 MATCH expression — build it
    /// with <see cref="BuildFtsMatchQuery"/>), best match first. Results carry no cosine
    /// score (<see cref="RetrievalResult.Score"/> is 0); rank fusion uses position, not
    /// score, so the raw BM25 value is not propagated.
    /// </summary>
    internal List<RetrievalResult> LexicalSearch(string libraryId, string matchQuery, int limit)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT c.source_file_name, c.stored_relative_path, c.page, c.chunk_index, c.text,
       c.text_length, c.sha256, c.section, c.heading_path, c.char_offset_start,
       c.char_offset_end, c.content_type
FROM chunks_fts
JOIN chunks c ON c.id = chunks_fts.rowid
WHERE chunks_fts MATCH $q AND c.library_id = $libraryId
ORDER BY bm25(chunks_fts)
LIMIT $limit";
        cmd.Parameters.AddWithValue("$q", matchQuery);
        cmd.Parameters.AddWithValue("$libraryId", libraryId);
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<RetrievalResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new RetrievalResult
            {
                Score = 0,
                Chunk = new DocumentChunk
                {
                    LibraryId = libraryId,
                    SourceFileName = reader.GetString(0),
                    StoredRelativePath = reader.GetString(1),
                    Page = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    ChunkIndex = reader.GetInt32(3),
                    Text = reader.GetString(4),
                    TextLength = reader.GetInt32(5),
                    Sha256 = reader.GetString(6),
                    Section = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    HeadingPath = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    CharOffsetStart = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                    CharOffsetEnd = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                    ContentType = reader.IsDBNull(11) ? string.Empty : reader.GetString(11)
                }
            });
        }

        return results;
    }

    /// <summary>
    /// Turns free-text user input into a safe FTS5 MATCH expression. Raw input is an
    /// FTS query language (AND/OR/NEAR/quotes/*/-/parens), so an unescaped question like
    /// "what's the F/A-18?" would raise a MATCH syntax error. Each whitespace token is
    /// wrapped as a quoted phrase (internal quotes doubled) and the phrases are OR-ed,
    /// so punctuation is neutralized and any term may match. Tokens with no
    /// letter/digit (bare punctuation) are dropped. Returns empty when nothing usable
    /// remains, signalling the caller to skip the lexical arm.
    /// </summary>
    internal static string BuildFtsMatchQuery(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return string.Empty;
        }

        var phrases = queryText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Any(char.IsLetterOrDigit))
            .Select(token => "\"" + token.Replace("\"", "\"\"") + "\"");

        return string.Join(" OR ", phrases);
    }

    /// <summary>
    /// Expands a ranked hit list with neighboring chunks so the model reads contiguous
    /// text. For each hit, pulls <c>chunk_index ± 1..radius</c> within the same file,
    /// bounded to the hit's <c>section</c> when it has one (else the same <c>page</c>),
    /// clamped at file edges. Overlapping windows merge into contiguous runs; runs are
    /// ordered by their best (earliest-ranked) hit, read in ascending chunk order within
    /// a run. Neighbors are flagged <see cref="RetrievalResult.IsNeighbor"/> and carry no
    /// score — they're context, not matches, and topK was already applied upstream.
    /// Returns the hits unchanged (as a fresh list) when <paramref name="radius"/> &lt;= 0.
    /// </summary>
    public List<RetrievalResult> ExpandNeighbors(string libraryId, IReadOnlyList<RetrievalResult> hits, int radius)
    {
        if (radius <= 0 || hits.Count == 0)
            return hits.ToList();

        // Rank each distinct hit by its position; keep the first (best-ranked) occurrence
        // and its original result so the hit's score survives.
        var rankByKey = new Dictionary<(string Path, int Index), int>();
        var items = new Dictionary<(string Path, int Index), RetrievalResult>();
        for (var i = 0; i < hits.Count; i++)
        {
            var key = (hits[i].Chunk.StoredRelativePath, hits[i].Chunk.ChunkIndex);
            if (rankByKey.ContainsKey(key)) continue;
            rankByKey[key] = i;
            items[key] = hits[i];
        }

        using var conn = OpenConnection();

        // One range fetch per file covers every hit's window in that file.
        foreach (var fileGroup in hits.GroupBy(h => h.Chunk.StoredRelativePath))
        {
            var path = fileGroup.Key;
            var indices = fileGroup.Select(h => h.Chunk.ChunkIndex).ToList();
            var lo = Math.Max(0, indices.Min() - radius);
            var hi = indices.Max() + radius;
            var rangeChunks = LoadChunksInRange(conn, libraryId, path, lo, hi);

            foreach (var hit in fileGroup)
            {
                var hc = hit.Chunk;
                for (var delta = -radius; delta <= radius; delta++)
                {
                    if (delta == 0) continue;
                    var j = hc.ChunkIndex + delta;
                    if (j < 0) continue;
                    var key = (path, j);
                    if (items.ContainsKey(key)) continue;            // already a hit or added
                    if (!rangeChunks.TryGetValue(j, out var nb)) continue; // file edge / gap
                    // Bound to the hit's section when it has one, else to its page.
                    var inBounds = hc.Section.Length > 0
                        ? string.Equals(nb.Section, hc.Section, StringComparison.Ordinal)
                        : nb.Page == hc.Page;
                    if (!inBounds) continue;
                    items[key] = new RetrievalResult { Chunk = nb, Score = 0, IsNeighbor = true };
                }
            }
        }

        // Split each file's included chunks into contiguous runs, then order runs by their
        // best hit's rank so the strongest context leads.
        var runs = new List<(int RunRank, string Path, int FirstIndex, List<RetrievalResult> Items)>();
        foreach (var fileGroup in items.Values.GroupBy(r => r.Chunk.StoredRelativePath))
        {
            var sorted = fileGroup.OrderBy(r => r.Chunk.ChunkIndex).ToList();
            var run = new List<RetrievalResult>();
            foreach (var item in sorted)
            {
                if (run.Count > 0 && item.Chunk.ChunkIndex != run[^1].Chunk.ChunkIndex + 1)
                {
                    runs.Add(BuildRun(fileGroup.Key, run, rankByKey));
                    run = new List<RetrievalResult>();
                }
                run.Add(item);
            }
            if (run.Count > 0)
                runs.Add(BuildRun(fileGroup.Key, run, rankByKey));
        }

        var ordered = new List<RetrievalResult>(items.Count);
        foreach (var run in runs
            .OrderBy(r => r.RunRank)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.FirstIndex))
        {
            ordered.AddRange(run.Items);
        }

        return ordered;
    }

    private static (int RunRank, string Path, int FirstIndex, List<RetrievalResult> Items) BuildRun(
        string path, List<RetrievalResult> run, Dictionary<(string Path, int Index), int> rankByKey)
    {
        var runRank = int.MaxValue;
        foreach (var item in run)
        {
            if (rankByKey.TryGetValue((path, item.Chunk.ChunkIndex), out var rank) && rank < runRank)
                runRank = rank;
        }
        return (runRank, path, run[0].Chunk.ChunkIndex, run);
    }

    /// <summary>
    /// Loads the chunks of a single file whose <c>chunk_index</c> falls in
    /// [<paramref name="lo"/>, <paramref name="hi"/>], keyed by index. Used by neighbor
    /// expansion to hydrate context chunks; no embedding is read (neighbors aren't scored).
    /// </summary>
    private static Dictionary<int, DocumentChunk> LoadChunksInRange(
        SqliteConnection conn, string libraryId, string storedRelativePath, int lo, int hi)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT source_file_name, stored_relative_path, page, chunk_index, text, text_length, sha256,
       section, heading_path, char_offset_start, char_offset_end, content_type
FROM chunks
WHERE library_id = $libraryId AND stored_relative_path = $path
  AND chunk_index BETWEEN $lo AND $hi";
        cmd.Parameters.AddWithValue("$libraryId", libraryId);
        cmd.Parameters.AddWithValue("$path", storedRelativePath);
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", hi);

        var byIndex = new Dictionary<int, DocumentChunk>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var chunk = new DocumentChunk
            {
                LibraryId = libraryId,
                SourceFileName = reader.GetString(0),
                StoredRelativePath = reader.GetString(1),
                Page = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                ChunkIndex = reader.GetInt32(3),
                Text = reader.GetString(4),
                TextLength = reader.GetInt32(5),
                Sha256 = reader.GetString(6),
                Section = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                HeadingPath = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                CharOffsetStart = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                CharOffsetEnd = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                ContentType = reader.IsDBNull(11) ? string.Empty : reader.GetString(11)
            };
            byIndex[chunk.ChunkIndex] = chunk;
        }

        return byIndex;
    }

    /// <summary>
    /// Returns the total number of chunks stored for a given library.
    /// </summary>
    public int GetChunkCount(string libraryId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE library_id=$libraryId";
        cmd.Parameters.AddWithValue("$libraryId", libraryId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// SIMD-accelerated dot product using <see cref="Vector{T}"/>. When both input
    /// vectors are L2-normalized, the dot product equals cosine similarity. Falls
    /// back to scalar arithmetic on hardware without SIMD support or for the
    /// trailing elements that don't fill a full SIMD register.
    /// </summary>
    public static double DotProductSimd(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0;

        float sum = 0;
        int i = 0;
        int simdLength = Vector<float>.Count;

        if (Vector.IsHardwareAccelerated && a.Length >= simdLength)
        {
            var sumVec = Vector<float>.Zero;
            int limit = a.Length - (a.Length % simdLength);
            for (; i < limit; i += simdLength)
            {
                var va = new Vector<float>(a, i);
                var vb = new Vector<float>(b, i);
                sumVec += va * vb;
            }
            sum = Vector.Sum(sumVec);
        }

        // Scalar tail for remaining elements.
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>
    /// Full cosine similarity (with magnitude computation). Retained for backward
    /// compatibility and for callers that work with non-normalized vectors.
    /// For search over pre-normalized embeddings, prefer <see cref="DotProductSimd"/>.
    /// </summary>
    public static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0;
        double magA = 0;
        double magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA <= double.Epsilon || magB <= double.Epsilon)
        {
            return 0;
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
