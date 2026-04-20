using System.Net;
using System.Net.Http.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

public sealed class DocumentReplacementTests : IDisposable
{
    private readonly string _tempRoot;

    public DocumentReplacementTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"doc-replacement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task IngestFilesAsync_ReplaceSamePath_PurgesOldChunksAndStoredFile()
    {
        var spy = new CountingEmbeddingHandler();
        var (manager, manifest, ingestor) = CreateIngestor(spy);
        var config = CreateConfig();

        var sourcePath = Path.Combine(_tempRoot, "manual.txt");
        File.WriteAllText(sourcePath, CreateLongText(600));
        await ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config);

        var firstStoredRelPath = manifest.Files.Single().StoredRelativePath;
        var firstStoredAbsPath = Path.Combine(manager.GetLibraryPath(manifest.Id), firstStoredRelPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(firstStoredAbsPath));

        // Replace with new content at the same path.
        File.WriteAllText(sourcePath, CreateLongText(700));
        await ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config);

        var secondStoredRelPath = manifest.Files.Single().StoredRelativePath;
        Assert.NotEqual(firstStoredRelPath, secondStoredRelPath);

        // Old stored file must be gone from disk.
        Assert.False(File.Exists(firstStoredAbsPath));

        // No orphan chunks for old path.
        Assert.Equal(0, CountChunksForPath(manager.GetIndexPath(manifest.Id), manifest.Id, firstStoredRelPath));

        // New chunks exist for new path.
        Assert.True(CountChunksForPath(manager.GetIndexPath(manifest.Id), manifest.Id, secondStoredRelPath) > 0);

        // Only one manifest entry.
        Assert.Single(manifest.Files);
    }

    [Fact]
    public async Task IngestFilesAsync_RenameWithSameContent_UpdatesPathAndSkipsReEmbed()
    {
        var spy = new CountingEmbeddingHandler();
        var (manager, manifest, ingestor) = CreateIngestor(spy);
        var config = CreateConfig();

        var content = CreateLongText(600);
        var p1 = Path.Combine(_tempRoot, "original.txt");
        File.WriteAllText(p1, content);
        await ingestor.IngestFilesAsync(manifest, new[] { p1 }, "localhost:11434", config);

        var callsAfterFirstIngest = spy.RequestCount;
        var storedRelPathAfterFirst = manifest.Files.Single().StoredRelativePath;
        var chunksAfterFirst = CountChunksForPath(manager.GetIndexPath(manifest.Id), manifest.Id, storedRelPathAfterFirst);

        // Copy same bytes to new filename (simulates rename).
        var p2 = Path.Combine(_tempRoot, "renamed.txt");
        File.WriteAllText(p2, content);
        await ingestor.IngestFilesAsync(manifest, new[] { p2 }, "localhost:11434", config);

        // No additional embedding calls — rename detected, re-embed skipped.
        Assert.Equal(callsAfterFirstIngest, spy.RequestCount);

        // Manifest has exactly one entry, with the updated source path.
        Assert.Single(manifest.Files);
        Assert.Equal(p2, manifest.Files[0].SourceOriginalPath, StringComparer.OrdinalIgnoreCase);

        // DB chunk count for the stored path is unchanged.
        Assert.Equal(chunksAfterFirst, CountChunksForPath(manager.GetIndexPath(manifest.Id), manifest.Id, storedRelPathAfterFirst));
    }

    [Fact]
    public async Task IngestFilesAsync_SourceDeletedThenSameContentAtNewPath_RenameDetected()
    {
        var spy = new CountingEmbeddingHandler();
        var (manager, manifest, ingestor) = CreateIngestor(spy);
        var config = CreateConfig();

        var content = CreateLongText(600);
        var p1 = Path.Combine(_tempRoot, "source.txt");
        File.WriteAllText(p1, content);
        await ingestor.IngestFilesAsync(manifest, new[] { p1 }, "localhost:11434", config);

        var callsAfterFirst = spy.RequestCount;
        File.Delete(p1);

        var p2 = Path.Combine(_tempRoot, "moved.txt");
        File.WriteAllText(p2, content);
        await ingestor.IngestFilesAsync(manifest, new[] { p2 }, "localhost:11434", config);

        // Rename detected: no re-embedding.
        Assert.Equal(callsAfterFirst, spy.RequestCount);

        // Manifest updated to new path.
        Assert.Single(manifest.Files);
        Assert.Equal(p2, manifest.Files[0].SourceOriginalPath, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IngestFilesAsync_MultipleLegacyEntriesWithSameSha_FallsThroughToNewEntry()
    {
        var spy = new CountingEmbeddingHandler();
        var (manager, manifest, ingestor) = CreateIngestor(spy);
        var config = CreateConfig();

        // Create the file we want to ingest.
        var content = CreateLongText(600);
        var p3 = Path.Combine(_tempRoot, "third.txt");
        File.WriteAllText(p3, content);
        var sha = DocumentHasher.ComputeSha256(p3);

        // Pre-seed two manifest entries with the same sha (simulates legacy duplicates pre-Stage 2).
        manifest.Files.Add(new DocumentFileEntry
        {
            SourceOriginalPath = Path.Combine(_tempRoot, "legacy1.txt"),
            StoredRelativePath = $"files/{sha[..12]}_legacy1.txt",
            FileName = "legacy1.txt",
            Sha256 = sha,
        });
        manifest.Files.Add(new DocumentFileEntry
        {
            SourceOriginalPath = Path.Combine(_tempRoot, "legacy2.txt"),
            StoredRelativePath = $"files/{sha[..12]}_legacy2.txt",
            FileName = "legacy2.txt",
            Sha256 = sha,
        });

        // Ingest at new path — >1 sha match, so falls through to new-entry behavior.
        await ingestor.IngestFilesAsync(manifest, new[] { p3 }, "localhost:11434", config);

        Assert.Equal(3, manifest.Files.Count);
        Assert.Contains(manifest.Files, f => string.Equals(f.SourceOriginalPath, p3, StringComparison.OrdinalIgnoreCase));
        // Pre-seeded entries untouched.
        Assert.Contains(manifest.Files, f => f.FileName == "legacy1.txt");
        Assert.Contains(manifest.Files, f => f.FileName == "legacy2.txt");
    }

    [Fact]
    public async Task IngestFilesAsync_EditAndRenameSimultaneously_TreatedAsNewEntry()
    {
        var spy = new CountingEmbeddingHandler();
        var (manager, manifest, ingestor) = CreateIngestor(spy);
        var config = CreateConfig();

        var p1 = Path.Combine(_tempRoot, "original.txt");
        File.WriteAllText(p1, CreateLongText(600));
        await ingestor.IngestFilesAsync(manifest, new[] { p1 }, "localhost:11434", config);
        Assert.Single(manifest.Files);

        // Different path AND different content → sha and path both changed → new entry.
        var p2 = Path.Combine(_tempRoot, "edited-renamed.txt");
        File.WriteAllText(p2, CreateLongText(700));
        await ingestor.IngestFilesAsync(manifest, new[] { p2 }, "localhost:11434", config);

        Assert.Equal(2, manifest.Files.Count);
        Assert.Contains(manifest.Files, f => string.Equals(f.SourceOriginalPath, p1, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(manifest.Files, f => string.Equals(f.SourceOriginalPath, p2, StringComparison.OrdinalIgnoreCase));
    }

    private (DocumentLibraryManager Manager, DocumentLibraryManifest Manifest, DocumentIngestor Ingestor) CreateIngestor(HttpMessageHandler handler)
    {
        var ssdRoot = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = manager.CreateLibraryAsync("test-lib").GetAwaiter().GetResult();
        var embeddingClient = new EmbeddingClient(new HttpClient(handler));
        var ingestor = new DocumentIngestor(manager, embeddingClient);
        return (manager, manifest, ingestor);
    }

    private static PortableConfig CreateConfig() => new()
    {
        ChunkSize = 200,
        ChunkOverlap = 0,
        MaxEmbeddingConcurrency = 1,
    };

    private static string CreateLongText(int approxLength)
    {
        var words = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega ";
        var text = words;
        while (text.Length < approxLength)
            text += words;
        return text;
    }

    private static int CountChunksForPath(string indexPath, string libraryId, string storedRelativePath)
    {
        var dbPath = Path.Combine(indexPath, "vectors.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE library_id=$lib AND stored_relative_path=$path";
        cmd.Parameters.AddWithValue("$lib", libraryId);
        cmd.Parameters.AddWithValue("$path", storedRelativePath);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private sealed class CountingEmbeddingHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            var embedding = new[] { 1f, 0f, 0f };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { embeddings = new[] { embedding } })
            };
            return Task.FromResult(response);
        }
    }
}
