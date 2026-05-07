using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using FreeAiSsd.PrepApp.Services;

namespace FreeAiSsd.Tests;

public class LiveModelCatalogServiceTests
{
    [Fact]
    public async Task FetchAsync_RefusesNonAllowlistedUrl()
    {
        using var handler = new StubHandler((_, _) => throw new InvalidOperationException("Should never reach the network"));
        using var client = new HttpClient(handler);
        using var svc = new LiveModelCatalogService(client, sourceUrl: "https://example.com/models");

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(() => svc.FetchAsync(CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.UrlNotAllowed, ex.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FetchAsync_PropagatesCallerCancellation()
    {
        using var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var svc = new LiveModelCatalogService(client);

        using var cts = new CancellationTokenSource();
        var task = svc.FetchAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task FetchAsync_TranslatesTimeoutToTypedException()
    {
        using var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var svc = new LiveModelCatalogService(client, timeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(() => svc.FetchAsync(CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.Timeout, ex.Reason);
    }

    [Fact]
    public async Task FetchAsync_TranslatesNetworkErrorToTypedException()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("dns failure"));
        using var client = new HttpClient(handler);
        using var svc = new LiveModelCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(() => svc.FetchAsync(CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.NetworkError, ex.Reason);
    }

    [Fact]
    public async Task FetchAsync_TranslatesNon2xxToTypedException()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var client = new HttpClient(handler);
        using var svc = new LiveModelCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(() => svc.FetchAsync(CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.NonSuccessStatus, ex.Reason);
        Assert.Equal("500", ex.StatusCode);
    }

    [Fact]
    public async Task FetchAsync_EmptyBodyTriggersEmptyResponse()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/html"),
            }));
        using var client = new HttpClient(handler);
        using var svc = new LiveModelCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(() => svc.FetchAsync(CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.EmptyResponse, ex.Reason);
    }

    [Fact]
    public async Task FetchAsync_NoModelCardsTriggersSchemaDrift()
    {
        const string html = "<!doctype html><html><body><h1>nothing here</h1></body></html>";
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            }));
        using var client = new HttpClient(handler);
        using var svc = new LiveModelCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(() => svc.FetchAsync(CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.SchemaDrift, ex.Reason);
    }

    [Fact]
    public async Task FetchAsync_ParsesCapturedFixtureSuccessfully()
    {
        var html = LoadFixture();
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            }));
        using var client = new HttpClient(handler);
        using var svc = new LiveModelCatalogService(client);

        var result = await svc.FetchAsync(CancellationToken.None);

        // The captured 2026-05-07 fixture has 229 model cards; after expanding
        // size variants and dropping pure-embedding models, we expect a few
        // hundred entries. Assert a reasonable lower bound rather than an
        // exact count so a future fixture refresh doesn't churn this test.
        Assert.True(result.Catalog.Models.Count >= 100,
            $"Expected at least 100 entries, got {result.Catalog.Models.Count}");
        Assert.Equal("https://ollama.com/library", result.SourceUrl);
        Assert.Equal(1, result.Catalog.SchemaVersion);

        // Spot-check a known card: llama3.1 has tools capability and 8b/70b/405b sizes.
        var llama31 = result.Catalog.Models.Where(m => m.Tag.StartsWith("llama3.1:", StringComparison.Ordinal)).ToList();
        Assert.Contains(llama31, m => m.Tag == "llama3.1:8b" && m.Params == "8B" && m.SizeTier == "Medium");
        Assert.Contains(llama31, m => m.Tag == "llama3.1:70b" && m.Params == "70B" && m.SizeTier == "Large");
        Assert.Contains(llama31, m => m.Tag == "llama3.1:405b" && m.Params == "405B" && m.SizeTier == "Large");
        Assert.All(llama31, m => Assert.Contains("tools", m.UseCases, StringComparer.OrdinalIgnoreCase));
        Assert.All(llama31, m => Assert.False(string.IsNullOrWhiteSpace(m.Description)));

        // Pure-embedding models (e.g., nomic-embed-text, mxbai-embed-large)
        // should be filtered — chat picker only.
        Assert.DoesNotContain(result.Catalog.Models,
            m => m.Tag.StartsWith("nomic-embed-text", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1b", "Small")]
    [InlineData("2b", "Small")]
    [InlineData("3b", "Medium")]
    [InlineData("7b", "Medium")]
    [InlineData("13b", "Medium")]
    [InlineData("14b", "Large")]
    [InlineData("70b", "Large")]
    [InlineData("405b", "Large")]
    [InlineData("1.5b", "Small")]
    [InlineData("335m", "Small")]
    [InlineData("", "Medium")]
    [InlineData("garbage", "Medium")]
    public void ClassifySize_MapsToExpectedTier(string size, string expectedTier)
    {
        Assert.Equal(expectedTier, LiveModelCatalogService.ClassifySize(size));
    }

    [Fact]
    public void ParseOllamaLibraryHtml_HandlesMissingDescriptionGracefully()
    {
        const string minimal = """
            <ul>
              <li x-test-model class="x">
                <a href="/library/foo">
                  <div x-test-model-title title="foo"></div>
                  <span x-test-size>7b</span>
                </a>
              </li>
            </ul>
            """;
        var catalog = LiveModelCatalogService.ParseOllamaLibraryHtml(minimal);
        Assert.Single(catalog.Models);
        Assert.Equal("foo:7b", catalog.Models[0].Tag);
        Assert.Equal(string.Empty, catalog.Models[0].Description);
    }

    [Fact]
    public void ParseOllamaLibraryHtml_DropsPureEmbeddingCards()
    {
        const string html = """
            <ul>
              <li x-test-model>
                <a href="/library/embeds-only">
                  <p class="max-w-lg break-words text-neutral-800 text-md">An embeddings model.</p>
                  <span x-test-capability>embedding</span>
                  <span x-test-size>335m</span>
                </a>
              </li>
              <li x-test-model>
                <a href="/library/chat-model">
                  <p class="max-w-lg break-words text-neutral-800 text-md">A chat model.</p>
                  <span x-test-capability>tools</span>
                  <span x-test-size>7b</span>
                </a>
              </li>
            </ul>
            """;
        var catalog = LiveModelCatalogService.ParseOllamaLibraryHtml(html);
        Assert.Single(catalog.Models);
        Assert.Equal("chat-model:7b", catalog.Models[0].Tag);
    }

    [Fact]
    public void ParseOllamaLibraryHtml_DecodesHtmlEntitiesInDescriptions()
    {
        const string html = """
            <ul>
              <li x-test-model>
                <a href="/library/foo">
                  <p class="max-w-lg break-words text-neutral-800 text-md">Meta&#39;s &quot;new&quot; model &amp; friends</p>
                  <span x-test-size>7b</span>
                </a>
              </li>
            </ul>
            """;
        var catalog = LiveModelCatalogService.ParseOllamaLibraryHtml(html);
        Assert.Single(catalog.Models);
        Assert.Equal("Meta's \"new\" model & friends", catalog.Models[0].Description);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static string LoadFixture([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;
        var path = Path.Combine(testsDir, "Fixtures", "OllamaLibrary", "2026-05-07-snapshot.html");
        return File.ReadAllText(path);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public int CallCount { get; private set; }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
            : this((req, ct) => Task.FromResult(handler(req, ct)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return _handler(request, cancellationToken);
        }
    }
}
