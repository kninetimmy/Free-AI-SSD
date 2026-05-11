using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using FreeAiSsd.PrepApp;
using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Tests;

public class HuggingFaceCatalogServiceTests
{
    // ── Allowlist + transport contract ─────────────────────────────────

    [Fact]
    public async Task SearchAsync_RefusesNonAllowlistedUrl()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("Should never reach the network")));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client, sourceUrl: "https://example.com/api/models");

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.UrlNotAllowed, ex.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SearchAsync_RefusesNonHttpsUrl()
    {
        // Test by injecting an http:// allowlist override that
        // bypasses the allowlist (the HTTPS check fires after the
        // allowlist). We use an allowlisted-but-http URL to exercise
        // the second guard.
        using var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("Should never reach the network")));
        using var client = new HttpClient(handler);
        // sourceUrl matches the allowlist exactly above only via HTTPS;
        // an http:// URL fails the allowlist guard (UrlNotAllowed) so
        // this test pins that an http:// URL is refused before any
        // network call. The "must be HTTPS" branch is dead code until
        // a future allowlist entry uses http:// — left in place for
        // defense in depth.
        using var svc = new HuggingFaceCatalogService(client, sourceUrl: "http://huggingface.co/api/models");

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.UrlNotAllowed, ex.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SearchAsync_PropagatesCallerCancellation()
    {
        using var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        using var cts = new CancellationTokenSource();
        var task = svc.SearchAsync(new HuggingFaceSearchQuery(), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task SearchAsync_TranslatesTimeoutToTypedException()
    {
        using var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client, timeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.Timeout, ex.Reason);
    }

    [Fact]
    public async Task SearchAsync_TranslatesNetworkErrorToTypedException()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("DNS failure")));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.NetworkError, ex.Reason);
    }

    [Fact]
    public async Task SearchAsync_TranslatesServerErrorToTypedException()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.NonSuccessStatus, ex.Reason);
        Assert.Equal("500", ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_RateLimitSurfaces429StatusCode()
    {
        // C27 Stage 1: HF returns 429 when the anonymous quota burns.
        // The UI uses the status-code field to render a "wait a minute"
        // caption specifically — pin so a future refactor doesn't lose
        // the status code on the typed exception.
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.NonSuccessStatus, ex.Reason);
        Assert.Equal("429", ex.StatusCode);
    }

    [Fact]
    public async Task SearchAsync_TranslatesEmptyBodyToTypedException()
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.EmptyResponse, ex.Reason);
    }

    [Fact]
    public async Task SearchAsync_TranslatesMalformedJsonToSchemaDrift()
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not actually json", Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.SchemaDrift, ex.Reason);
    }

    // ── Request building ───────────────────────────────────────────────

    [Fact]
    public void BuildRequestUrl_IncludesGgufFilterAndSortAndLimit()
    {
        var url = HuggingFaceCatalogService.BuildRequestUrl(
            "https://huggingface.co/api/models",
            new HuggingFaceSearchQuery(Search: null, Limit: 25, Sort: "downloads"));
        Assert.Contains("filter=gguf", url);
        Assert.Contains("sort=downloads", url);
        Assert.Contains("limit=25", url);
        Assert.DoesNotContain("search=", url);
    }

    [Fact]
    public void BuildRequestUrl_UrlEncodesSearchTerm()
    {
        // "qwen 3" → "qwen%203" (space → %20). Stand-in for any
        // user-typed term that includes a space.
        var url = HuggingFaceCatalogService.BuildRequestUrl(
            "https://huggingface.co/api/models",
            new HuggingFaceSearchQuery(Search: "qwen 3"));
        Assert.Contains("search=qwen%203", url);
    }

    [Fact]
    public async Task SearchAsync_SendsUserAgentHeader()
    {
        string? capturedUserAgent = null;
        using var handler = new StubHandler((req, _) =>
        {
            capturedUserAgent = req.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        await svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(capturedUserAgent));
        Assert.Contains("Free-AI-SSD", capturedUserAgent!);
    }

    [Fact]
    public async Task SearchAsync_DoesNotSendAuthorizationHeader()
    {
        // C27 Stage 1 is anonymous read only. Token auth deferred to
        // Stage 3 alongside encrypted-config storage. Pin so a future
        // contributor doesn't accidentally route a token here without
        // wiring the encrypted-config posture.
        string? capturedAuth = null;
        using var handler = new StubHandler((req, _) =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        await svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None);
        Assert.Null(capturedAuth);
    }

    [Fact]
    public async Task SearchAsync_CachesIdenticalQuery()
    {
        // C27 Stage 1: in-memory cache scoped to the service instance.
        // Two calls with the same (search, limit, sort) hit the network
        // exactly once.
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            }));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        var q = new HuggingFaceSearchQuery(Search: "qwen");
        await svc.SearchAsync(q, CancellationToken.None);
        await svc.SearchAsync(q, CancellationToken.None);
        Assert.Equal(1, handler.CallCount);
    }

    // ── Projection ─────────────────────────────────────────────────────

    [Fact]
    public void ParseHuggingFaceResponse_ProjectsFixture()
    {
        var json = LoadFixture();
        var catalog = HuggingFaceCatalogService.ParseHuggingFaceResponse(json);
        // Fixture has 6 entries but one ("ggml-org/embedding-only-no-gguf-tag")
        // lacks the gguf tag and one ("unsloth/tiny-500M-Instruct-GGUF") has
        // an "M" suffix (sub-billion). Both should survive the projection
        // except the no-gguf-tag entry, which is filtered out.
        Assert.Equal(5, catalog.Models.Count);
        Assert.DoesNotContain(catalog.Models, m => m.Tag.Contains("embedding-only-no-gguf-tag"));
    }

    [Fact]
    public void ParseHuggingFaceResponse_TagsRowsWithHfDotCoPrefix()
    {
        var json = LoadFixture();
        var catalog = HuggingFaceCatalogService.ParseHuggingFaceResponse(json);
        var first = catalog.Models.First(m => m.Tag.Contains("Qwen3-8B-GGUF"));
        Assert.Equal("hf.co/bartowski/Qwen3-8B-GGUF", first.Tag);
    }

    [Fact]
    public void ParseHuggingFaceResponse_MapsDownloadsToPullCount()
    {
        var json = LoadFixture();
        var catalog = HuggingFaceCatalogService.ParseHuggingFaceResponse(json);
        var qwen = catalog.Models.First(m => m.Tag == "hf.co/bartowski/Qwen3-8B-GGUF");
        Assert.Equal(245321L, qwen.PullCount);
    }

    [Fact]
    public void ParseHuggingFaceResponse_MapsLastModifiedToLastUpdated()
    {
        var json = LoadFixture();
        var catalog = HuggingFaceCatalogService.ParseHuggingFaceResponse(json);
        var qwen = catalog.Models.First(m => m.Tag == "hf.co/bartowski/Qwen3-8B-GGUF");
        Assert.NotNull(qwen.LastUpdated);
        Assert.Equal(new DateTimeOffset(2026, 4, 28, 14, 22, 11, TimeSpan.Zero), qwen.LastUpdated);
    }

    [Fact]
    public void ParseHuggingFaceResponse_AllEntriesAreHuggingFaceSource()
    {
        var json = LoadFixture();
        var catalog = HuggingFaceCatalogService.ParseHuggingFaceResponse(json);
        Assert.All(catalog.Models, m => Assert.Equal(ModelSource.HuggingFace, m.Source));
    }

    [Fact]
    public void ParseHuggingFaceResponse_FiltersEntriesWithoutGgufTag()
    {
        const string json = """
            [
              {
                "id": "owner/repo",
                "downloads": 100,
                "tags": ["transformers"]
              }
            ]
            """;
        var catalog = HuggingFaceCatalogService.ParseHuggingFaceResponse(json);
        Assert.Empty(catalog.Models);
    }

    [Fact]
    public void ParseHuggingFaceResponse_FiltersMalformedIds()
    {
        const string json = """
            [
              {
                "id": "no-slash-here",
                "tags": ["gguf"]
              },
              {
                "id": "too/many/slashes",
                "tags": ["gguf"]
              },
              {
                "id": "owner/",
                "tags": ["gguf"]
              }
            ]
            """;
        var catalog = HuggingFaceCatalogService.ParseHuggingFaceResponse(json);
        Assert.Empty(catalog.Models);
    }

    // ── TryExtractParamsFromRepoId ─────────────────────────────────────

    [Theory]
    [InlineData("bartowski/Qwen3-8B-GGUF", 8.0)]
    [InlineData("lmstudio-community/Llama-3.2-7B-Instruct-GGUF", 7.0)]
    [InlineData("TheBloke/Mistral-7B-OpenOrca-GGUF", 7.0)]
    [InlineData("unsloth/Llama-3.3-70B-Instruct-bnb-4bit", 70.0)]
    [InlineData("owner/Model-1.5B-Chat", 1.5)]
    public void TryExtractParamsFromRepoId_StandaloneBillionToken(string repoId, double expected)
    {
        Assert.Equal(expected, HuggingFaceCatalogService.TryExtractParamsFromRepoId(repoId));
    }

    [Theory]
    [InlineData("owner/Tiny-500M-GGUF", 0.5)]
    [InlineData("owner/Pico-100m-test", 0.1)]
    public void TryExtractParamsFromRepoId_StandaloneMillionToken(string repoId, double expected)
    {
        Assert.Equal(expected, HuggingFaceCatalogService.TryExtractParamsFromRepoId(repoId));
    }

    [Theory]
    [InlineData("mistralai/Mixtral-8x7B", 8.0)]
    [InlineData("owner/MoE-128x17b-test", 128.0)]
    public void TryExtractParamsFromRepoId_MoeNotationReturnsLargerComponent(string repoId, double expected)
    {
        // Same posture as LiveModelCatalogService.ParseParamsBillions —
        // memory-constrained users sizing for VRAM shouldn't see MoE
        // models slip under the ≤7B cap.
        Assert.Equal(expected, HuggingFaceCatalogService.TryExtractParamsFromRepoId(repoId));
    }

    [Theory]
    [InlineData("owner/no-size-info")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("owner/Just-Words-No-Number")]
    public void TryExtractParamsFromRepoId_ReturnsNullForUnparseable(string repoId)
    {
        Assert.Null(HuggingFaceCatalogService.TryExtractParamsFromRepoId(repoId));
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string LoadFixture([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;
        var path = Path.Combine(testsDir, "Fixtures", "HuggingFace", "2026-05-11-popular-gguf.json");
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return _handler(request, cancellationToken);
        }
    }
}
