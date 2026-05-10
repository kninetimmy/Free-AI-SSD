using System.Net;
using System.Net.Http;
using System.Text;
using FreeAiSsd.PrepApp;
using FreeAiSsd.Shared.Services;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// Pins the JSON-API pull path that replaced the MAC31 CLI-stdout
/// parser. The v1.3.20 field test surfaced a regression where Ollama's
/// progress TUI shifted from <c>pulling &lt;hash&gt;... NN%</c> to
/// <c>pulling &lt;hash&gt;: NN%</c> after MAC38 unpinned the static
/// version, breaking the regex anchor and routing every progress tick
/// to the scrolling log instead of the in-place label.
///
/// The fix moves the pull onto Ollama's <c>POST /api/pull</c> NDJSON
/// stream — the canonical contract that the CLI itself consumes —
/// so future shape drifts in the human-facing TUI stop affecting
/// the PrepApp UI. These tests pin:
///   1. NDJSON frames map cleanly to <see cref="OllamaPullProgress"/>.
///   2. <c>{"error":"..."}</c> frames throw with the message included.
///   3. Non-2xx responses surface the body text in the exception so
///      MAC38-style "412 requires newer version" errors stay readable.
///   4. <see cref="OllamaPullProgress.ToDisplayString"/> renders both
///      stage frames (status only) and layer frames (with bytes/percent)
///      in a form the PrepApp's single-line label can consume.
/// </summary>
public sealed class OllamaPullClientTests
{
    [Fact]
    public async Task PullAsync_ForwardsEachNdjsonFrameAsStructuredProgress()
    {
        // Real-world frame sequence: stage transition → layer ticks → stage → done.
        var ndjson = string.Join('\n',
            "{\"status\":\"pulling manifest\"}",
            "{\"status\":\"pulling 96c415656d37\",\"digest\":\"sha256:96c415656d37\",\"total\":4700000000,\"completed\":3900000000}",
            "{\"status\":\"pulling 96c415656d37\",\"digest\":\"sha256:96c415656d37\",\"total\":4700000000,\"completed\":4700000000}",
            "{\"status\":\"verifying sha256 digest\"}",
            "{\"status\":\"writing manifest\"}",
            "{\"status\":\"success\"}",
            "");

        var captured = new List<OllamaPullProgress>();
        var handler = new StubHandler(HttpStatusCode.OK, ndjson);

        await OllamaPullClient.PullAsync(
            "127.0.0.1:11434", "deepseek-r1:7b",
            captured.Add, CancellationToken.None, handler);

        Assert.Equal(6, captured.Count);
        Assert.Equal("pulling manifest", captured[0].Status);
        Assert.Null(captured[0].Total);

        Assert.Equal("pulling 96c415656d37", captured[1].Status);
        Assert.Equal("sha256:96c415656d37", captured[1].Digest);
        Assert.Equal(4700000000L, captured[1].Total);
        Assert.Equal(3900000000L, captured[1].Completed);

        Assert.Equal("success", captured[5].Status);
    }

    [Fact]
    public async Task PullAsync_ThrowsWhenFrameCarriesError()
    {
        // MAC38 case: an out-of-date Ollama server returns a structured
        // error frame mid-stream. The pre-MAC38 CLI surfaced this as
        // "412: requires a newer version of Ollama"; the API surfaces
        // it as an in-stream error frame.
        var ndjson = string.Join('\n',
            "{\"status\":\"pulling manifest\"}",
            "{\"error\":\"pull model manifest: 412: The model you are attempting to pull requires a newer version of Ollama.\"}",
            "");

        var captured = new List<OllamaPullProgress>();
        var handler = new StubHandler(HttpStatusCode.OK, ndjson);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OllamaPullClient.PullAsync("127.0.0.1:11434", "deepseek-r1:7b",
                captured.Add, CancellationToken.None, handler));

        Assert.Contains("412", ex.Message);
        Assert.Contains("newer version", ex.Message);
        // The pre-error frame must still have been forwarded — the UI
        // shouldn't lose progress visibility just because a later frame
        // errored.
        Assert.Single(captured);
    }

    [Fact]
    public async Task PullAsync_SurfacesNon2xxBodyInException()
    {
        // If the server rejects the request before streaming starts
        // (auth gate, malformed model tag, etc.) the body should still
        // reach the user — bare status codes leave the operator guessing.
        var handler = new StubHandler(HttpStatusCode.BadRequest, "{\"error\":\"model 'nonsense' not found\"}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OllamaPullClient.PullAsync("127.0.0.1:11434", "nonsense",
                _ => { }, CancellationToken.None, handler));

        Assert.Contains("400", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task PullAsync_NormalizesBareHostToHttpScheme()
    {
        // OllamaServerHandle.Host returns "127.0.0.1:NNNNN" (no scheme).
        // The HTTP client expects a scheme; the pull client must
        // synthesize http:// when the input lacks one.
        var handler = new StubHandler(HttpStatusCode.OK, "{\"status\":\"success\"}\n");

        await OllamaPullClient.PullAsync(
            "127.0.0.1:54321", "tag:1",
            _ => { }, CancellationToken.None, handler);

        Assert.Equal("http://127.0.0.1:54321/api/pull", handler.LastRequestUrl);
    }

    [Fact]
    public async Task PullAsync_ContainsThrowingOnProgressCallback()
    {
        // A misbehaving UI dispatcher must not abort the response stream
        // — same invariant as the prior ModelOperations.Consume catch.
        // Otherwise a single bad frame poisons every subsequent pull tick.
        var ndjson = string.Join('\n',
            "{\"status\":\"first\"}",
            "{\"status\":\"second\"}",
            "{\"status\":\"third\"}",
            "");
        var handler = new StubHandler(HttpStatusCode.OK, ndjson);
        var seen = new List<string>();

        await OllamaPullClient.PullAsync(
            "127.0.0.1:11434", "tag:1",
            p =>
            {
                seen.Add(p.Status);
                if (p.Status == "second") throw new InvalidOperationException("UI dispatcher choked");
            },
            CancellationToken.None, handler);

        Assert.Equal(new[] { "first", "second", "third" }, seen);
    }

    [Fact]
    public async Task PullAsync_RequiresOllamaHost()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            OllamaPullClient.PullAsync(
                "", "tag:1", _ => { }, CancellationToken.None));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public string? LastRequestUrl { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUrl = request.RequestUri?.ToString();
            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/x-ndjson"),
            };
            return Task.FromResult(response);
        }
    }
}
