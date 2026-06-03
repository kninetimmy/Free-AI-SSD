using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

/// C2 Stage 2a. EmbeddingClient pre-C2 used EnsureSuccessStatusCode,
/// which throws an HttpRequestException whose message is generic
/// ("Response status code does not indicate success: 404 (Not Found).").
/// The `/api/embed` body — which carries the actual reason
/// (`{"error":"model 'X' not found"}`) — was discarded. These pins
/// verify the body now flows through into the exception message so
/// the provisioning gap is debuggable from the user-facing error.
public sealed class EmbeddingClientTests
{
    [Fact]
    public async Task EmbedAsync_404Response_RaisesWithBodyAndStatusCode()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.NotFound,
            "{\"error\":\"model 'nomic-embed-text' not found\"}");
        var client = new EmbeddingClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.EmbedAsync("localhost:11434", "nomic-embed-text", "hello"));

        Assert.Contains("404", ex.Message);
        Assert.Contains("nomic-embed-text", ex.Message);
        Assert.Contains("model 'nomic-embed-text' not found", ex.Message);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task EmbedAsync_500Response_EmptyBody_StillReadable()
    {
        var handler = new StaticResponseHandler(HttpStatusCode.InternalServerError, string.Empty);
        var client = new EmbeddingClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.EmbedAsync("localhost:11434", "nomic-embed-text", "hello"));

        Assert.Contains("500", ex.Message);
        Assert.Contains("(empty body)", ex.Message);
    }

    [Fact]
    public async Task EmbedAsync_200WithEmbedding_ReturnsVector()
    {
        var handler = new JsonResponseHandler(HttpStatusCode.OK,
            new { embeddings = new[] { new[] { 0.5f, -0.5f, 0.25f } } });
        var client = new EmbeddingClient(new HttpClient(handler));

        var vector = await client.EmbedAsync("localhost:11434", "nomic-embed-text", "hello");

        Assert.Equal(3, vector.Length);
        Assert.Equal(0.5f, vector[0]);
        Assert.Equal(-0.5f, vector[1]);
    }

    // --- Embed-context thrash fix: pin num_ctx + truncate, clip oversized input ---
    // Regression guard for the freeze where token-dense chunks made Ollama reload the
    // embed model 300+ times. Pinning options.num_ctx keeps the embed runner stable and
    // truncate=true lets an over-long input fit instead of erroring.

    [Fact]
    public async Task EmbedAsync_PinsNumCtxAndTruncate_WhenNumCtxProvided()
    {
        var handler = new CapturingHandler();
        var client = new EmbeddingClient(new HttpClient(handler));

        await client.EmbedAsync("localhost:11434", "nomic-embed-text", "hello",
            default, numCtx: 2048, maxInputChars: null);

        using var doc = JsonDocument.Parse(handler.CapturedJson!);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("truncate").GetBoolean());
        Assert.Equal(2048, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public async Task EmbedAsync_OmitsOptions_ButKeepsTruncate_WhenNumCtxNull()
    {
        var handler = new CapturingHandler();
        var client = new EmbeddingClient(new HttpClient(handler));

        await client.EmbedAsync("localhost:11434", "nomic-embed-text", "hello");

        using var doc = JsonDocument.Parse(handler.CapturedJson!);
        Assert.False(doc.RootElement.TryGetProperty("options", out _));
        Assert.True(doc.RootElement.GetProperty("truncate").GetBoolean());
    }

    [Fact]
    public async Task EmbedAsync_ClipsInput_ToMaxInputChars()
    {
        var handler = new CapturingHandler();
        var client = new EmbeddingClient(new HttpClient(handler));
        var longInput = new string('x', 5000);

        await client.EmbedAsync("localhost:11434", "nomic-embed-text", longInput,
            default, numCtx: null, maxInputChars: 100);

        using var doc = JsonDocument.Parse(handler.CapturedJson!);
        Assert.Equal(100, doc.RootElement.GetProperty("input").GetString()!.Length);
    }

    [Fact]
    public async Task EmbedBatchAsync_ClipsEachInput_AndPinsNumCtx()
    {
        var handler = new CapturingHandler(embeddingCount: 2);
        var client = new EmbeddingClient(new HttpClient(handler));
        var inputs = new[] { new string('a', 5000), "short" };

        await client.EmbedBatchAsync("localhost:11434", "nomic-embed-text", inputs,
            default, numCtx: 4096, maxInputChars: 100);

        using var doc = JsonDocument.Parse(handler.CapturedJson!);
        var sent = doc.RootElement.GetProperty("input").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();
        Assert.Equal(100, sent[0].Length);   // long input clipped
        Assert.Equal("short", sent[1]);      // short input untouched
        Assert.Equal(4096, doc.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly int _embeddingCount;
        public CapturingHandler(int embeddingCount = 1) => _embeddingCount = embeddingCount;
        public string? CapturedJson { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            var embeddings = Enumerable.Range(0, _embeddingCount)
                .Select(_ => new[] { 0.1f, 0.2f }).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { embeddings })
            };
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        public StaticResponseHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class JsonResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly object _payload;
        public JsonResponseHandler(HttpStatusCode status, object payload)
        {
            _status = status;
            _payload = payload;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_status)
            {
                Content = JsonContent.Create(_payload)
            };
            return Task.FromResult(response);
        }
    }
}
