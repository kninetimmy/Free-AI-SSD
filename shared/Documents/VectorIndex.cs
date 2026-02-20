using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FreeAiSsd.Shared.Documents;

public sealed class VectorIndex
{
    private readonly string _dbPath;

    public VectorIndex(string indexFolderPath)
    {
        Directory.CreateDirectory(indexFolderPath);
        _dbPath = Path.Combine(indexFolderPath, "vectors.db");
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
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
    embedding_json TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_chunks_library ON chunks(library_id);
CREATE INDEX IF NOT EXISTS idx_chunks_sha ON chunks(sha256);
";
        cmd.ExecuteNonQuery();
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
            ins.CommandText = @"INSERT INTO chunks (library_id, source_file_name, stored_relative_path, page, chunk_index, text, text_length, sha256, embedding_json)
VALUES ($libraryId,$source,$stored,$page,$idx,$text,$len,$sha,$emb)";
            ins.Parameters.AddWithValue("$libraryId", c.LibraryId);
            ins.Parameters.AddWithValue("$source", c.SourceFileName);
            ins.Parameters.AddWithValue("$stored", c.StoredRelativePath);
            ins.Parameters.AddWithValue("$page", (object?)c.Page ?? DBNull.Value);
            ins.Parameters.AddWithValue("$idx", c.ChunkIndex);
            ins.Parameters.AddWithValue("$text", c.Text);
            ins.Parameters.AddWithValue("$len", c.TextLength);
            ins.Parameters.AddWithValue("$sha", c.Sha256);
            ins.Parameters.AddWithValue("$emb", JsonSerializer.Serialize(c.Embedding));
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

    public List<RetrievalResult> Search(string libraryId, float[] queryEmbedding, int topK)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source_file_name,stored_relative_path,page,chunk_index,text,text_length,sha256,embedding_json FROM chunks WHERE library_id=$libraryId";
        cmd.Parameters.AddWithValue("$libraryId", libraryId);

        var all = new List<RetrievalResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var emb = JsonSerializer.Deserialize<float[]>(reader.GetString(7)) ?? Array.Empty<float>();
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

        return all.OrderByDescending(x => x.Score).Take(Math.Max(1, topK)).ToList();
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
