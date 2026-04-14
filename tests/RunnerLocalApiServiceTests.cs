using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class RunnerLocalApiServiceTests
{
    [Fact]
    public async Task HealthEndpoint_DoesNotRequireApiKey()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: true, allowTts: true);
        using var http = new HttpClient();

        var response = await http.GetAsync($"{fixture.BaseUrl}/api/health");
        response.EnsureSuccessStatusCode();

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task ApiKeyEnforcement_BlocksChatWithoutKey()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: true, allowTts: true);
        using var http = new HttpClient();

        var unauthorized = await http.PostAsJsonAsync($"{fixture.BaseUrl}/api/chat", new { model = "phi3", prompt = "hello" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/api/chat")
        {
            Content = JsonContent.Create(new { model = "phi3", prompt = "hello" })
        };
        req.Headers.Add("Authorization", "Bearer secret-key");

        var authorized = await http.SendAsync(req);
        authorized.EnsureSuccessStatusCode();

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task ChatEndpoint_ReturnsChatResponse()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true);
        fixture.Chat.Response = new ChatResponse("roger", new List<string> { "manual.pdf p.1" }, true);

        using var http = new HttpClient();
        var response = await http.PostAsJsonAsync($"{fixture.BaseUrl}/api/chat", new { model = "phi3", prompt = "status" });

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("roger", json.GetProperty("responseText").GetString());
        Assert.True(json.GetProperty("usedRagContext").GetBoolean());

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task TtsEndpoints_RespectAllowTtsFlag()
    {
        var blocked = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: false);
        using var http = new HttpClient();

        var forbidden = await http.PostAsJsonAsync($"{blocked.BaseUrl}/api/tts/speak", new { text = "hello" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(0, blocked.Tts.SpeakCallCount);
        await blocked.DisposeAsync();

        var allowed = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true);
        var ok = await http.PostAsJsonAsync($"{allowed.BaseUrl}/api/tts/speak", new { text = "hello" });
        ok.EnsureSuccessStatusCode();

        var stop = await http.PostAsync($"{allowed.BaseUrl}/api/tts/stop", null);
        stop.EnsureSuccessStatusCode();

        Assert.Equal(1, allowed.Tts.SpeakCallCount);
        Assert.Equal(1, allowed.Tts.StopCallCount);

        await allowed.DisposeAsync();
    }

    private sealed class RunnerLocalApiFixture : IAsyncDisposable
    {
        private readonly RunnerLocalApiService _service;

        private RunnerLocalApiFixture(RunnerLocalApiService service, FakeChatService chat, FakeTtsService tts)
        {
            _service = service;
            Chat = chat;
            Tts = tts;
        }

        public FakeChatService Chat { get; }
        public FakeTtsService Tts { get; }
        public string BaseUrl => _service.CurrentBaseUrl!;

        public static async Task<RunnerLocalApiFixture> StartAsync(bool requireApiKey, bool allowTts)
        {
            var chat = new FakeChatService();
            var tts = new FakeTtsService();
            var service = new RunnerLocalApiService(chat, () => tts, logger: null);
            var config = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkBindAddress = "127.0.0.1",
                NetworkPort = GetFreePort(),
                NetworkRequireApiKey = requireApiKey,
                NetworkApiKey = "secret-key",
                NetworkAllowTts = allowTts,
                Models = new List<ModelConfigEntry>
                {
                    new() { Name = "phi3", Status = ModelInstallStatus.Installed }
                }
            };

            await service.StartAsync(config, "127.0.0.1:11434");
            return new RunnerLocalApiFixture(service, chat, tts);
        }

        public async ValueTask DisposeAsync()
        {
            await _service.DisposeAsync();
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class FakeChatService : IChatService
    {
        public event Action<string>? LogMessage;

        public ChatResponse Response { get; set; } = new("ok", null, false);

        public Task<ChatResponse> SendPromptAsync(string model, string userPrompt, string host, PortableConfig config)
            => Task.FromResult(Response);

        public Task<ChatResponse> SendPromptStreamingAsync(string model, string userPrompt, string host, PortableConfig config, Action<string> onToken, CancellationToken cancellationToken = default)
        {
            onToken("tok");
            return Task.FromResult(Response with { ResponseText = "tok" });
        }
    }

    private sealed class FakeTtsService : ITextToSpeechService
    {
        public int SpeakCallCount { get; private set; }
        public int StopCallCount { get; private set; }

        public event Action<string>? LogMessage;
        public bool IsSpeaking => false;

        public void Speak(string text)
        {
            SpeakCallCount++;
        }

        public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            SpeakCallCount++;
            return Task.CompletedTask;
        }

        public void Stop()
        {
            StopCallCount++;
        }

        public void SetVoice(string voiceName) { }
        public void SetRate(int rate) { }
        public void SetVolume(int volume) { }
        public IReadOnlyList<string> GetAvailableVoices() => Array.Empty<string>();
        public void Dispose() { }
    }
}
