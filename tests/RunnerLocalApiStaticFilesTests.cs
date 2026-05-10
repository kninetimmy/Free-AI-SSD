using System.Net;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC6: covers the static-file middleware <see cref="RunnerLocalApiService"/>
/// wires in front of the /api/* group so X4 (when shipped) is served from the
/// same Kestrel on Windows and Mac. Two cases:
///
/// 1. With a placeholder index.html present in <c>wwwroot/chat/</c>, GET
///    /chat/index.html returns 200 and the file body. UseDefaultFiles
///    rewrites GET /chat/ to /chat/index.html so that also returns 200.
/// 2. With no SPA assets present (an injected empty wwwroot), GET /chat/
///    returns 404 cleanly — the middleware falls through and the API group
///    doesn't match, producing the framework's default 404.
/// 3. A wwwroot directory under the SSD root is ignored so removable-drive
///    contents cannot shadow the published RunnerCore assets.
/// </summary>
public sealed class RunnerLocalApiStaticFilesTests
{
    [Fact]
    public async Task ChatRoute_With_PlaceholderIndex_Serves200()
    {
        using var workdir = new TempWorkingDir();
        var wwwrootChat = Path.Combine(workdir.Path, "wwwroot", "chat");
        Directory.CreateDirectory(wwwrootChat);
        var placeholder = "<!doctype html><title>placeholder</title>";
        await File.WriteAllTextAsync(Path.Combine(wwwrootChat, "index.html"), placeholder);

        await using var fixture = await StaticFileFixture.StartAsync(
            workdir.Path,
            staticFilesRoot: Path.Combine(workdir.Path, "wwwroot"));
        using var http = new HttpClient();

        var indexResponse = await http.GetAsync($"{fixture.BaseUrl}/chat/index.html");
        Assert.Equal(HttpStatusCode.OK, indexResponse.StatusCode);
        var indexBody = await indexResponse.Content.ReadAsStringAsync();
        Assert.Contains("placeholder", indexBody);

        // UseDefaultFiles makes GET /chat/ resolve to /chat/index.html.
        var defaultResponse = await http.GetAsync($"{fixture.BaseUrl}/chat/");
        Assert.Equal(HttpStatusCode.OK, defaultResponse.StatusCode);
    }

    [Fact]
    public async Task ChatRoute_WithoutAssets_ReturnsCleanNotFound()
    {
        using var workdir = new TempWorkingDir();
        var emptyWwwroot = Path.Combine(workdir.Path, "empty-wwwroot");
        Directory.CreateDirectory(emptyWwwroot);
        // Intentionally inject an empty wwwroot. The static-file middleware falls
        // through to the framework's default 404 (the API group only matches
        // /api/*).
        await using var fixture = await StaticFileFixture.StartAsync(
            workdir.Path,
            staticFilesRoot: emptyWwwroot);
        using var http = new HttpClient();

        var response = await http.GetAsync($"{fixture.BaseUrl}/chat/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // /api/health is unauthenticated and always wired — confirms the
        // service is actually running and we're not seeing a 404 from a
        // never-started host.
        var health = await http.GetAsync($"{fixture.BaseUrl}/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task ChatRoute_DoesNotServeFilesFromSsdRootWwwroot()
    {
        using var workdir = new TempWorkingDir();
        var ssdWwwrootChat = Path.Combine(workdir.Path, "wwwroot", "chat");
        Directory.CreateDirectory(ssdWwwrootChat);
        await File.WriteAllTextAsync(Path.Combine(ssdWwwrootChat, "index.html"), "ssd-shadow");

        var publishedWwwroot = Path.Combine(workdir.Path, "published-wwwroot");
        Directory.CreateDirectory(publishedWwwroot);

        await using var fixture = await StaticFileFixture.StartAsync(
            workdir.Path,
            staticFilesRoot: publishedWwwroot);
        using var http = new HttpClient();

        var response = await http.GetAsync($"{fixture.BaseUrl}/chat/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class TempWorkingDir : IDisposable
    {
        public string Path { get; }

        public TempWorkingDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freeai-mac6-static-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private sealed class StaticFileFixture : IAsyncDisposable
    {
        private readonly RunnerLocalApiService _service;

        private StaticFileFixture(RunnerLocalApiService service) => _service = service;

        public string BaseUrl => _service.CurrentBaseUrl!;

        public static async Task<StaticFileFixture> StartAsync(string ssdRoot, string? staticFilesRoot = null)
        {
            var service = new RunnerLocalApiService(
                new StubChatService(),
                new NoOpSpeechToTextService(),
                new NoOpTtsProvider(),
                logger: null,
                ssdRoot: ssdRoot,
                staticFilesRoot: staticFilesRoot);

            var config = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkBindAddress = "127.0.0.1",
                NetworkPort = GetFreePort(),
                NetworkRequireApiKey = false,
                NetworkApiKey = string.Empty,
            };

            await service.StartAsync(config, "127.0.0.1:11434");
            return new StaticFileFixture(service);
        }

        public async ValueTask DisposeAsync() => await _service.DisposeAsync();

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class StubChatService : IChatService
    {
        public event Action<string>? LogMessage;
        public event Action<int>? FirstTokenPending;
        public Task<ChatResult> SendPromptAsync(string model, string userPrompt, string host, PortableConfig config)
            => Task.FromResult<ChatResult>(new ChatResult.Success(new ChatResponse("ok", null, false)));
        public Task<ChatResult> SendPromptStreamingAsync(string model, string userPrompt, string host, PortableConfig config, Func<string, Task> onToken, CancellationToken cancellationToken = default)
            => Task.FromResult<ChatResult>(new ChatResult.Success(new ChatResponse("ok", null, false)));
    }
}
