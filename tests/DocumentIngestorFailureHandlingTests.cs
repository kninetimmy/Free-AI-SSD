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

    /// C2 Stage 2b. Pre-C2 the threshold-exceeded message named only the
    /// ratio + counts, leaving the user (and future debuggers) no path to
    /// the actual cause — typically "model not found" because the
    /// embedder was never pulled by PrepApp. The capture-first-failure
    /// patch piggybacks the underlying exception message onto the
    /// existing error so the next time this fires, the cause is in the
    /// same string the user sees.
    [Fact]
    public async Task IngestFilesAsync_HighFailureRatio_IncludesFirstFailureCause()
    {
        var handler = new ModelNotFoundEmbeddingHandler();
        var (manager, manifest, ingestor) = await CreateIngestorAsync(handler);
        var sourcePath = Path.Combine(_tempRoot, "no-embedder.txt");
        File.WriteAllText(sourcePath, CreateLongText(1600));
        var config = CreateConfig();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config));

        Assert.Contains("embedding failures exceeded threshold", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("First failure:", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The cause text must surface the underlying /api/embed body so
        // the provisioning gap is self-explanatory next time.
        Assert.Contains("model 'nomic-embed-text' not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// Workstream B: the terminal progress frame (empty CurrentFile) reports the
    /// indexed count and the total skipped count — unsupported-extension files
    /// (dropped before the loop) plus in-loop skips (oversize/symlink/validation).
    [Fact]
    public async Task IngestFilesAsync_TerminalFrame_ReportsIndexedAndSkippedCounts()
    {
        var (_, manifest, ingestor) = await CreateIngestorAsync(new FailFirstEmbeddingHandler(failFirstRequests: 0));

        var goodPath = Path.Combine(_tempRoot, "good.txt");
        File.WriteAllText(goodPath, CreateLongText(1600));
        // .docx is genuinely unsupported (.csv/.json are accepted as text by
        // DocumentParser.IsSupported), so it is dropped by the pre-loop filter.
        var unsupportedPath = Path.Combine(_tempRoot, "notes.docx");
        File.WriteAllText(unsupportedPath, "not really a docx");
        var oversizePath = Path.Combine(_tempRoot, "oversize.txt");
        File.WriteAllText(oversizePath, new string('a', 2 * 1024 * 1024));

        // 1 MB cap → the 2 MB .txt trips the in-loop oversize gate.
        var config = CreateConfig();
        config.MaxDocumentSizeMB = 1;

        var frames = new List<IndexingProgress>();
        await ingestor.IngestFilesAsync(
            manifest,
            new[] { goodPath, oversizePath, unsupportedPath },
            "localhost:11434",
            config,
            frames.Add);

        var terminal = frames.Last();
        Assert.Equal(string.Empty, terminal.CurrentFile);
        Assert.Equal(1, terminal.CompletedFiles);   // only good.txt indexed
        Assert.Equal(2, terminal.SkippedFiles);      // oversize + unsupported
        Assert.Single(manifest.Files);
    }

    /// Workstream B: a tolerated partial-failure run still surfaces FailedChunks
    /// on a progress frame so the UI can show "— N failed" without the whole
    /// ingest aborting.
    [Fact]
    public async Task IngestFilesAsync_PartialChunkFailure_EmitsFailedChunksFrame()
    {
        var (_, manifest, ingestor) = await CreateIngestorAsync(new FailFirstEmbeddingHandler(failFirstRequests: 1));
        var sourcePath = Path.Combine(_tempRoot, "partial.txt");
        File.WriteAllText(sourcePath, CreateLongText(1600));
        var config = CreateConfig();

        var frames = new List<IndexingProgress>();
        await ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config, frames.Add);

        Assert.Contains(frames, f => f.FailedChunks > 0);
        Assert.Single(manifest.Files);
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

    /// X18 Stage 2: the abort threshold is now config-driven. A single dropped chunk is
    /// tolerated at the 0.50 default (see AcceptableFailureRatio above), but a zero-tolerance
    /// config aborts the same ingest — proving the knob changes behavior.
    [Fact]
    public async Task IngestFilesAsync_ZeroToleranceThreshold_AbortsOnSingleChunkFailure()
    {
        var handler = new FailFirstEmbeddingHandler(failFirstRequests: 1);
        var (manager, manifest, ingestor) = await CreateIngestorAsync(handler);
        var sourcePath = Path.Combine(_tempRoot, "zero-tolerance.txt");
        File.WriteAllText(sourcePath, CreateLongText(1600));
        var config = CreateConfig();
        config.MaxEmbeddingFailureRatioBeforeAbort = 0.0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config));

        Assert.Contains("embedding failures exceeded threshold", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(manifest.Files);

        var vectorIndex = new VectorIndex(manager.GetIndexPath(manifest.Id));
        Assert.Equal(0, vectorIndex.GetChunkCount(manifest.Id));
    }

    /// X18 Stage 1: a tolerated partial failure still indexes the file, and the terminal
    /// frame (empty CurrentFile) carries the batch dropped-chunk total so the completion
    /// summary can surface it — not just the transient per-file frame.
    [Fact]
    public async Task IngestFilesAsync_ToleratedPartialFailure_TerminalFrameCarriesBatchFailedChunks()
    {
        var handler = new FailFirstEmbeddingHandler(failFirstRequests: 1);
        var (_, manifest, ingestor) = await CreateIngestorAsync(handler);
        var sourcePath = Path.Combine(_tempRoot, "partial-terminal.txt");
        File.WriteAllText(sourcePath, CreateLongText(1600));
        var config = CreateConfig();

        var frames = new List<IndexingProgress>();
        await ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, "localhost:11434", config, frames.Add);

        var terminal = frames.Last();
        Assert.Equal(string.Empty, terminal.CurrentFile);
        Assert.Equal(1, terminal.FailedChunks);
        Assert.Single(manifest.Files);
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

    /// C2 fixture: simulates the actual production failure mode — Ollama
    /// returns 404 with `{"error":"model 'X' not found"}` when the
    /// embedder was never pulled. Combined with the C2 EmbeddingClient
    /// hardening, this body should flow through to the threshold message.
    private sealed class ModelNotFoundEmbeddingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{\"error\":\"model 'nomic-embed-text' not found\"}")
            };
            return Task.FromResult(response);
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
