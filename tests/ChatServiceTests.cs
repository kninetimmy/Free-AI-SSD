using System.Net;
using System.Text;
using System.Text.Json;
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

    // ─── #58: model-parameter slider wiring ───────────────────────────────

    [Fact]
    public async Task SendPromptAsync_WithDefaultConfig_DoesNotSendOptionsBlock()
    {
        var capturedBody = await CaptureGenerateRequestBodyAsync(new PortableConfig { ActiveDocumentLibraryId = null });

        using var doc = JsonDocument.Parse(capturedBody);
        Assert.False(doc.RootElement.TryGetProperty("options", out _),
            "Default config must not send an 'options' block so each model keeps its compiled-in defaults.");
    }

    [Fact]
    public async Task SendPromptAsync_WithAllOverrides_SendsEveryOllamaOptionKey()
    {
        var config = new PortableConfig
        {
            ActiveDocumentLibraryId = null,
            ModelContextWindow = 8192,
            ModelTemperature = 0.4,
            ModelTopP = 0.85,
            ModelMaxOutputTokens = 1024
        };
        var capturedBody = await CaptureGenerateRequestBodyAsync(config);

        using var doc = JsonDocument.Parse(capturedBody);
        var options = doc.RootElement.GetProperty("options");
        Assert.Equal(8192, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(0.4, options.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.85, options.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(1024, options.GetProperty("num_predict").GetInt32());
    }

    [Fact]
    public async Task SendPromptAsync_WithOnlyTemperatureOverridden_OmitsOtherKeys()
    {
        var config = new PortableConfig
        {
            ActiveDocumentLibraryId = null,
            ModelTemperature = 0.2 // others stay at sentinel defaults
        };
        var capturedBody = await CaptureGenerateRequestBodyAsync(config);

        using var doc = JsonDocument.Parse(capturedBody);
        var options = doc.RootElement.GetProperty("options");
        Assert.Equal(0.2, options.GetProperty("temperature").GetDouble(), 3);
        Assert.False(options.TryGetProperty("num_ctx", out _));
        Assert.False(options.TryGetProperty("top_p", out _));
        Assert.False(options.TryGetProperty("num_predict", out _));
    }

    // ─── #35: thinking control (top-level `think`) ────────────────────────

    [Fact]
    public async Task SendPromptAsync_WithDefaultConfig_DoesNotSendThinkField()
    {
        var capturedBody = await CaptureGenerateRequestBodyAsync(new PortableConfig { ActiveDocumentLibraryId = null });

        using var doc = JsonDocument.Parse(capturedBody);
        Assert.False(doc.RootElement.TryGetProperty("think", out _),
            "Default config must omit 'think' so non-thinking models aren't rejected with a 400.");
    }

    [Fact]
    public async Task SendPromptAsync_WithThinkOff_SendsTopLevelThinkFalse()
    {
        var config = new PortableConfig { ActiveDocumentLibraryId = null, ModelThinkMode = "off" };
        var capturedBody = await CaptureGenerateRequestBodyAsync(config);

        using var doc = JsonDocument.Parse(capturedBody);
        var think = doc.RootElement.GetProperty("think");
        Assert.Equal(JsonValueKind.False, think.ValueKind);

        // `think` is a top-level field, never an Ollama option.
        if (doc.RootElement.TryGetProperty("options", out var options))
            Assert.False(options.TryGetProperty("think", out _));
    }

    [Fact]
    public async Task SendPromptAsync_WithThinkLevel_SendsTopLevelThinkString()
    {
        var config = new PortableConfig { ActiveDocumentLibraryId = null, ModelThinkMode = "high" };
        var capturedBody = await CaptureGenerateRequestBodyAsync(config);

        using var doc = JsonDocument.Parse(capturedBody);
        var think = doc.RootElement.GetProperty("think");
        Assert.Equal(JsonValueKind.String, think.ValueKind);
        Assert.Equal("high", think.GetString());
    }

    [Fact]
    public async Task SendPromptAsync_WhenModelRejectsThink_ReturnsActionableFailure()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                """{"error":"model \"phi3\" does not support thinking"}""",
                Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler);
        var libraryManager = new DocumentLibraryManager(_tempRoot);
        var service = new ChatService(http, libraryManager, logger: null);
        var config = new PortableConfig { ActiveDocumentLibraryId = null, ModelThinkMode = "off" };

        var result = await service.SendPromptAsync("phi3", "hello", "127.0.0.1:11434", config);

        var failure = Assert.IsType<ChatResult.Failure>(result);
        Assert.Contains("Thinking", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendPromptAsync_When400AndThinkUnset_UsesGenericFailureNotThinkMessage()
    {
        // A 400 unrelated to thinking, with `think` not set, must not be
        // misattributed to the Thinking control.
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"some other problem"}""", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler);
        var libraryManager = new DocumentLibraryManager(_tempRoot);
        var service = new ChatService(http, libraryManager, logger: null);
        var config = new PortableConfig { ActiveDocumentLibraryId = null };

        var result = await service.SendPromptAsync("phi3", "hello", "127.0.0.1:11434", config);

        var failure = Assert.IsType<ChatResult.Failure>(result);
        Assert.DoesNotContain("Thinking", failure.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> CaptureGenerateRequestBodyAsync(PortableConfig config)
    {
        string? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            captured = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return MakeJsonResponse("""{"response":"ok","done":true}""");
        });
        using var http = new HttpClient(handler);
        var libraryManager = new DocumentLibraryManager(_tempRoot);
        var service = new ChatService(http, libraryManager, logger: null);

        await service.SendPromptAsync("phi3", "hi", "127.0.0.1:11434", config);

        Assert.NotNull(captured);
        return captured!;
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
