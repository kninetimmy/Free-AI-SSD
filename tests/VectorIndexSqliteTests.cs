using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

public class VectorIndexSqliteTests
{
    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), $"vi-sqlite-{Guid.NewGuid():N}");

    [Fact]
    public void OpenConnection_SetsWalJournalMode()
    {
        var root = TempRoot();
        var index = new VectorIndex(root);

        using var conn = index.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        var result = cmd.ExecuteScalar() as string;

        Assert.Equal("wal", result);
    }

    [Fact]
    public void OpenConnection_SetsBusyTimeout5000()
    {
        var root = TempRoot();
        var index = new VectorIndex(root);

        using var conn = index.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout";
        var result = Convert.ToInt32(cmd.ExecuteScalar());

        Assert.Equal(5000, result);
    }

    [Fact]
    public async Task ConcurrentWriters_SecondWriterCompletesWithinBusyTimeout()
    {
        var root = TempRoot();
        var index = new VectorIndex(root);
        var dbPath = Path.Combine(root, "vectors.db");

        // Hold a write lock for 200 ms via BEGIN IMMEDIATE on a raw connection.
        // In WAL mode, writers serialize — a second writer gets SQLITE_BUSY until
        // the first commits or rolls back. busy_timeout=5000 gives it 5 s to wait,
        // so the 200 ms hold should be absorbed without a SqliteException.
        using var blocker = new SqliteConnection($"Data Source={dbPath}");
        blocker.Open();
        using var tx = blocker.BeginTransaction();
        using (var cmd = blocker.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('lock_test', '1')";
            cmd.ExecuteNonQuery();
        }

        var writerTask = Task.Run(() => index.UpsertFileChunks("lib1", "files/a.txt",
            new List<DocumentChunk>
            {
                new() { LibraryId = "lib1", SourceFileName = "a.txt",
                        StoredRelativePath = "files/a.txt", ChunkIndex = 0,
                        Text = "x", TextLength = 1, Sha256 = "x",
                        Embedding = new float[] { 1, 0, 0 } },
            }));

        await Task.Delay(200);
        tx.Rollback();

        var ex = await Record.ExceptionAsync(async () => await writerTask);
        Assert.Null(ex);
    }
}
