using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class RunnerLocalApiServiceTests
{
    private static readonly TimeSpan CiFriendlyTimeout = TimeSpan.FromSeconds(10);

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
    public async Task ApiKeyEnforcement_BlocksRemoteSttWithoutKey()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: true, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true);
        using var http = new HttpClient();

        var unauthorized = await http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", CreateWavUploadContent());
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/api/stt/transcribe")
        {
            Content = CreateWavUploadContent()
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
    public async Task ChatStream_EmitsNdjsonTokensInOrder()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true);
        fixture.Chat.StreamingTokens = new List<string> { "alpha", "bravo", "charlie" };
        fixture.Chat.Response = new ChatResponse("alphabravocharlie", new List<string> { "src-1" }, true);
        using var http = new HttpClient();

        using var response = await http.PostAsJsonAsync($"{fixture.BaseUrl}/api/chat/stream", new { model = "phi3", prompt = "status" });
        response.EnsureSuccessStatusCode();
        var lines = await ReadNdjsonLinesAsync(response);
        Assert.True(lines.Count >= 5);

        var events = lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
        Assert.Equal("start", events[0].GetProperty("type").GetString());
        Assert.Equal("token", events[1].GetProperty("type").GetString());
        Assert.Equal("alpha", events[1].GetProperty("token").GetString());
        Assert.Equal("bravo", events[2].GetProperty("token").GetString());
        Assert.Equal("charlie", events[3].GetProperty("token").GetString());
        Assert.Equal("complete", events[^1].GetProperty("type").GetString());
        Assert.Equal("alphabravocharlie", events[^1].GetProperty("responseText").GetString());

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task ChatStream_AwaitsAsyncTokenCallbackPath()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true);
        fixture.Chat.StreamingTokens = new List<string> { "tok" };
        fixture.Chat.BlockTokenCallbacks = true;
        fixture.Chat.Response = new ChatResponse("tok", null, false);
        using var http = new HttpClient();

        var requestTask = http.PostAsJsonAsync($"{fixture.BaseUrl}/api/chat/stream", new { model = "phi3", prompt = "status" });
        await fixture.Chat.WaitForTokenCallbackAttemptAsync(CiFriendlyTimeout);
        Assert.False(requestTask.IsCompleted);

        fixture.Chat.ReleaseTokenCallbacks();
        using var response = await requestTask.WaitAsync(CiFriendlyTimeout);
        response.EnsureSuccessStatusCode();
        var lines = await ReadNdjsonLinesAsync(response);
        Assert.Contains(lines, line => line.Contains("\"type\":\"token\"", StringComparison.Ordinal));

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task ChatStream_ClientCancellation_PropagatesToChatService()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true);
        fixture.Chat.StreamingTokens = new List<string> { "tok" };
        fixture.Chat.StreamUntilCancelled = true;
        using var http = new HttpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        using var response = await http.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"{fixture.BaseUrl}/api/chat/stream")
            {
                Content = JsonContent.Create(new { model = "phi3", prompt = "status" })
            },
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var firstLine = await reader.ReadLineAsync(cts.Token);
        Assert.NotNull(firstLine);

        await stream.DisposeAsync();
        response.Dispose();
        await fixture.Chat.WaitForCancellationAsync(CiFriendlyTimeout);
        Assert.True(fixture.Chat.StreamingCancellationObserved);

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

    [Fact]
    public async Task RemoteStt_RejectsInvalidAndOversizedUploads()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true, maxUploadMb: 1);
        using var http = new HttpClient();

        var wrongType = await http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", JsonContent.Create(new { hello = "world" }));
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

        using var empty = new MultipartFormDataContent();
        var missing = await http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", empty);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using var oversizedContent = new MultipartFormDataContent();
        var oversizedBytes = new byte[2 * 1024 * 1024];
        oversizedContent.Add(new ByteArrayContent(oversizedBytes), "audio", "big.raw");
        oversizedContent.Add(new StringContent("pcm16le"), "format");
        var oversized = await http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", oversizedContent);
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task MalformedMultipartUpload_ReturnsBadRequest_ForSttAndVoiceQuery()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true);
        using var http = new HttpClient();

        var sttResponse = await http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", CreateMalformedMultipartContent());
        Assert.Equal(HttpStatusCode.BadRequest, sttResponse.StatusCode);

        var voiceResponse = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", CreateMalformedMultipartContent());
        Assert.Equal(HttpStatusCode.BadRequest, voiceResponse.StatusCode);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task RemoteStt_ReturnsTranscription()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true);
        fixture.Stt.TranscriptionToReturn = "request startup checklist";
        using var http = new HttpClient();

        var response = await http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", CreateWavUploadContent());
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("request startup checklist", json.GetProperty("transcription").GetString());

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task RemoteStt_LazyInitializes_WhenNotPreinitialized()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true);
        fixture.Stt.SetModelLoaded(false);
        using var http = new HttpClient();

        var response = await http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", CreateWavUploadContent());
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, fixture.Stt.InitializeCallCount);
        Assert.Equal(1, fixture.Stt.TranscribeCallCount);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task RemoteStt_Returns503_WhenInitializationFails()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true);
        fixture.Stt.SetModelLoaded(false);
        fixture.Stt.FailInitialization = true;
        using var http = new HttpClient();

        var response = await http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", CreateWavUploadContent());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, fixture.Stt.InitializeCallCount);
        Assert.Equal(0, fixture.Stt.TranscribeCallCount);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task RemoteStt_ConcurrentRequests_AreSerialized()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true);
        fixture.Stt.DelayTranscriptionMs = 150;
        using var http = new HttpClient();

        var first = http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", CreateWavUploadContent());
        var second = http.PostAsync($"{fixture.BaseUrl}/api/stt/transcribe", CreateWavUploadContent());
        await Task.WhenAll(first, second);

        Assert.Equal(HttpStatusCode.OK, first.Result.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.Result.StatusCode);
        Assert.Equal(2, fixture.Stt.TranscribeCallCount);
        Assert.Equal(1, fixture.Stt.MaxConcurrentTranscribe);
        await fixture.DisposeAsync();
    }


    [Fact]
    public async Task VoiceQuery_ResponseShape_ContainsCompanionContractFields()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: true);
        fixture.Stt.TranscriptionToReturn = "checklist";
        fixture.Chat.Response = new ChatResponse("complete", new List<string> { "ref" }, true);
        using var http = new HttpClient();

        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", CreateWavUploadContent(model: "phi3", autoSendToChat: true, speakResponse: true));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(json.TryGetProperty("transcription", out _));
        Assert.True(json.TryGetProperty("responseText", out _));
        Assert.True(json.TryGetProperty("sources", out _));
        Assert.True(json.TryGetProperty("ttsTriggeredOnHost", out _));

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task VoiceQuery_NonWhitelistedModel_ReturnsExistingErrorShape()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: true);
        fixture.Chat.EnforceConfigModelAllowList = true;
        using var http = new HttpClient();

        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", CreateWavUploadContent(model: "unknown-model", autoSendToChat: true, speakResponse: false));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("not installed in config", json.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task VoiceQuery_AutoSendDisabled_ReturnsTranscriptionOnly()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: false);
        fixture.Stt.TranscriptionToReturn = "hold short line up";
        using var http = new HttpClient();

        var content = CreateWavUploadContent();
        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("hold short line up", json.GetProperty("transcription").GetString());
        Assert.True(json.GetProperty("responseText").ValueKind is JsonValueKind.Null);
        Assert.Equal(0, fixture.Chat.SendPromptCallCount);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task VoiceQuery_AutoSendEnabled_CallsChat_AndCanTriggerHostTts()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: true);
        fixture.Stt.TranscriptionToReturn = "what is bingo fuel";
        fixture.Chat.Response = new ChatResponse("Bingo fuel is your return minimum.", new List<string> { "ops-manual.pdf p.3" }, true);

        using var http = new HttpClient();
        var content = CreateWavUploadContent(model: "phi3", autoSendToChat: true, speakResponse: true);
        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("what is bingo fuel", json.GetProperty("transcription").GetString());
        Assert.Equal("Bingo fuel is your return minimum.", json.GetProperty("responseText").GetString());
        Assert.True(json.GetProperty("ttsTriggeredOnHost").GetBoolean());
        Assert.Equal(1, fixture.Chat.SendPromptCallCount);
        Assert.Equal(1, fixture.Tts.SpeakCallCount);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task VoiceQuery_HostTts_DoesNotBlockHttpResponse()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: true);
        fixture.Stt.TranscriptionToReturn = "check weather";
        fixture.Chat.Response = new ChatResponse("Weather is clear.", null, false);
        fixture.Tts.BlockSpeakUntilReleased = true;
        using var http = new HttpClient();

        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", CreateWavUploadContent(model: "phi3", autoSendToChat: true, speakResponse: true));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("ttsTriggeredOnHost").GetBoolean());
        await fixture.Tts.WaitForSpeakCallAsync(TimeSpan.FromSeconds(2));
        Assert.False(fixture.Tts.SpeakCompletedTask.IsCompleted);
        Assert.Equal(1, fixture.Tts.SpeakCallCount);

        fixture.Tts.ReleaseSpeakCompletion();
        await fixture.Tts.SpeakCompletedTask.WaitAsync(TimeSpan.FromSeconds(2));

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task VoiceQuery_DoesNotTriggerTts_WhenDisabledByConfig()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: false, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: true);
        fixture.Stt.TranscriptionToReturn = "confirm approach lights";
        fixture.Chat.Response = new ChatResponse("Approach lights confirmed.", null, false);
        using var http = new HttpClient();

        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", CreateWavUploadContent(model: "phi3", autoSendToChat: true, speakResponse: true));
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(json.GetProperty("ttsTriggeredOnHost").GetBoolean());
        Assert.Equal(0, fixture.Tts.SpeakCallCount);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task VoiceQuery_ReturnAudio_IncludesBase64Wav_AndSkipsHostPlayback()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: true);
        fixture.Stt.TranscriptionToReturn = "ready for taxi";
        fixture.Chat.Response = new ChatResponse("Cleared to taxi.", null, false);
        using var http = new HttpClient();

        var content = CreateWavUploadContent(model: "phi3", autoSendToChat: true, speakResponse: true, returnAudio: true);
        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("audio/wav", json.GetProperty("audioMime").GetString());
        var base64 = json.GetProperty("audioBase64").GetString();
        Assert.False(string.IsNullOrWhiteSpace(base64));
        var bytes = Convert.FromBase64String(base64!);
        Assert.True(bytes.Length > 0);
        Assert.False(json.GetProperty("ttsTriggeredOnHost").GetBoolean());
        Assert.Equal(0, fixture.Tts.SpeakCallCount);
        Assert.Equal(1, fixture.Tts.SynthesizeCallCount);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task VoiceQuery_ReturnAudioFalse_PreservesHostPlayback_AndOmitsAudio()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: true);
        fixture.Stt.TranscriptionToReturn = "status report";
        fixture.Chat.Response = new ChatResponse("All nominal.", null, false);
        using var http = new HttpClient();

        var content = CreateWavUploadContent(model: "phi3", autoSendToChat: true, speakResponse: true, returnAudio: false);
        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("audioBase64").ValueKind is JsonValueKind.Null);
        Assert.True(json.GetProperty("audioMime").ValueKind is JsonValueKind.Null);
        Assert.True(json.GetProperty("ttsTriggeredOnHost").GetBoolean());
        Assert.Equal(1, fixture.Tts.SpeakCallCount);
        Assert.Equal(0, fixture.Tts.SynthesizeCallCount);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task VoiceQuery_ReturnAudio_WithTtsDisabled_OmitsAudio_AndDoesNotTriggerHost()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: false, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: true);
        fixture.Stt.TranscriptionToReturn = "set altimeter";
        fixture.Chat.Response = new ChatResponse("Altimeter set 29.92.", null, false);
        using var http = new HttpClient();

        var content = CreateWavUploadContent(model: "phi3", autoSendToChat: true, speakResponse: true, returnAudio: true);
        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("set altimeter", json.GetProperty("transcription").GetString());
        Assert.Equal("Altimeter set 29.92.", json.GetProperty("responseText").GetString());
        Assert.True(json.GetProperty("audioBase64").ValueKind is JsonValueKind.Null);
        Assert.True(json.GetProperty("audioMime").ValueKind is JsonValueKind.Null);
        Assert.False(json.GetProperty("ttsTriggeredOnHost").GetBoolean());
        Assert.Equal(0, fixture.Tts.SpeakCallCount);
        Assert.Equal(0, fixture.Tts.SynthesizeCallCount);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task VoiceQuery_AutoSendRequiresModel()
    {
        var fixture = await RunnerLocalApiFixture.StartAsync(requireApiKey: false, allowTts: true, allowRemoteStt: true, allowVoiceQuery: true, voiceAutoSendToChat: true);
        using var http = new HttpClient();

        var response = await http.PostAsync($"{fixture.BaseUrl}/api/voice/query", CreateWavUploadContent(model: null, autoSendToChat: true, speakResponse: false));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await fixture.DisposeAsync();
    }

    private sealed class RunnerLocalApiFixture : IAsyncDisposable
    {
        private readonly RunnerLocalApiService _service;

        private RunnerLocalApiFixture(RunnerLocalApiService service, FakeChatService chat, FakeSttService stt, FakeTtsService tts)
        {
            _service = service;
            Chat = chat;
            Stt = stt;
            Tts = tts;
        }

        public FakeChatService Chat { get; }
        public FakeSttService Stt { get; }
        public FakeTtsService Tts { get; }
        public string BaseUrl => _service.CurrentBaseUrl!;

        public static async Task<RunnerLocalApiFixture> StartAsync(
            bool requireApiKey,
            bool allowTts,
            bool allowRemoteStt = false,
            bool allowVoiceQuery = false,
            bool voiceAutoSendToChat = true,
            int maxUploadMb = 10)
        {
            var chat = new FakeChatService();
            var stt = new FakeSttService();
            var tts = new FakeTtsService();
            var ttsProvider = new TtsProvider();
            ttsProvider.SetCurrent(tts);
            var service = new RunnerLocalApiService(chat, stt, ttsProvider, logger: null);
            var config = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkBindAddress = "127.0.0.1",
                NetworkPort = GetFreePort(),
                NetworkRequireApiKey = requireApiKey,
                NetworkApiKey = "secret-key",
                NetworkAllowTts = allowTts,
                NetworkAllowRemoteStt = allowRemoteStt,
                NetworkAllowRemoteVoiceQuery = allowVoiceQuery,
                NetworkVoiceAutoSendToChat = voiceAutoSendToChat,
                NetworkMaxAudioUploadMB = maxUploadMb,
                Models = new List<ModelConfigEntry>
                {
                    new() { Name = "phi3", Status = ModelInstallStatus.Installed }
                }
            };

            await service.StartAsync(config, "127.0.0.1:11434");
            return new RunnerLocalApiFixture(service, chat, stt, tts);
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
        public int SendPromptCallCount { get; private set; }
        public bool EnforceConfigModelAllowList { get; set; }

        public Task<ChatResponse> SendPromptAsync(string model, string userPrompt, string host, PortableConfig config)
        {
            SendPromptCallCount++;
            if (EnforceConfigModelAllowList && !config.Models.Any(m => m.Status == ModelInstallStatus.Installed && string.Equals(m.Name, model, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Model '{model}' is not installed in config.");
            }
            return Task.FromResult(Response);
        }

        public List<string> StreamingTokens { get; set; } = new() { "tok" };
        public bool BlockTokenCallbacks { get; set; }
        public bool StreamUntilCancelled { get; set; }
        public bool StreamingCancellationObserved { get; private set; }

        private readonly TaskCompletionSource<bool> _tokenCallbackAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _allowTokenCallbacks = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _streamCancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChatResponse> SendPromptStreamingAsync(string model, string userPrompt, string host, PortableConfig config, Func<string, Task> onToken, CancellationToken cancellationToken = default)
        {
            var assembled = new StringBuilder();

            foreach (var token in StreamingTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _tokenCallbackAttempted.TrySetResult(true);

                if (BlockTokenCallbacks)
                {
                    await _allowTokenCallbacks.Task.WaitAsync(cancellationToken);
                }

                await onToken(token);
                assembled.Append(token);
            }

            try
            {
                if (StreamUntilCancelled)
                {
                    while (true)
                    {
                        await Task.Delay(50, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                StreamingCancellationObserved = true;
                _streamCancellationObserved.TrySetResult(true);
                throw;
            }

            return Response with { ResponseText = assembled.ToString() };
        }

        public Task WaitForTokenCallbackAttemptAsync(TimeSpan timeout)
        {
            return _tokenCallbackAttempted.Task.WaitAsync(timeout);
        }

        public void ReleaseTokenCallbacks()
        {
            _allowTokenCallbacks.TrySetResult(true);
        }

        public Task WaitForCancellationAsync(TimeSpan timeout)
        {
            return _streamCancellationObserved.Task.WaitAsync(timeout);
        }
    }

    private sealed class FakeSttService : ISpeechToTextService
    {
        public event Action<string>? LogMessage;
        private volatile bool _isModelLoaded = true;
        private int _concurrentTranscribe;

        public bool IsModelLoaded => _isModelLoaded;
        public string TranscriptionToReturn { get; set; } = "transcribed";
        public bool FailInitialization { get; set; }
        public int DelayTranscriptionMs { get; set; }
        public int InitializeCallCount { get; private set; }
        public int TranscribeCallCount { get; private set; }
        public int MaxConcurrentTranscribe { get; private set; }

        public Task InitializeAsync(string ssdRoot, PortableConfig config)
        {
            InitializeCallCount++;
            if (FailInitialization)
            {
                throw new InvalidOperationException("init failed");
            }

            _isModelLoaded = true;
            return Task.CompletedTask;
        }

        public Task<string> TranscribeAudioAsync(byte[] audioData)
            => TranscribeAudioAsync(audioData, CancellationToken.None);

        public async Task<string> TranscribeAudioAsync(byte[] audioData, CancellationToken cancellationToken)
        {
            if (!_isModelLoaded)
            {
                throw new InvalidOperationException("Whisper model is not loaded. Call InitializeAsync first.");
            }

            TranscribeCallCount++;
            var concurrent = Interlocked.Increment(ref _concurrentTranscribe);
            if (concurrent > MaxConcurrentTranscribe)
            {
                MaxConcurrentTranscribe = concurrent;
            }

            try
            {
                if (DelayTranscriptionMs > 0)
                {
                    await Task.Delay(DelayTranscriptionMs, cancellationToken);
                }

                return TranscriptionToReturn;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentTranscribe);
            }
        }

        public Task<string> TranscribeStreamAsync(Stream audioStream) => Task.FromResult(TranscriptionToReturn);
        public Task<string> TranscribeStreamAsync(Stream audioStream, CancellationToken cancellationToken) => Task.FromResult(TranscriptionToReturn);
        public void SetModelLoaded(bool isLoaded) => _isModelLoaded = isLoaded;

        public void Dispose() { }
    }

    private sealed class FakeTtsService : ITextToSpeechService
    {
        public int SpeakCallCount { get; private set; }
        public int StopCallCount { get; private set; }
        public int SpeakDelayMs { get; set; }
        public bool BlockSpeakUntilReleased { get; set; }
        public Task SpeakCompletedTask => _speakCompleted.Task;

        private readonly TaskCompletionSource<bool> _allowSpeakCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _speakCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<string>? LogMessage;
        public bool IsSpeaking => false;

        public void Speak(string text)
        {
            SpeakCallCount++;
        }

        public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            SpeakCallCount++;
            return SpeakAsyncCore(cancellationToken);
        }

        private async Task SpeakAsyncCore(CancellationToken cancellationToken)
        {
            if (BlockSpeakUntilReleased)
            {
                await _allowSpeakCompletion.Task.WaitAsync(cancellationToken);
            }

            if (SpeakDelayMs > 0)
            {
                await Task.Delay(SpeakDelayMs, cancellationToken);
            }

            _speakCompleted.TrySetResult(true);
        }

        public void ReleaseSpeakCompletion()
        {
            _allowSpeakCompletion.TrySetResult(true);
        }

        public async Task WaitForSpeakCallAsync(TimeSpan timeout)
        {
            var start = DateTime.UtcNow;
            while (SpeakCallCount == 0)
            {
                if (DateTime.UtcNow - start > timeout)
                {
                    throw new TimeoutException("SpeakAsync was not invoked in time.");
                }

                await Task.Delay(10);
            }
        }

        public void Stop()
        {
            StopCallCount++;
        }

        public void SetVoice(string voiceName) { }
        public void SetRate(int rate) { }
        public void SetVolume(int volume) { }
        public IReadOnlyList<string> GetAvailableVoices() => Array.Empty<string>();

        public int SynthesizeCallCount { get; private set; }
        public byte[]? SynthesizedWavBytes { get; set; } = DefaultSynthesizedWav();
        public bool ThrowOnSynthesize { get; set; }

        public Task<byte[]?> SynthesizeToWavAsync(string text, CancellationToken cancellationToken = default)
        {
            SynthesizeCallCount++;
            if (ThrowOnSynthesize)
            {
                throw new InvalidOperationException("synthesize failure");
            }
            return Task.FromResult(SynthesizedWavBytes);
        }

        private static byte[] DefaultSynthesizedWav()
        {
            return CreateTestWavBytes(new short[] { 32, -32, 64, -64 });
        }

        public void Dispose() { }
    }

    private static MultipartFormDataContent CreateWavUploadContent(string? model = "phi3", bool? autoSendToChat = null, bool? speakResponse = null, bool? returnAudio = null)
    {
        var content = new MultipartFormDataContent();
        var wavBytes = CreateTestWavBytes(new short[] { 0, 0, 256, -256, 512, -512, 0, 0 });
        var audio = new ByteArrayContent(wavBytes);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "audio", "clip.wav");

        if (model is not null)
        {
            content.Add(new StringContent(model), "model");
        }

        if (autoSendToChat is bool autoSend)
        {
            content.Add(new StringContent(autoSend ? "true" : "false"), "autoSendToChat");
        }

        if (speakResponse is bool speak)
        {
            content.Add(new StringContent(speak ? "true" : "false"), "speakResponse");
        }

        if (returnAudio is bool ret)
        {
            content.Add(new StringContent(ret ? "true" : "false"), "returnAudio");
        }

        return content;
    }

    private static HttpContent CreateMalformedMultipartContent()
    {
        var content = new StringContent("not-valid-multipart-body");
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data");
        return content;
    }

    private static byte[] CreateTestWavBytes(short[] pcmSamples)
    {
        var pcm = new byte[pcmSamples.Length * sizeof(short)];
        Buffer.BlockCopy(pcmSamples, 0, pcm, 0, pcm.Length);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        const int sampleRate = 16000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return ms.ToArray();
    }

    private static async Task<List<string>> ReadNdjsonLinesAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
            {
                break;
            }

            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }
}
