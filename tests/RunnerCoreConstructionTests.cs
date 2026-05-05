using System.Net;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Tests;

public sealed class RunnerCoreConstructionTests : IDisposable
{
    private readonly string _tempRoot;

    public RunnerCoreConstructionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "freeai-runnercore-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
        SsdLayout.EnsureStructure(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void CoreServices_ConstructWithoutWpfHost()
    {
        using var http = new HttpClient(new StubHandler());
        var libraryManager = new DocumentLibraryManager(_tempRoot);
        var embeddingClient = new EmbeddingClient(http);
        var ingestor = new DocumentIngestor(libraryManager, embeddingClient);

        var chat = new ChatService(http, libraryManager, logger: null);
        var docs = new DocumentOperationsService(libraryManager, ingestor, new ConfigStore());
        var models = new ModelManagementService(http, UnknownSystemResourceProbe.Instance);
        var api = new RunnerLocalApiService(chat, new FakeSttService(), new TtsProvider(), logger: null, ssdRoot: _tempRoot);

        Assert.False(api.IsRunning);
        Assert.Empty(models.GetInstalledModelNames(new PortableConfig()));
        Assert.NotNull(docs.GetLibraryDisplayInfo(new PortableConfig()));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class FakeSttService : ISpeechToTextService
    {
        public event Action<string>? LogMessage;

        public bool IsModelLoaded => true;

        public void Dispose()
        {
        }

        public Task InitializeAsync(string ssdRoot, PortableConfig config) => Task.CompletedTask;

        public Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData)
            => Task.FromResult<TranscriptionResult>(new TranscriptionResult.Success("test"));

        public Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData, CancellationToken cancellationToken)
            => Task.FromResult<TranscriptionResult>(new TranscriptionResult.Success("test"));

        public Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream)
            => Task.FromResult<TranscriptionResult>(new TranscriptionResult.Success("test"));

        public Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream, CancellationToken cancellationToken)
            => Task.FromResult<TranscriptionResult>(new TranscriptionResult.Success("test"));
    }
}
