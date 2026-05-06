using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FreeAiSsd.MacRunnerHost;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC7 Mac-side RAG parity tests. These exercise the Mac host's real
/// RunnerCore wiring against a temporary SSD and a deterministic fake Ollama
/// endpoint, without requiring a published osx-arm64 binary.
/// </summary>
public sealed class MacRunnerHostRagParityTests : IDisposable
{
    private readonly string _tempRoot;

    public MacRunnerHostRagParityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "freeai-mac7-rag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task ChatEndpoint_UsesActiveLibraryAndReturnsSources()
    {
        using var ollama = FakeOllamaServer.Start(keyword: "hydraulics");
        var (ssdRoot, manifest) = await SeedLibraryAsync(
            ollama.Host,
            fileName: "hydraulics.txt",
            text: "Hydraulics power the landing gear actuator and emergency brake accumulator.");

        var apiPort = GetFreePort();
        await using var host = await StartMacHostAsync(ssdRoot, ollama.Host, CreateConfig(manifest.Id, apiPort));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = await http.PostAsJsonAsync($"http://127.0.0.1:{apiPort}/api/chat", new
        {
            model = "phi3",
            prompt = "What do hydraulics power?"
        });

        Assert.True(response.IsSuccessStatusCode, $"/api/chat returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("success", response.Headers.GetValues("X-RAG-Status").Single());

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("generated answer", json.GetProperty("responseText").GetString());
        Assert.True(json.GetProperty("usedRagContext").GetBoolean());
        var sources = json.GetProperty("sources").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList();
        Assert.Contains(sources, s => s.Contains("hydraulics.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ollama.GeneratePrompts, p => p.Contains("Reference context:", StringComparison.Ordinal) &&
                                                     p.Contains("Hydraulics power", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamEndpoint_CompletesWithRagSources()
    {
        using var ollama = FakeOllamaServer.Start(keyword: "hydraulics");
        var (ssdRoot, manifest) = await SeedLibraryAsync(
            ollama.Host,
            fileName: "hydraulics-stream.txt",
            text: "Hydraulics power the canopy actuator and landing gear uplock.");

        var apiPort = GetFreePort();
        await using var host = await StartMacHostAsync(ssdRoot, ollama.Host, CreateConfig(manifest.Id, apiPort));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.PostAsJsonAsync($"http://127.0.0.1:{apiPort}/api/chat/stream", new
        {
            model = "phi3",
            prompt = "What do hydraulics power?"
        });

        Assert.True(response.IsSuccessStatusCode, $"/api/chat/stream returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadAsStringAsync();
        var events = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                return doc.RootElement.Clone();
            })
            .ToList();

        Assert.Contains(events, e => e.GetProperty("type").GetString() == "token");
        var complete = Assert.Single(events, e => e.GetProperty("type").GetString() == "complete");
        Assert.True(complete.GetProperty("usedRagContext").GetBoolean());
        var sources = complete.GetProperty("sources").EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList();
        Assert.Contains(sources, s => s.Contains("hydraulics-stream.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ChatEndpoint_WhenEmbeddingDimensionMismatches_ReturnsWarningAndFallbackAnswer()
    {
        using var ollama = FakeOllamaServer.Start(keyword: "hydraulics");
        var (ssdRoot, manifest) = await SeedLibraryAsync(
            ollama.Host,
            fileName: "hydraulics-mismatch.txt",
            text: "Hydraulics power the brake accumulator.");

        ollama.EmbeddingDimensions = 32;

        var apiPort = GetFreePort();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        await using var host = await StartMacHostAsync(ssdRoot, ollama.Host, CreateConfig(manifest.Id, apiPort), stdout, stderr);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = await http.PostAsJsonAsync($"http://127.0.0.1:{apiPort}/api/chat", new
        {
            model = "phi3",
            prompt = "What do hydraulics power?"
        });

        Assert.True(response.IsSuccessStatusCode, $"/api/chat returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal("retrieval-failed", response.Headers.GetValues("X-RAG-Status").Single());

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("generated answer", json.GetProperty("responseText").GetString());
        Assert.False(json.GetProperty("usedRagContext").GetBoolean());
        var warning = json.GetProperty("ragWarning").GetString() ?? string.Empty;
        Assert.Contains("Embedding dimension mismatch", warning);
        Assert.Contains("Embedding dimension mismatch", stdout.ToString());
    }

    private async Task<(string SsdRoot, DocumentLibraryManifest Manifest)> SeedLibraryAsync(
        string ollamaHost,
        string fileName,
        string text)
    {
        var ssdRoot = Path.Combine(_tempRoot, "ssd-" + Guid.NewGuid().ToString("N"));
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("Mac RAG test");

        var sourcePath = Path.Combine(_tempRoot, fileName);
        await File.WriteAllTextAsync(sourcePath, text);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var ingestor = new DocumentIngestor(manager, new EmbeddingClient(http), logger: null);
        await ingestor.IngestFilesAsync(manifest, new[] { sourcePath }, ollamaHost, CreateConfig(manifest.Id, networkPort: GetFreePort()));
        return (ssdRoot, manifest);
    }

    private static PortableConfig CreateConfig(string libraryId, int networkPort) => new()
    {
        ActiveDocumentLibraryId = libraryId,
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

    private static async Task<HostLifetime> StartMacHostAsync(
        string ssdRoot,
        string ollamaHost,
        PortableConfig config,
        TextWriter? stdout = null,
        TextWriter? stderr = null)
    {
        var host = new HostLifetime(
            ssdRoot,
            stdout ?? new StringWriter(),
            stderr ?? new StringWriter());
        await host.StartAsync(config, ollamaHost);
        return host;
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
        private readonly object _promptsLock = new();
        private readonly List<string> _generatePrompts = new();

        private FakeOllamaServer(WebApplication app, int port, string keyword)
        {
            _app = app;
            Port = port;
            _keyword = keyword;
        }

        public int Port { get; }
        public int EmbeddingDimensions { get; set; } = 64;
        public string Host => $"127.0.0.1:{Port}";

        public IReadOnlyList<string> GeneratePrompts
        {
            get
            {
                lock (_promptsLock)
                {
                    return _generatePrompts.ToList();
                }
            }
        }

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

            var embedding = CreateEmbedding(input);
            await context.Response.WriteAsJsonAsync(new { embeddings = new[] { embedding } });
        }

        private async Task HandleGenerateAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var prompt = doc.RootElement.TryGetProperty("prompt", out var promptEl)
                ? promptEl.GetString() ?? string.Empty
                : string.Empty;
            var stream = doc.RootElement.TryGetProperty("stream", out var streamEl) && streamEl.GetBoolean();

            lock (_promptsLock)
            {
                _generatePrompts.Add(prompt);
            }

            if (!stream)
            {
                await context.Response.WriteAsJsonAsync(new { response = "generated answer", done = true });
                return;
            }

            context.Response.ContentType = "application/x-ndjson";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { response = "generated ", done = false }) + "\n");
            await context.Response.Body.FlushAsync();
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { response = "answer", done = true }) + "\n");
            await context.Response.Body.FlushAsync();
        }

        private float[] CreateEmbedding(string input)
        {
            var embedding = new float[EmbeddingDimensions];
            if (input.Contains(_keyword, StringComparison.OrdinalIgnoreCase))
            {
                embedding[0] = 1f;
            }
            else if (EmbeddingDimensions > 1)
            {
                embedding[1] = 1f;
            }
            else
            {
                embedding[0] = 1f;
            }
            return embedding;
        }

    }
}
