using System.Net;
using System.Net.Http;
using System.Text;
using FreeAiSsd.RunnerCli;

namespace FreeAiSsd.Tests;

public sealed class RunnerCliApiClientTests
{
    [Fact]
    public void Constructor_RejectsNonHttpScheme()
    {
        Assert.Throws<ArgumentException>(() =>
            new RunnerApiClient(new Uri("ftp://example.com"), null));
    }

    [Fact]
    public async Task GetModels_ParsesJsonArray()
    {
        var handler = new StubHandler((req, ct) =>
        {
            Assert.Equal("/api/models", req.RequestUri!.AbsolutePath);
            return MakeJsonResponse("""{"models":["phi3","llama3:8b"]}""");
        });

        using var http = new HttpClient(handler);
        using var client = new RunnerApiClient(new Uri("http://127.0.0.1:41555"), null, http);

        var models = await client.GetModelsAsync();

        Assert.Equal(new[] { "phi3", "llama3:8b" }, models);
    }

    [Fact]
    public async Task ChatStream_YieldsStartTokensAndComplete()
    {
        var ndjson = string.Join('\n',
            """{"type":"start","model":"phi3","usedRagContext":false,"sources":[]}""",
            """{"type":"token","token":"Hello"}""",
            """{"type":"token","token":", "}""",
            """{"type":"token","token":"world."}""",
            """{"type":"complete","usedRagContext":true,"sources":["doc-a.pdf","doc-b.txt"],"responseText":"Hello, world."}""");

        var handler = new StubHandler((req, ct) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Equal("/api/chat/stream", req.RequestUri!.AbsolutePath);
            return MakeNdjsonResponse(ndjson);
        });

        using var http = new HttpClient(handler);
        using var client = new RunnerApiClient(new Uri("http://127.0.0.1:41555"), "secret", http);

        var events = new List<RunnerApiClient.StreamEvent>();
        await foreach (var evt in client.ChatStreamAsync("phi3", "hi"))
        {
            events.Add(evt);
        }

        Assert.Equal(5, events.Count);
        Assert.Equal("start", events[0].Type);
        Assert.Equal("Hello", events[1].Token);
        Assert.Equal("complete", events[4].Type);
        Assert.True(events[4].UsedRagContext);
        Assert.Equal(new[] { "doc-a.pdf", "doc-b.txt" }, events[4].Sources);
    }

    [Fact]
    public async Task ApiKey_IsSentAsBearerHeader()
    {
        string? seenAuth = null;
        var handler = new StubHandler((req, ct) =>
        {
            seenAuth = req.Headers.Authorization?.ToString();
            return MakeJsonResponse("""{"status":"ok","networkModeEnabled":true,"requireApiKey":true,"ollamaRunning":true,"timestampUtc":"2026-04-17T00:00:00Z"}""");
        });

        using var http = new HttpClient(handler);
        using var client = new RunnerApiClient(new Uri("http://127.0.0.1:41555"), "  my-key  ", http);
        await client.GetHealthAsync();

        Assert.Equal("Bearer my-key", seenAuth);
    }

    [Fact]
    public async Task NoApiKey_OmitsAuthHeader()
    {
        string? seenAuth = null;
        var handler = new StubHandler((req, ct) =>
        {
            seenAuth = req.Headers.Authorization?.ToString();
            return MakeJsonResponse("""{"status":"ok","networkModeEnabled":true,"requireApiKey":false,"ollamaRunning":true,"timestampUtc":"2026-04-17T00:00:00Z"}""");
        });

        using var http = new HttpClient(handler);
        using var client = new RunnerApiClient(new Uri("http://127.0.0.1:41555"), null, http);
        await client.GetHealthAsync();

        Assert.Null(seenAuth);
    }

    [Fact]
    public async Task ChatStream_MalformedNdjson_Throws()
    {
        var handler = new StubHandler((_, _) => MakeNdjsonResponse("{not valid json\n"));
        using var http = new HttpClient(handler);
        using var client = new RunnerApiClient(new Uri("http://127.0.0.1:41555"), null, http);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.ChatStreamAsync("phi3", "hi"))
            {
            }
        });
    }

    private static HttpResponseMessage MakeJsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage MakeNdjsonResponse(string ndjson) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request, cancellationToken));
    }
}
