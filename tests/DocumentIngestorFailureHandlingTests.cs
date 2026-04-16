using System.Net;
using System.Net.Http.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

public sealed class DocumentIngestorFailureHandlingTests : IDisposable
{
    private readonly string _tempRoot;

    public DocumentIngestorFailureHandlingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"doc-ingestor-failure-{Guid.NewGuid():N}");
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
    public async Task IngestFilesAsync_ZeroChunks_ThrowsAndDoesNotPersist()
    {
        var (manager, manifest, ingestor) = await CreateIngestorAsync(new FailFirstEmbeddingHandler(0));
        var sourcePath = Path.Combine(_tempRoot, "blank.txt");
        File.WriteAllText(sourcePath, "   \r\n\t   ");
        var config = CreateConfig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config));

        Assert.Contains("no chunks were generated", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manifest.Files);

        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        Assert.Equal(0, vectorIndex.GetChunkCount(manifest.Id));
    }

    [Fact]
    public async Task IngestFilesAsync_HighFailureRatio_ThrowsAndDoesNotPersist()
    {
        var handler = new AlwaysFailEmbeddingHandler();
        var (manager, manifest, ingestor) = await CreateIngestorAsync(handler);
        var sourcePath = Path.Combine(_tempRoot, "high-failure.txt");
        File.WriteAllText(sourcePath, CreateLongText(1600));
        var config = CreateConfig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config));

        Assert.Contains("embedding failures exceeded threshold", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ratio=", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(handler.RequestCount > 0);
        Assert.Empty(manifest.Files);

        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        Assert.Equal(0, vectorIndex.GetChunkCount(manifest.Id));
    }

    [Fact]
    public async Task IngestFilesAsync_AcceptableFailureRatio_PersistsSuccessfulChunksOnly()
    {
        var handler = new FailFirstEmbeddingHandler(failFirstRequests: 1);
        var (manager, manifest, ingestor) = await CreateIngestorAsync(handler);
        var sourcePath = Path.Combine(_tempRoot, "partial-success.txt");
        File.WriteAllText(sourcePath, CreateLongText(1600));
        var config = CreateConfig();

        await ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config);

        Assert.Single(manifest.Files);
        Assert.True(handler.RequestCount > 1);
        Assert.Equal(1, handler.FailedCount);

        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        Assert.Equal(handler.RequestCount - handler.FailedCount, vectorIndex.GetChunkCount(manifest.Id));
    }

    private async Task<(DocumentLibraryManager Manager, DocumentLibraryManifest Manifest, DocumentIngestor Ingestor)> CreateIngestorAsync(HttpMessageHandler handler)
    {
        var ssdRoot = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("test-lib");
        var embeddingClient = new EmbeddingClient(new HttpClient(handler));
        var ingestor = new DocumentIngestor(manager, embeddingClient);
        return (manager, manifest, ingestor);
    }

    private static PortableConfig CreateConfig() => new()
    {
        ChunkSize = 200,
        ChunkOverlap = 0,
        MaxEmbeddingConcurrency = 1
    };

    private static string CreateLongText(int approxLength)
    {
        var words = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon phi chi psi omega ";
        var text = words;
        while (text.Length < approxLength)
        {
            text += words;
        }

        return text;
    }

    private sealed class AlwaysFailEmbeddingHandler : HttpMessageHandler
    {
        private int _requestCount;

        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class FailFirstEmbeddingHandler : HttpMessageHandler
    {
        private readonly int _failFirstRequests;
        private int _requestCount;
        private int _failedCount;

        public FailFirstEmbeddingHandler(int failFirstRequests)
        {
            _failFirstRequests = failFirstRequests;
        }

        public int RequestCount => _requestCount;
        public int FailedCount => _failedCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestNumber = Interlocked.Increment(ref _requestCount);
            if (requestNumber <= _failFirstRequests)
            {
                Interlocked.Increment(ref _failedCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            var embedding = new[] { 1f, 0f, 0f };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { embeddings = new[] { embedding } })
            };
            return Task.FromResult(response);
        }
    }
}
