using System.Net;
using System.Net.Http.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

public sealed class DocumentRebuildTests : IDisposable
{
    private readonly string _tempRoot;

    public DocumentRebuildTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"doc-rebuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task RebuildIndexAsync_SourceMissingStoredPresent_ReembedFromStoredCopy()
    {
        var spy = new CountingEmbeddingHandler();
        var (manager, manifest, ingestor) = CreateIngestor(spy);
        var config = CreateConfig();

        var sourcePath = Path.Combine(_tempRoot, "manual.txt");
        File.WriteAllText(sourcePath, CreateLongText(600));
        await ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config);

        var callsAfterIngest = spy.RequestCount;
        var storedRelPath = manifest.Files.Single().StoredRelativePath;
        var storedAbsPath = Path.Combine(manager.GetLibraryPath(manifest.Id), storedRelPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(storedAbsPath));

        File.Delete(sourcePath);
        Assert.False(File.Exists(sourcePath));

        await ingestor.RebuildIndexAsync(manifest, "localhost:11434", config);

        // Entry is preserved in manifest (SourceOriginalPath left pointing to the missing original).
        Assert.Single(manifest.Files);
        Assert.Equal(sourcePath, manifest.Files[0].SourceOriginalPath, StringComparer.OrdinalIgnoreCase);

        // Chunks were re-embedded from the stored copy.
        Assert.True(CountChunksForPath(manager.GetIndexPath(manifest.Id), manifest.Id, storedRelPath) > 0);
        Assert.True(spy.RequestCount > callsAfterIngest);
    }

    [Fact]
    public async Task RebuildIndexAsync_SourceAndStoredBothMissing_DropsEntryFromManifest()
    {
        var spy = new CountingEmbeddingHandler();
        var (manager, manifest, ingestor) = CreateIngestor(spy);
        var config = CreateConfig();

        var sourcePath = Path.Combine(_tempRoot, "vanished.txt");
        File.WriteAllText(sourcePath, CreateLongText(600));
        await ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config);

        var storedRelPath = manifest.Files.Single().StoredRelativePath;
        var storedAbsPath = Path.Combine(manager.GetLibraryPath(manifest.Id), storedRelPath.Replace('/', Path.DirectorySeparatorChar));

        File.Delete(sourcePath);
        File.Delete(storedAbsPath);

        await ingestor.RebuildIndexAsync(manifest, "localhost:11434", config);

        // Entry dropped — both source and stored copy are gone.
        Assert.Empty(manifest.Files);
        Assert.Equal(0, CountChunksForPath(manager.GetIndexPath(manifest.Id), manifest.Id, storedRelPath));
    }

    [Fact]
    public async Task RebuildIndexAsync_AllSourcesPresent_PreservesChunkCount()
    {
        var spy = new CountingEmbeddingHandler();
        var (manager, manifest, ingestor) = CreateIngestor(spy);
        var config = CreateConfig();

        var p1 = Path.Combine(_tempRoot, "doc1.txt");
        var p2 = Path.Combine(_tempRoot, "doc2.txt");
        File.WriteAllText(p1, CreateLongText(600));
        File.WriteAllText(p2, CreateLongText(800));
        await ingestor.IngestFilesAsync(manifest, new[] { p1, p2 }, "localhost:11434", config);

        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        var chunksBefore = vectorIndex.GetChunkCount(manifest.Id);
        Assert.True(chunksBefore > 0);

        await ingestor.RebuildIndexAsync(manifest, "localhost:11434", config);

        var chunksAfter = new VectorIndex(manager.GetIndexPath(manifest.Id)).GetChunkCount(manifest.Id);
        Assert.Equal(chunksBefore, chunksAfter);
    }

    [Fact(Skip = "Depends on X21 — provenance gate requires embedding_model_id + model_version columns in chunks table.")]
    public Task RebuildIndexAsync_ProvenanceMatches_SkipsReEmbed()
    {
        // Real test lands with X21. Placeholder keeps the contract visible.
        return Task.CompletedTask;
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
