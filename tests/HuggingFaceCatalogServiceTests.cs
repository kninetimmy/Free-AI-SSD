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

    // ── C27 Stage 2: FetchSiblingsAsync transport contract ─────────────

    [Fact]
    public async Task FetchSiblingsAsync_RefusesNonAllowlistedUrl()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("Should never reach the network")));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client, modelDetailsBaseUrl: "https://evil.example.com/api/models/");

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.FetchSiblingsAsync("owner/repo", CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.UrlNotAllowed, ex.Reason);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FetchSiblingsAsync_RefusesMalformedRepoId()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("Should never reach the network")));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.FetchSiblingsAsync("no-slash-here", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.FetchSiblingsAsync("too/many/slashes", CancellationToken.None));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FetchSiblingsAsync_BuildsExpectedUrl()
    {
        string? capturedUrl = null;
        using var handler = new StubHandler((req, _) =>
        {
            capturedUrl = req.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"owner/repo","siblings":[]}""", Encoding.UTF8, "application/json")
            });
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        await svc.FetchSiblingsAsync("Qwen/Qwen3-8B-GGUF", CancellationToken.None);
        Assert.Equal("https://huggingface.co/api/models/Qwen/Qwen3-8B-GGUF", capturedUrl);
    }

    [Fact]
    public async Task FetchSiblingsAsync_SurfacesAuthStatusForGatedRepo()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.FetchSiblingsAsync("meta-llama/Llama-3.3-70B-Instruct-GGUF", CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.NonSuccessStatus, ex.Reason);
        Assert.Equal("401", ex.StatusCode);
    }

    [Fact]
    public async Task FetchSiblingsAsync_RateLimitSurfaces429()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.FetchSiblingsAsync("owner/repo", CancellationToken.None));
        Assert.Equal("429", ex.StatusCode);
    }

    [Fact]
    public async Task FetchSiblingsAsync_CachesByRepoId()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"owner/repo","siblings":[]}""", Encoding.UTF8, "application/json")
            }));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        await svc.FetchSiblingsAsync("owner/repo", CancellationToken.None);
        await svc.FetchSiblingsAsync("owner/repo", CancellationToken.None);
        Assert.Equal(1, handler.CallCount);
    }

    // ── C27 Stage 2: ParseModelDetailsResponse + flag parsing ──────────

    [Fact]
    public void ParseModelDetailsResponse_PrefersLfsSizeOverTopLevelSize()
    {
        const string json = """
            {
              "id": "owner/repo",
              "siblings": [
                {"rfilename": "model-Q4_K_M.gguf", "size": 100, "lfs": {"size": 4900000000}},
                {"rfilename": "README.md", "size": 1024}
              ]
            }
            """;
        var details = HuggingFaceCatalogService.ParseModelDetailsResponse("owner/repo", json);
        var gguf = details.Siblings.Single(s => s.Filename.EndsWith(".gguf"));
        Assert.Equal(4_900_000_000L, gguf.SizeBytes);
        var readme = details.Siblings.Single(s => s.Filename == "README.md");
        Assert.Equal(1024L, readme.SizeBytes);
    }

    [Fact]
    public void ParseModelDetailsResponse_GatedString_RecognizedAsGated()
    {
        // HF's gated field is polymorphic: "auto"/"manual"/false/true.
        const string jsonAuto = """{"id":"owner/repo","gated":"auto","siblings":[]}""";
        const string jsonManual = """{"id":"owner/repo","gated":"manual","siblings":[]}""";
        const string jsonFalse = """{"id":"owner/repo","gated":"false","siblings":[]}""";
        const string jsonBool = """{"id":"owner/repo","gated":true,"siblings":[]}""";

        Assert.True(HuggingFaceCatalogService.ParseModelDetailsResponse("owner/repo", jsonAuto).Gated);
        Assert.True(HuggingFaceCatalogService.ParseModelDetailsResponse("owner/repo", jsonManual).Gated);
        Assert.False(HuggingFaceCatalogService.ParseModelDetailsResponse("owner/repo", jsonFalse).Gated);
        Assert.True(HuggingFaceCatalogService.ParseModelDetailsResponse("owner/repo", jsonBool).Gated);
    }

    [Fact]
    public void ParseModelDetailsResponse_PrivateFlagDefaultsFalse()
    {
        const string json = """{"id":"owner/repo","siblings":[]}""";
        var details = HuggingFaceCatalogService.ParseModelDetailsResponse("owner/repo", json);
        Assert.False(details.Private);
        Assert.False(details.Gated);
    }

    // ── C27 Stage 2: PickSizingFile heuristic ──────────────────────────

    [Fact]
    public void PickSizingFile_PrefersQ4_K_M_WhenPresent()
    {
        var siblings = new List<HuggingFaceSiblingFile>
        {
            new("model-Q2_K.gguf", 2_500_000_000L),
            new("model-Q4_K_M.gguf", 4_900_000_000L),
            new("model-Q8_0.gguf", 8_500_000_000L),
            new("README.md", 1024L),
        };
        var pick = HuggingFaceCatalogService.PickSizingFile(siblings);
        Assert.NotNull(pick);
        Assert.Equal("model-Q4_K_M.gguf", pick!.PrimaryFilename);
        Assert.Equal(4_900_000_000L, pick.TotalBytes);
        Assert.Equal(1, pick.PartCount);
    }

    [Fact]
    public void PickSizingFile_FallsBackToSmallestGguf_WhenNoQ4_K_M()
    {
        var siblings = new List<HuggingFaceSiblingFile>
        {
            new("model-Q5_K_M.gguf", 5_500_000_000L),
            new("model-Q8_0.gguf", 8_500_000_000L),
            new("model-Q2_K.gguf", 2_500_000_000L),
        };
        var pick = HuggingFaceCatalogService.PickSizingFile(siblings);
        Assert.NotNull(pick);
        Assert.Equal("model-Q2_K.gguf", pick!.PrimaryFilename);
        Assert.Equal(2_500_000_000L, pick.TotalBytes);
    }

    [Fact]
    public void PickSizingFile_ReturnsNull_WhenNoGguf()
    {
        var siblings = new List<HuggingFaceSiblingFile>
        {
            new("README.md", 1024L),
            new("config.json", 512L),
        };
        Assert.Null(HuggingFaceCatalogService.PickSizingFile(siblings));
    }

    [Fact]
    public void PickSizingFile_SumsMultiPartGgufFiles()
    {
        // Real-world example: Llama-3.1-70B-Instruct-Q4_K_M split into
        // 3 parts. Sum must reflect the total disk cost, not just part 1.
        var siblings = new List<HuggingFaceSiblingFile>
        {
            new("Llama-3.1-70B-Q4_K_M-00001-of-00003.gguf", 14_000_000_000L),
            new("Llama-3.1-70B-Q4_K_M-00002-of-00003.gguf", 14_000_000_000L),
            new("Llama-3.1-70B-Q4_K_M-00003-of-00003.gguf", 14_000_000_000L),
            new("README.md", 1024L),
        };
        var pick = HuggingFaceCatalogService.PickSizingFile(siblings);
        Assert.NotNull(pick);
        Assert.Equal(3, pick!.PartCount);
        Assert.Equal(42_000_000_000L, pick.TotalBytes);
        Assert.Contains("-00001-of-00003", pick.PrimaryFilename);
    }

    [Fact]
    public void PickSizingFile_HandlesNullSizesGracefully()
    {
        var siblings = new List<HuggingFaceSiblingFile>
        {
            new("model-Q4_K_M.gguf", null),
        };
        var pick = HuggingFaceCatalogService.PickSizingFile(siblings);
        Assert.NotNull(pick);
        Assert.Equal(0L, pick!.TotalBytes);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static string LoadFixture([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;
        var path = Path.Combine(testsDir, "Fixtures", "HuggingFace", "2026-05-11-popular-gguf.json");
        return File.ReadAllText(path);
    }

    // ── C27 Stage 3: token auth ────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_WithAuthToken_AddsBearerHeader()
    {
        string? observedAuth = null;
        using var handler = new StubHandler((req, _) =>
        {
            observedAuth = req.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        svc.UpdateAuthToken("hf_test_abc123");
        await svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None);

        Assert.Equal("Bearer hf_test_abc123", observedAuth);
    }

    [Fact]
    public async Task FetchSiblingsAsync_WithAuthToken_AddsBearerHeader()
    {
        string? observedAuth = null;
        using var handler = new StubHandler((req, _) =>
        {
            observedAuth = req.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"o/r\",\"siblings\":[]}", Encoding.UTF8, "application/json")
            });
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        svc.UpdateAuthToken("  hf_token_xyz  ");
        await svc.FetchSiblingsAsync("owner/repo", CancellationToken.None);

        Assert.Equal("Bearer hf_token_xyz", observedAuth);
    }

    [Fact]
    public async Task SearchAsync_WithoutToken_OmitsAuthHeader()
    {
        string? observedAuth = "<unset>";
        using var handler = new StubHandler((req, _) =>
        {
            observedAuth = req.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        await svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None);

        Assert.Null(observedAuth);
    }

    [Fact]
    public void UpdateAuthToken_NormalizesEmptyToNull()
    {
        using var svc = new HuggingFaceCatalogService();
        svc.UpdateAuthToken("hf_xyz");
        Assert.Equal("hf_xyz", svc.AuthToken);
        svc.UpdateAuthToken("   ");
        Assert.Null(svc.AuthToken);
    }

    [Fact]
    public async Task UpdateAuthToken_DropsCachedSiblings()
    {
        var calls = 0;
        using var handler = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"o/r\",\"siblings\":[]}", Encoding.UTF8, "application/json")
            });
        });
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        // First call populates cache.
        await svc.FetchSiblingsAsync("owner/repo", CancellationToken.None);
        // Second call hits the cache.
        await svc.FetchSiblingsAsync("owner/repo", CancellationToken.None);
        Assert.Equal(1, calls);

        // Token install drops the cache; next fetch hits the network again.
        svc.UpdateAuthToken("hf_token");
        await svc.FetchSiblingsAsync("owner/repo", CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task SearchAsync_With401_SurfacesNonSuccessStatus()
    {
        using var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client = new HttpClient(handler);
        using var svc = new HuggingFaceCatalogService(client);

        var ex = await Assert.ThrowsAsync<LiveCatalogFetchException>(
            () => svc.SearchAsync(new HuggingFaceSearchQuery(), CancellationToken.None));
        Assert.Equal(LiveCatalogFetchReason.NonSuccessStatus, ex.Reason);
        Assert.Equal("401", ex.StatusCode);
    }

    // ── C27 Stage 4: per-quant projection ──────────────────────────────

    [Fact]
    public void ProjectQuantChildren_ProducesOneRowPerDistinctQuant()
    {
        var siblings = new List<HuggingFaceSiblingFile>
        {
            new("README.md", null),
            new("Qwen3-8B-Q4_K_M.gguf", 4_500_000_000),
            new("Qwen3-8B-Q5_K_M.gguf", 5_500_000_000),
            new("Qwen3-8B-Q8_0.gguf", 8_700_000_000),
        };

        var children = HuggingFaceQuantProjector.Project("Qwen/Qwen3-8B-GGUF", siblings);

        Assert.Equal(3, children.Count);
        // Sort order: Q4 → Q5 → Q8 (smallest → largest by digit).
        Assert.Equal("Q4_K_M", children[0].QuantLabel);
        Assert.Equal("Q5_K_M", children[1].QuantLabel);
        Assert.Equal("Q8_0", children[2].QuantLabel);
        Assert.All(children, c => Assert.Equal("Qwen/Qwen3-8B-GGUF", c.ParentRepoId));
        Assert.All(children, c => Assert.Equal(ModelSource.HuggingFace, c.Source));
        Assert.All(children, c => Assert.False(c.IsExpandable));
        Assert.Equal("hf.co/Qwen/Qwen3-8B-GGUF:Q4_K_M", children[0].Tag);
        Assert.Equal(4_500_000_000L, children[0].QuantSizeBytes);
    }

    [Fact]
    public void ProjectQuantChildren_SumsMultiPartSeries()
    {
        var siblings = new List<HuggingFaceSiblingFile>
        {
            new("Qwen3-70B-Q4_K_M-00001-of-00003.gguf", 10_000_000_000),
            new("Qwen3-70B-Q4_K_M-00002-of-00003.gguf", 10_000_000_000),
            new("Qwen3-70B-Q4_K_M-00003-of-00003.gguf", 10_000_000_000),
        };

        var children = HuggingFaceQuantProjector.Project("Qwen/Qwen3-70B-GGUF", siblings);

        Assert.Single(children);
        var only = children[0];
        Assert.Equal("Q4_K_M", only.QuantLabel);
        Assert.Equal(30_000_000_000L, only.QuantSizeBytes);
        Assert.Contains("3-part split", only.BestAt);
    }

    [Fact]
    public void ProjectQuantChildren_SkipsNonGgufAndUnlabeled()
    {
        var siblings = new List<HuggingFaceSiblingFile>
        {
            new("README.md", null),
            new("config.json", null),
            new("model-no-quant-label.gguf", 1_000_000),
            new("Qwen3-8B-Q4_K_M.gguf", 5_000_000_000),
        };

        var children = HuggingFaceQuantProjector.Project("Qwen/Qwen3-8B-GGUF", siblings);

        Assert.Single(children);
        Assert.Equal("Q4_K_M", children[0].QuantLabel);
    }

    [Fact]
    public void ExtractQuantLabel_HandlesCommonPatterns()
    {
        Assert.Equal("Q4_K_M", HuggingFaceQuantProjector.ExtractQuantLabel("Qwen3-8B-Q4_K_M.gguf"));
        Assert.Equal("Q8_0", HuggingFaceQuantProjector.ExtractQuantLabel("Llama-3.1-8B.Q8_0.gguf"));
        Assert.Equal("F16", HuggingFaceQuantProjector.ExtractQuantLabel("Mistral-7B.F16.gguf"));
        Assert.Equal("BF16", HuggingFaceQuantProjector.ExtractQuantLabel("Phi-3-mini-BF16.gguf"));
        Assert.Equal("IQ2_XXS", HuggingFaceQuantProjector.ExtractQuantLabel("Qwen3-30B-IQ2_XXS.gguf"));
        Assert.Null(HuggingFaceQuantProjector.ExtractQuantLabel("README.md"));
        Assert.Null(HuggingFaceQuantProjector.ExtractQuantLabel("model.gguf"));
    }

    [Fact]
    public void QuantSortOrder_OrdersSmallToLarge()
    {
        var labels = new[] { "Q8_0", "Q4_K_M", "F16", "Q5_K_M", "IQ2_XXS", "BF16", "F32" };
        var ordered = labels
            .OrderBy(l => HuggingFaceQuantProjector.QuantSortOrder(l), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(new[] { "IQ2_XXS", "Q4_K_M", "Q5_K_M", "Q8_0", "BF16", "F16", "F32" }, ordered);
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
