using System.Net;
using System.Net.Http.Json;
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
