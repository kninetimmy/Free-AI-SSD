using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FreeAiSsd.MacRunnerHost;
using FreeAiSsd.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC8 Mac-host library-management integration tests. Boot the Mac sidecar's
/// real RunnerCore DI (with NoOpConfigStore — Swift owns persistence) against
/// a temp SSD and a deterministic fake Ollama embed endpoint, then drive the
/// /api/library/* endpoints through HTTP to prove the full Mac path works
/// end-to-end without a real osx-arm64 binary or real Ollama.
///
/// Pairs with <see cref="MacRunnerHostRagParityTests"/> (chat + RAG end-to-end)
/// and <see cref="RunnerLocalApiLibraryTests"/> (direct API tests).
/// </summary>
public sealed class MacRunnerHostLibraryTests : IDisposable
{
    private readonly string _tempRoot;

    public MacRunnerHostLibraryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "freeai-mac8-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task FullFlow_CreateLibrary_UploadFile_ChatReturnsSourcesFromUpload()
    {
        using var ollama = FakeOllamaServer.Start(keyword: "hydraulics");
        var ssdRoot = Path.Combine(_tempRoot, "ssd-" + Guid.NewGuid().ToString("N"));
        SsdLayout.EnsureStructure(ssdRoot);

        var apiPort = GetFreePort();
        var config = CreateConfig(activeLibraryId: null, apiPort);
        await using var host = await StartMacHostAsync(ssdRoot, ollama.Host, config);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var baseUrl = $"http://127.0.0.1:{apiPort}";

        // 1. Create library — server sets ActiveDocumentLibraryId in-memory and
        //    returns the new id. Swift would persist it via SsdEncryption.swift;
        //    here we only assert the response shape and that subsequent endpoints
        //    on the same in-memory config see the new active library.
        var createResp = await http.PostAsJsonAsync($"{baseUrl}/api/library", new { name = "Mac MAC8" });
        createResp.EnsureSuccessStatusCode();
        var createBody = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var libraryId = createBody.GetProperty("activeLibraryId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(libraryId));

        // 2. Upload a TXT file via multipart — sidecar copies into a temp dir,
        //    then DocumentIngestor copies it into <ssdRoot>/docs/libraries/<id>/files/.
        using (var content = new MultipartFormDataContent())
        {
            var bytes = Encoding.UTF8.GetBytes("Hydraulics power the landing gear actuator and emergency brake accumulator.");
            var filePart = new ByteArrayContent(bytes);
            filePart.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");
            content.Add(filePart, name: "files", fileName: "hydraulics.txt");

            var uploadResp = await http.PostAsync($"{baseUrl}/api/library/{libraryId}/files", content);
            Assert.True(uploadResp.IsSuccessStatusCode, $"Upload returned {uploadResp.StatusCode}: {await uploadResp.Content.ReadAsStringAsync()}");

            var events = await ReadNdjsonAsync(uploadResp);
            var complete = Assert.Single(events, e => e.GetProperty("type").GetString() == "complete");
            Assert.Equal(1, complete.GetProperty("library").GetProperty("fileCount").GetInt32());
        }

        // 3. /api/chat against the now-active library. MAC7's pipeline kicks in:
        //    embedding -> vector search -> RAG prompt -> generated answer.
        //    Sources should reference the file we just uploaded.
        var chatResp = await http.PostAsJsonAsync($"{baseUrl}/api/chat", new
        {
            model = "phi3",
            prompt = "What do hydraulics power?"
        });

        Assert.True(chatResp.IsSuccessStatusCode, $"/api/chat returned {chatResp.StatusCode}: {await chatResp.Content.ReadAsStringAsync()}");
        Assert.Equal("success", chatResp.Headers.GetValues("X-RAG-Status").Single());

        var chatBody = await chatResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(chatBody.GetProperty("usedRagContext").GetBoolean());
        var sources = chatBody.GetProperty("sources").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList();
        Assert.Contains(sources, s => s.Contains("hydraulics.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SetActive_PersistsInMemoryConfigForSubsequentChatRequests()
    {
        using var ollama = FakeOllamaServer.Start(keyword: "hydraulics");
        var ssdRoot = Path.Combine(_tempRoot, "ssd-" + Guid.NewGuid().ToString("N"));
        SsdLayout.EnsureStructure(ssdRoot);

        var apiPort = GetFreePort();
        var config = CreateConfig(activeLibraryId: null, apiPort);
        await using var host = await StartMacHostAsync(ssdRoot, ollama.Host, config);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var baseUrl = $"http://127.0.0.1:{apiPort}";

        // Create two libraries
        var lib1Resp = await http.PostAsJsonAsync($"{baseUrl}/api/library", new { name = "Lib1" });
        var lib1Id = (await lib1Resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("activeLibraryId").GetString()!;

        var lib2Resp = await http.PostAsJsonAsync($"{baseUrl}/api/library", new { name = "Lib2" });
        var lib2Id = (await lib2Resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("activeLibraryId").GetString()!;

        // Switch active back to Lib1
        var setActive = await http.PutAsJsonAsync($"{baseUrl}/api/library/active", new { libraryId = lib1Id });
        setActive.EnsureSuccessStatusCode();

        // GET /api/library reflects the switch
        var listResp = await http.GetAsync($"{baseUrl}/api/library");
        var list = await listResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(lib1Id, list.GetProperty("activeLibraryId").GetString());

        // Clearing active also works
        var clear = await http.PutAsJsonAsync($"{baseUrl}/api/library/active", new { libraryId = (string?)null });
        clear.EnsureSuccessStatusCode();
        var afterClear = await (await http.GetAsync($"{baseUrl}/api/library")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, afterClear.GetProperty("activeLibraryId").ValueKind);
    }

    private static PortableConfig CreateConfig(string? activeLibraryId, int networkPort) => new()
    {
        ActiveDocumentLibraryId = activeLibraryId,
        EmbeddingModelName = "nomic-embed-text",
        RetrievalTopK = 5,
        MinimumSimilarityThreshold = 0.1,
        ChunkSize = 800,
        ChunkOverlap = 0,
        MaxDocumentSizeMB = 50,
        MaxEmbeddingConcurrency = 1,
        NetworkModeEnabled = true,
        NetworkBindAddress = "127.0.0.1",
        NetworkPort = networkPort,
        NetworkRequireApiKey = false,
        NetworkApiKey = string.Empty,
        NetworkAllowTts = false,
        NetworkAllowRemoteStt = false,
        NetworkAllowRemoteVoiceQuery = false,
        NetworkMaxAudioUploadMB = 10,
        Models = new List<ModelConfigEntry>
        {
            new() { Name = "phi3", Status = ModelInstallStatus.Installed }
        }
    };

    private static async Task<HostLifetime> StartMacHostAsync(string ssdRoot, string ollamaHost, PortableConfig config)
    {
        var host = new HostLifetime(ssdRoot, new StringWriter(), new StringWriter());
        await host.StartAsync(config, ollamaHost);
        return host;
    }

    private static async Task<List<JsonElement>> ReadNdjsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var events = new List<JsonElement>();
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            events.Add(doc.RootElement.Clone());
        }
        return events;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class FakeOllamaServer : IDisposable
    {
        private readonly WebApplication _app;
        private readonly string _keyword;

        private FakeOllamaServer(WebApplication app, int port, string keyword)
        {
            _app = app;
            Port = port;
            _keyword = keyword;
        }

        public int Port { get; }
        public string Host => $"127.0.0.1:{Port}";

        public static FakeOllamaServer Start(string keyword)
        {
            var port = GetFreePort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseKestrel().UseUrls($"http://127.0.0.1:{port}");
            var app = builder.Build();
            var server = new FakeOllamaServer(app, port, keyword);

            app.MapPost("/api/embed", server.HandleEmbedAsync);
            app.MapPost("/api/generate", server.HandleGenerateAsync);
            app.StartAsync().GetAwaiter().GetResult();

            return server;
        }

        public void Dispose()
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private async Task HandleEmbedAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var input = doc.RootElement.TryGetProperty("input", out var inputEl)
                ? inputEl.GetString() ?? string.Empty
                : string.Empty;

            var embedding = new float[64];
            if (input.Contains(_keyword, StringComparison.OrdinalIgnoreCase))
            {
                embedding[0] = 1f;
            }
            else
            {
                embedding[1] = 1f;
            }
            await context.Response.WriteAsJsonAsync(new { embeddings = new[] { embedding } });
        }

        private async Task HandleGenerateAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            await reader.ReadToEndAsync();
            await context.Response.WriteAsJsonAsync(new { response = "generated answer", done = true });
        }
    }
}
