using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC8 library-management API tests. Exercise the new /api/library/* surface
/// at the HTTP layer with a real <see cref="DocumentLibraryManager"/>,
/// <see cref="DocumentOperationsService"/>, and <see cref="RunnerLocalApiService"/>
/// against a fake Ollama embed endpoint. These tests run on Windows CI; the
/// same endpoints are also covered end-to-end via the Mac sidecar in
/// <see cref="MacRunnerHostLibraryTests"/>.
/// </summary>
public sealed class RunnerLocalApiLibraryTests : IDisposable
{
    private readonly string _tempRoot;

    public RunnerLocalApiLibraryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "freeai-mac8-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetLibrary_OnEmptySsd_ReturnsEmptyList()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);

        var response = await fixture.Http.GetAsync($"{fixture.BaseUrl}/api/library");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("libraries").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeLibraryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeLibrary").ValueKind);
    }

    [Fact]
    public async Task CreateLibrary_AssignsIdAndSetsActive()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);

        var response = await fixture.Http.PostAsJsonAsync(
            $"{fixture.BaseUrl}/api/library",
            new { name = "Avionics" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var newId = body.GetProperty("activeLibraryId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(newId));
        Assert.Equal("Avionics", body.GetProperty("library").GetProperty("name").GetString());
        Assert.Equal(0, body.GetProperty("library").GetProperty("fileCount").GetInt32());

        // GET reflects active state
        var listResp = await fixture.Http.GetAsync($"{fixture.BaseUrl}/api/library");
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(newId, list.GetProperty("activeLibraryId").GetString());
        Assert.Equal(newId, list.GetProperty("activeLibrary").GetProperty("id").GetString());

        // In-memory PortableConfig was mutated (ChatService and other endpoints
        // honor it without a config save — Mac side persists via Swift)
        Assert.Equal(newId, fixture.Config.ActiveDocumentLibraryId);
    }

    [Fact]
    public async Task CreateLibrary_DuplicateName_Returns409()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);

        var first = await fixture.Http.PostAsJsonAsync($"{fixture.BaseUrl}/api/library", new { name = "Dupe" });
        first.EnsureSuccessStatusCode();

        var second = await fixture.Http.PostAsJsonAsync($"{fixture.BaseUrl}/api/library", new { name = "Dupe" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task CreateLibrary_BlankName_Returns400()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);

        var response = await fixture.Http.PostAsJsonAsync($"{fixture.BaseUrl}/api/library", new { name = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SetActive_UnknownId_Returns404()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);

        var response = await fixture.Http.PutAsJsonAsync(
            $"{fixture.BaseUrl}/api/library/active",
            new { libraryId = "lib-does-not-exist" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetActive_NullClearsActive()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);
        await fixture.Http.PostAsJsonAsync($"{fixture.BaseUrl}/api/library", new { name = "ToBeCleared" });

        var clear = await fixture.Http.PutAsJsonAsync(
            $"{fixture.BaseUrl}/api/library/active",
            new { libraryId = (string?)null });

        clear.EnsureSuccessStatusCode();
        var body = await clear.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("activeLibraryId").ValueKind);
        Assert.Null(fixture.Config.ActiveDocumentLibraryId);
    }

    [Fact]
    public async Task UploadFiles_TxtIngested_StreamsProgressAndCompletes()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);

        var libraryId = await CreateLibraryAsync(fixture, "Hydraulics");

        using var content = new MultipartFormDataContent();
        var bytes = Encoding.UTF8.GetBytes("Hydraulics power the landing gear actuator.");
        var filePart = new ByteArrayContent(bytes);
        filePart.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        content.Add(filePart, name: "files", fileName: "hydraulics.txt");

        var response = await fixture.Http.PostAsync(
            $"{fixture.BaseUrl}/api/library/{libraryId}/files",
            content);

        Assert.True(response.IsSuccessStatusCode, $"Upload returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        var (events, rawBody) = await ReadNdjsonWithBodyAsync(response);
        var types = events.Select(TypeOrMissing).ToList();
        Assert.Contains("start", types);
        Assert.Contains("progress", types);
        Assert.True(types.Count(t => t == "complete") == 1, $"Expected exactly one 'complete' frame; got types={string.Join(",", types)} body={rawBody}");
        var complete = events.Single(e => TypeOrMissing(e) == "complete");

        var libraryDetail = complete.GetProperty("library");
        Assert.Equal(1, libraryDetail.GetProperty("fileCount").GetInt32());
        var files = libraryDetail.GetProperty("files").EnumerateArray().Select(x => x.GetProperty("fileName").GetString()).ToList();
        Assert.Contains("hydraulics.txt", files);
    }

    [Fact]
    public async Task UploadFiles_UnsupportedExtension_RejectedAndCompletes()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);

        var libraryId = await CreateLibraryAsync(fixture, "Bad");

        using var content = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(Encoding.UTF8.GetBytes("binary nope"));
        filePart.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        content.Add(filePart, name: "files", fileName: "evil.exe");

        var response = await fixture.Http.PostAsync(
            $"{fixture.BaseUrl}/api/library/{libraryId}/files",
            content);

        Assert.True(response.IsSuccessStatusCode);
        var events = await ReadNdjsonAsync(response);
        var rejected = Assert.Single(events, e => e.GetProperty("type").GetString() == "file-rejected");
        Assert.Equal("evil.exe", rejected.GetProperty("fileName").GetString());
        Assert.Contains("Unsupported", rejected.GetProperty("reason").GetString() ?? string.Empty);
        // Complete should still fire — the library just got 0 new files
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "complete");
    }

    [Fact]
    public async Task UploadFiles_OversizedFile_RejectedWithSizeMessage()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot, maxDocSizeMB: 1);

        var libraryId = await CreateLibraryAsync(fixture, "TooBig");

        using var content = new MultipartFormDataContent();
        // 2 MB of text => oversized for a 1 MB cap
        var bytes = Encoding.UTF8.GetBytes(new string('a', 2 * 1024 * 1024));
        var filePart = new ByteArrayContent(bytes);
        filePart.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
        content.Add(filePart, name: "files", fileName: "huge.txt");

        var response = await fixture.Http.PostAsync(
            $"{fixture.BaseUrl}/api/library/{libraryId}/files",
            content);

        var events = await ReadNdjsonAsync(response);
        var rejected = Assert.Single(events, e => e.GetProperty("type").GetString() == "file-rejected");
        Assert.Contains("max document size", rejected.GetProperty("reason").GetString() ?? string.Empty);
    }

    [Fact]
    public async Task UploadFiles_UnknownLibrary_Returns404()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("hello"))
        {
            Headers = { ContentType = MediaTypeHeaderValue.Parse("text/plain") }
        }, name: "files", fileName: "hello.txt");

        var response = await fixture.Http.PostAsync(
            $"{fixture.BaseUrl}/api/library/lib-does-not-exist/files",
            content);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AddWatchedFolder_NonexistentPath_Returns400()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);
        var libraryId = await CreateLibraryAsync(fixture, "WatchTest");

        var response = await fixture.Http.PostAsJsonAsync(
            $"{fixture.BaseUrl}/api/library/{libraryId}/folders",
            new { path = Path.Combine(_tempRoot, "no-such-folder") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddWatchedFolder_ExistingPath_AddsAndReturnsManifest()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);
        var libraryId = await CreateLibraryAsync(fixture, "Watcher");

        var folder = Path.Combine(_tempRoot, "watch-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        var response = await fixture.Http.PostAsJsonAsync(
            $"{fixture.BaseUrl}/api/library/{libraryId}/folders",
            new { path = folder });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("added").GetBoolean());
        var watched = body.GetProperty("watchedFolders").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains(folder, watched);
    }

    [Fact]
    public async Task DeleteFile_RemovesEntryAndReturnsRefreshedManifest()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);
        var libraryId = await CreateLibraryAsync(fixture, "Trash");

        // Ingest one file via the API
        using (var content = new MultipartFormDataContent())
        {
            var fp = new ByteArrayContent(Encoding.UTF8.GetBytes("delete me"));
            fp.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
            content.Add(fp, name: "files", fileName: "deleteme.txt");
            var ingest = await fixture.Http.PostAsync($"{fixture.BaseUrl}/api/library/{libraryId}/files", content);
            Assert.True(ingest.IsSuccessStatusCode,
                $"Upload returned {ingest.StatusCode}: {await ingest.Content.ReadAsStringAsync()}");
        }

        // Find the stored relative path
        var manager = new DocumentLibraryManager(fixture.SsdRoot);
        var manifest = manager.LoadManifest(libraryId);
        var stored = Assert.Single(manifest.Files);

        var del = await fixture.Http.DeleteAsync($"{fixture.BaseUrl}/api/library/{libraryId}/files/{Uri.EscapeDataString(stored.StoredRelativePath)}");
        del.EnsureSuccessStatusCode();
        var body = await del.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("library").GetProperty("fileCount").GetInt32());
    }

    [Fact]
    public async Task DeleteFile_TraversalAttempt_Returns400()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);
        var libraryId = await CreateLibraryAsync(fixture, "Traversal");

        // Path that escapes the files/ directory
        var del = await fixture.Http.DeleteAsync(
            $"{fixture.BaseUrl}/api/library/{libraryId}/files/{Uri.EscapeDataString("../../etc/passwd")}");

        Assert.Equal(HttpStatusCode.BadRequest, del.StatusCode);
    }

    [Fact]
    public async Task Sweep_WatchedFolder_IngestsSupportedFilesFromFolder()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);
        var libraryId = await CreateLibraryAsync(fixture, "Sweep");

        var folder = Path.Combine(_tempRoot, "sweep-folder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "swept.txt"), "Hydraulics article body.");
        await File.WriteAllTextAsync(Path.Combine(folder, "ignored.exe"), "binary"); // unsupported

        // Add watched folder
        await fixture.Http.PostAsJsonAsync($"{fixture.BaseUrl}/api/library/{libraryId}/folders", new { path = folder });

        var response = await fixture.Http.PostAsync($"{fixture.BaseUrl}/api/library/{libraryId}/sweep",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        Assert.True(response.IsSuccessStatusCode);
        var (events, rawBody) = await ReadNdjsonWithBodyAsync(response);
        var types = events.Select(TypeOrMissing).ToList();
        Assert.True(types.Count(t => t == "complete") == 1, $"Expected exactly one 'complete' frame; got types={string.Join(",", types)} body={rawBody}");
        var complete = events.Single(e => TypeOrMissing(e) == "complete");
        var detail = complete.GetProperty("library");
        Assert.Equal(1, detail.GetProperty("fileCount").GetInt32());
        var fileNames = detail.GetProperty("files").EnumerateArray().Select(x => x.GetProperty("fileName").GetString()).ToList();
        Assert.Contains("swept.txt", fileNames);
    }

    [Fact]
    public async Task Rebuild_RecreatesIndexFromStoredCopies()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot);
        var libraryId = await CreateLibraryAsync(fixture, "Rebuild");

        using (var content = new MultipartFormDataContent())
        {
            var fp = new ByteArrayContent(Encoding.UTF8.GetBytes("Hydraulics body"));
            fp.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
            content.Add(fp, name: "files", fileName: "hydraulics.txt");
            await fixture.Http.PostAsync($"{fixture.BaseUrl}/api/library/{libraryId}/files", content);
        }

        var response = await fixture.Http.PostAsync($"{fixture.BaseUrl}/api/library/{libraryId}/rebuild",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        var (events, rawBody) = await ReadNdjsonWithBodyAsync(response);
        var types = events.Select(TypeOrMissing).ToList();
        Assert.True(types.Contains("complete"), $"Expected a 'complete' frame; got types={string.Join(",", types)} body={rawBody}");
    }

    [Fact]
    public async Task LibraryEndpoints_RequireApiKey_When_NetworkRequireApiKey()
    {
        await using var fixture = await Fixture.StartAsync(_tempRoot, requireApiKey: true);

        // Without auth header
        var unauth = await fixture.Http.GetAsync($"{fixture.BaseUrl}/api/library");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // With auth header
        using var authedReq = new HttpRequestMessage(HttpMethod.Get, $"{fixture.BaseUrl}/api/library");
        authedReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Fixture.ApiKey);
        var authed = await fixture.Http.SendAsync(authedReq);
        authed.EnsureSuccessStatusCode();
    }

    private static async Task<string> CreateLibraryAsync(Fixture fixture, string name)
    {
        var response = await fixture.Http.PostAsJsonAsync($"{fixture.BaseUrl}/api/library", new { name });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("activeLibraryId").GetString()!;
    }

    private static async Task<(List<JsonElement> Events, string RawBody)> ReadNdjsonWithBodyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var events = new List<JsonElement>();
        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            using var doc = JsonDocument.Parse(trimmed);
            events.Add(doc.RootElement.Clone());
        }
        return (events, body);
    }

    private static async Task<List<JsonElement>> ReadNdjsonAsync(HttpResponseMessage response)
    {
        var (events, _) = await ReadNdjsonWithBodyAsync(response);
        return events;
    }

    /// <summary>
    /// Diagnostic helper: returns the event's "type" string, or "&lt;missing&gt;"
    /// if the event lacks the property. Used in test predicates so a missing
    /// type field doesn't crash xUnit's iteration with KeyNotFoundException.
    /// </summary>
    private static string TypeOrMissing(JsonElement e)
    {
        return e.TryGetProperty("type", out var t) ? (t.GetString() ?? "<null>") : "<missing>";
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string ApiKey = "test-api-key";

        private readonly RunnerLocalApiService _service;
        private readonly FakeOllamaServer _ollama;

        private Fixture(RunnerLocalApiService service, string ssdRoot, PortableConfig config, FakeOllamaServer ollama)
        {
            _service = service;
            SsdRoot = ssdRoot;
            Config = config;
            _ollama = ollama;
            Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        public string SsdRoot { get; }
        public PortableConfig Config { get; }
        public HttpClient Http { get; }
        public string BaseUrl => _service.CurrentBaseUrl!;

        public static async Task<Fixture> StartAsync(
            string tempRoot,
            int maxDocSizeMB = 50,
            bool requireApiKey = false)
        {
            var ssdRoot = Path.Combine(tempRoot, "ssd-" + Guid.NewGuid().ToString("N"));
            SsdLayout.EnsureStructure(ssdRoot);

            var ollama = FakeOllamaServer.Start();

            var libraryManager = new DocumentLibraryManager(ssdRoot);
            var ingestor = new DocumentIngestor(libraryManager, new EmbeddingClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }), logger: null);
            var configStore = new ConfigStore(logger: null);
            var docOps = new DocumentOperationsService(libraryManager, ingestor, configStore);

            var chat = new StubChatService();
            var stt = new NoOpSpeechToTextService();
            var ttsProvider = new TtsProvider();

            var service = new RunnerLocalApiService(
                chat,
                stt,
                ttsProvider,
                logger: null,
                ssdRoot: ssdRoot,
                staticFilesRoot: null,
                docOps: docOps,
                libraryManager: libraryManager);

            var config = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkBindAddress = "127.0.0.1",
                NetworkPort = GetFreePort(),
                NetworkRequireApiKey = requireApiKey,
                NetworkApiKey = ApiKey,
                MaxDocumentSizeMB = maxDocSizeMB,
                EmbeddingModelName = "nomic-embed-text",
                ChunkSize = 800,
                ChunkOverlap = 0,
                MaxEmbeddingConcurrency = 1,
                RetrievalTopK = 5,
                MinimumSimilarityThreshold = 0.1,
                Models = new List<ModelConfigEntry>
                {
                    new() { Name = "phi3", Status = ModelInstallStatus.Installed }
                }
            };

            await service.StartAsync(config, ollama.Host);
            return new Fixture(service, ssdRoot, config, ollama);
        }

        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await _service.DisposeAsync();
            _ollama.Dispose();
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class StubChatService : IChatService
    {
        public event Action<string>? LogMessage;
        public event Action<int>? FirstTokenPending;
        public Task<ChatResult> SendPromptAsync(string model, string userPrompt, string host, PortableConfig config)
            => Task.FromResult<ChatResult>(new ChatResult.Success(new ChatResponse("stub", null, false)));
        public Task<ChatResult> SendPromptStreamingAsync(string model, string userPrompt, string host, PortableConfig config, Func<string, Task> onToken, CancellationToken cancellationToken = default)
            => Task.FromResult<ChatResult>(new ChatResult.Success(new ChatResponse("stub", null, false)));
    }

    private sealed class FakeOllamaServer : IDisposable
    {
        private readonly WebApplication _app;

        private FakeOllamaServer(WebApplication app, int port)
        {
            _app = app;
            Port = port;
        }

        public int Port { get; }
        public string Host => $"127.0.0.1:{Port}";

        public static FakeOllamaServer Start()
        {
            var port = GetFreePort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
            var app = builder.Build();

            app.MapPost("/api/embed", async (HttpContext ctx) =>
            {
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(body);
                var input = doc.RootElement.TryGetProperty("input", out var inputEl)
                    ? inputEl.GetString() ?? string.Empty
                    : string.Empty;
                var embedding = new float[64];
                embedding[Math.Abs(input.GetHashCode()) % embedding.Length] = 1f;
                await ctx.Response.WriteAsJsonAsync(new { embeddings = new[] { embedding } });
            });

            app.StartAsync().GetAwaiter().GetResult();
            return new FakeOllamaServer(app, port);
        }

        public void Dispose()
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
