using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FreeAiSsd.Shared.Documents;

public sealed class VectorIndex
{
    private readonly string _dbPath;

    public VectorIndex(string indexFolderPath, SsdLogger? logger = null)
    {
        Directory.CreateDirectory(indexFolderPath);
        _dbPath = Path.Combine(indexFolderPath, "vectors.db");
        EnsureSchema(logger);
    }

    private void EnsureSchema(SsdLogger? logger)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

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
            return;
        }

        if (hasOldSchema)
        {
            MigrateTextToBlob(conn, logger);
        }
    }

    /// <summary>
    /// Migrates an existing database from JSON TEXT embeddings to binary BLOB storage.
    /// The migration is atomic: either all rows are converted or none are (transaction rollback).
    /// Safe to re-run — a leftover temp table from a previous crashed attempt is cleaned up first.
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

        tx.Commit();
        logger?.Info("Embedding migration to binary format complete.");
    }

    public void UpsertFileChunks(string libraryId, string storedRelativePath, IReadOnlyList<DocumentChunk> chunks)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var tx = conn.BeginTransaction();

        var delete = conn.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = "DELETE FROM chunks WHERE library_id=$libraryId AND stored_relative_path=$path";
        delete.Parameters.AddWithValue("$libraryId", libraryId);
        delete.Parameters.AddWithValue("$path", storedRelativePath);
        delete.ExecuteNonQuery();

        foreach (var c in chunks)
        {
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
            ins.Parameters.AddWithValue("$emb", EmbeddingSerializer.ToBlob(c.Embedding));
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public void RemoveFile(string libraryId, string storedRelativePath)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM chunks WHERE library_id=$libraryId AND stored_relative_path=$path";
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
    /// Searches for the most similar chunks, filtering out any result whose cosine
    /// similarity score falls below <paramref name="minimumSimilarity"/>. Results are
    /// filtered first, then the top <paramref name="topK"/> are returned from what remains.
    /// Returns an empty list when no chunks meet the threshold.
    /// </summary>
    public List<RetrievalResult> Search(string libraryId, float[] queryEmbedding, int topK, double minimumSimilarity, SsdLogger? logger)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_file_name,stored_relative_path,page,chunk_index,text,text_length,sha256,embedding FROM chunks WHERE library_id=$libraryId";
        cmd.Parameters.AddWithValue("$libraryId", libraryId);

        var all = new List<RetrievalResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blob = (byte[])reader[7];
            var emb = EmbeddingSerializer.FromBlob(blob);
            var score = CosineSimilarity(queryEmbedding, emb);
            all.Add(new RetrievalResult
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
            });
        }

        var sorted = all.OrderByDescending(x => x.Score).ToList();
        var aboveThreshold = sorted.Where(x => x.Score >= minimumSimilarity).ToList();
        var filtered = aboveThreshold.Take(Math.Max(1, topK)).ToList();

        // When a threshold is active, return empty if nothing qualifies
        if (minimumSimilarity > 0 && aboveThreshold.Count == 0)
        {
            logger?.Debug($"VectorIndex: {all.Count} chunks found, 0 above threshold ({minimumSimilarity:F2}). Top score: {(sorted.Count > 0 ? sorted[0].Score : 0):F2}");
            return new List<RetrievalResult>();
        }

        if (minimumSimilarity > 0)
        {
            var discarded = all.Count - aboveThreshold.Count;
            var topScore = filtered.Count > 0 ? filtered[0].Score : 0;
            var lowestIncluded = filtered.Count > 0 ? filtered[^1].Score : 0;
            logger?.Debug($"VectorIndex: {all.Count} chunks found, {aboveThreshold.Count} above threshold ({minimumSimilarity:F2}). Top score: {topScore:F2}, lowest included: {lowestIncluded:F2}");
        }

        return filtered;
    }

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
