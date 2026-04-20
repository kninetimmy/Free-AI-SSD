using System.Net;
using System.Text;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Xunit;

namespace FreeAiSsd.Tests;

public sealed class ChatServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public ChatServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "freeai-chatservice-test-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task SendPromptAsync_WhenHttpFails_ReturnsChatResultFailure()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var libraryManager = new DocumentLibraryManager(_tempRoot);
        var service = new ChatService(http, libraryManager, logger: null);
        var config = new PortableConfig { ActiveDocumentLibraryId = null };

        var result = await service.SendPromptAsync("phi3", "hello", "127.0.0.1:11434", config);

        Assert.IsType<ChatResult.Failure>(result);
    }

    [Fact]
    public async Task SendPromptAsync_WhenOllamaUnreachable_ReturnsFriendlyFailureMessage()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException(
            "Connection refused",
            new System.Net.Sockets.SocketException()));
        using var http = new HttpClient(handler);
        var libraryManager = new DocumentLibraryManager(_tempRoot);
        var service = new ChatService(http, libraryManager, logger: null);
        var config = new PortableConfig { ActiveDocumentLibraryId = null };

        var result = await service.SendPromptAsync("phi3", "hello", "127.0.0.1:11434", config);

        var failure = Assert.IsType<ChatResult.Failure>(result);
        Assert.Contains("Ollama", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendPromptAsync_WhenSuccessful_ReturnsChatResultSuccess()
    {
        var handler = new StubHandler((_, _) =>
            MakeJsonResponse("""{"response":"pong","done":true}"""));
        using var http = new HttpClient(handler);
        var libraryManager = new DocumentLibraryManager(_tempRoot);
        var service = new ChatService(http, libraryManager, logger: null);
        var config = new PortableConfig { ActiveDocumentLibraryId = null };

        var result = await service.SendPromptAsync("phi3", "ping", "127.0.0.1:11434", config);

        var success = Assert.IsType<ChatResult.Success>(result);
        Assert.Equal("pong", success.Response.ResponseText);
    }

    [Fact]
    public async Task SendPromptAsync_WhenRagLibraryMissing_ReturnsRagRetrievalFailed()
    {
        var handler = new StubHandler((_, _) =>
            MakeJsonResponse("""{"response":"fallback answer","done":true}"""));
        using var http = new HttpClient(handler);
        var libraryManager = new DocumentLibraryManager(_tempRoot);
        var service = new ChatService(http, libraryManager, logger: null);
        var config = new PortableConfig { ActiveDocumentLibraryId = "nonexistent-library-id" };

        var result = await service.SendPromptAsync("phi3", "what does the manual say?", "127.0.0.1:11434", config);

        Assert.IsType<ChatResult.RagRetrievalFailed>(result);
    }

    [Fact]
    public async Task SendPromptStreamingAsync_WhenHttpFails_ReturnsChatResultFailure()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var libraryManager = new DocumentLibraryManager(_tempRoot);
        var service = new ChatService(http, libraryManager, logger: null);
        var config = new PortableConfig { ActiveDocumentLibraryId = null };

        var result = await service.SendPromptStreamingAsync("phi3", "hello", "127.0.0.1:11434", config,
            _ => Task.CompletedTask);

        Assert.IsType<ChatResult.Failure>(result);
    }

    private static HttpResponseMessage MakeJsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, cancellationToken));
    }
}
