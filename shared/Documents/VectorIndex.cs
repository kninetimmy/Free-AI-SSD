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
    embedding BLOB NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_chunks_library ON chunks(library_id);
CREATE INDEX IF NOT EXISTS idx_chunks_sha ON chunks(sha256);
";
            cmd.ExecuteNonQuery();

            // Fresh database — mark embeddings as normalized from the start.
            SetMeta(conn, "embeddings_normalized", "1");
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

        var delete = conn.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = "DELETE FROM chunks WHERE library_id=$libraryId AND stored_relative_path=$path";
        delete.Parameters.AddWithValue("$libraryId", libraryId);
        delete.Parameters.AddWithValue("$path", storedRelativePath);
        delete.ExecuteNonQuery();

        foreach (var c in chunks)
        {
            // Pre-normalize so search only needs a dot product.
            var normalized = EmbeddingSerializer.Normalize(c.Embedding);

            var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = @"INSERT INTO chunks (library_id, source_file_name, stored_relative_path, page, chunk_index, text, text_length, sha256, embedding)
VALUES ($libraryId,$source,$stored,$page,$idx,$text,$len,$sha,$emb)";
            ins.Parameters.AddWithValue("$libraryId", c.LibraryId);
            ins.Parameters.AddWithValue("$source", c.SourceFileName);
            ins.Parameters.AddWithValue("$stored", c.StoredRelativePath);
            ins.Parameters.AddWithValue("$page", (object?)c.Page ?? DBNull.Value);
            ins.Parameters.AddWithValue("$idx", c.ChunkIndex);
            ins.Parameters.AddWithValue("$text", c.Text);
            ins.Parameters.AddWithValue("$len", c.TextLength);
            ins.Parameters.AddWithValue("$sha", c.Sha256);
            ins.Parameters.AddWithValue("$emb", EmbeddingSerializer.ToBlob(normalized));
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
        cmd.CommandText = "SELECT source_file_name,stored_relative_path,page,chunk_index,text,text_length,sha256,embedding FROM chunks WHERE library_id=$libraryId";
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
                    Sha256 = reader.GetString(6)
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
