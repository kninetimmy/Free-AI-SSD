using System.Net;
using System.Text;
using System.Text.Json;
using FreeAiSsd.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FreeAiSsd.Runner.Services;

public sealed class RunnerLocalApiService : IRunnerLocalApiService
{
    private readonly IChatService _chatService;
    private readonly ISpeechToTextService _sttService;
    private readonly ITtsProvider _ttsProvider;
    private readonly SsdLogger? _logger;
    private readonly string _ssdRoot;
    private readonly SemaphoreSlim _sttInitGate = new(1, 1);
    private readonly SemaphoreSlim _sttTranscribeGate = new(1, 1);
    private WebApplication? _app;

    public RunnerLocalApiService(
        IChatService chatService,
        ISpeechToTextService sttService,
        ITtsProvider ttsProvider,
        SsdLogger? logger,
        string? ssdRoot = null)
    {
        _chatService = chatService;
        _sttService = sttService;
        _ttsProvider = ttsProvider;
        _logger = logger;
        _ssdRoot = string.IsNullOrWhiteSpace(ssdRoot) ? AppContext.BaseDirectory : ssdRoot;
    }

    public event Action<string>? LogMessage;

    public bool IsRunning => _app is not null;
    public string? CurrentBaseUrl { get; private set; }

    public async Task StartAsync(PortableConfig config, string ollamaHost, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            await StopAsync(cancellationToken);
        }

        if (!config.NetworkModeEnabled)
        {
            LogMessage?.Invoke("Network Mode is disabled in config.");
            return;
        }

        var bindAddress = NormalizeBindAddress(config.NetworkBindAddress);
        var networkPort = ValidatePort(config.NetworkPort);

        if (!IsLoopbackAddress(bindAddress))
        {
            var warning = $"Network Mode bind address is not loopback: exposing Runner API on {bindAddress}:{networkPort}. There is no TLS. Only use on a trusted LAN.";
            _logger?.Warn(warning);
            LogMessage?.Invoke(warning);
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel().UseUrls($"http://{bindAddress}:{networkPort}");

        builder.Services.AddRouting();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if (string.Equals(context.Request.Path, "/api/health", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (!config.NetworkRequireApiKey)
            {
                await next();
                return;
            }

            var expectedKey = config.NetworkApiKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(expectedKey))
            {
                await WriteErrorAsync(context, HttpStatusCode.ServiceUnavailable,
                    "API key is required by configuration but not set on host.");
                return;
            }

            var providedKey = TryReadApiKey(context.Request);
            if (!ConstantTimeEquals(expectedKey, providedKey))
            {
                await WriteErrorAsync(context, HttpStatusCode.Unauthorized, "Missing or invalid API key.");
                return;
            }

            await next();
        });

        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            networkModeEnabled = config.NetworkModeEnabled,
            requireApiKey = config.NetworkRequireApiKey,
            ollamaRunning = !string.IsNullOrWhiteSpace(ollamaHost),
            timestampUtc = DateTime.UtcNow
        }));

        api.MapGet("/models", () =>
        {
            var models = config.Models
                .Where(m => m.Status == ModelInstallStatus.Installed)
                .Select(m => m.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(new { models });
        });

        api.MapPost("/chat", async (ChatRequest request, CancellationToken ct) =>
        {
            var error = ValidateChatRequest(request);
            if (error is not null)
            {
                return Results.BadRequest(new ErrorResponse(error));
            }

            var response = await _chatService.SendPromptAsync(request.Model.Trim(), request.Prompt.Trim(), ollamaHost, config);
            return Results.Ok(new ChatResultResponse(
                response.ResponseText,
                response.Sources ?? new List<string>(),
                response.UsedRagContext));
        });

        api.MapPost("/chat/stream", async (HttpContext context, ChatRequest request, CancellationToken ct) =>
        {
            var error = ValidateChatRequest(request);
            if (error is not null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new ErrorResponse(error), cancellationToken: ct);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-ndjson";

            await WriteNdjsonAsync(context.Response, new
            {
                type = "start",
                model = request.Model.Trim(),
                usedRagContext = false,
                sources = Array.Empty<string>()
            }, ct);

            var streamResponse = await _chatService.SendPromptStreamingAsync(
                request.Model.Trim(),
                request.Prompt.Trim(),
                ollamaHost,
                config,
                onToken: token => WriteNdjsonAsync(context.Response, new { type = "token", token }, ct),
                cancellationToken: ct);

            await WriteNdjsonAsync(context.Response, new
            {
                type = "complete",
                usedRagContext = streamResponse.UsedRagContext,
                sources = streamResponse.Sources ?? new List<string>(),
                responseText = streamResponse.ResponseText
            }, ct);
        });

        api.MapPost("/tts/speak", async (TtsSpeakRequest request, CancellationToken ct) =>
        {
            if (!config.NetworkAllowTts)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrWhiteSpace(request.Text))
            {
                return Results.BadRequest(new ErrorResponse("'text' is required."));
            }

            var tts = _ttsProvider.Current;
            if (tts is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            await tts.SpeakAsync(request.Text.Trim(), ct);
            return Results.Ok(new { status = "speaking" });
        });

        api.MapPost("/tts/stop", () =>
        {
            if (!config.NetworkAllowTts)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var tts = _ttsProvider.Current;
            if (tts is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            tts.Stop();
            return Results.Ok(new { status = "stopped" });
        });

        api.MapPost("/stt/transcribe", async (HttpContext context, CancellationToken ct) =>
        {
            if (!config.NetworkAllowRemoteStt)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var parseResult = await ParseUploadedAudioAsync(context, config, ct);
            if (!parseResult.Success)
            {
                _logger?.Warn($"Rejected /api/stt/transcribe request: {parseResult.Error}");
                return Results.BadRequest(new ErrorResponse(parseResult.Error!));
            }

            try
            {
                await EnsureSttInitializedAsync(config, ct);
                var transcription = await TranscribeAudioSerializedAsync(parseResult.PcmAudio!, ct);
                return Results.Ok(new SttTranscribeResponse(transcription));
            }
            catch (SttUnavailableException ex)
            {
                _logger?.Error($"STT unavailable: {ex.Message}");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                _logger?.Error($"STT transcription failed: {ex.Message}");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        });

        api.MapPost("/voice/query", async (HttpContext context, CancellationToken ct) =>
        {
            if (!config.NetworkAllowRemoteVoiceQuery)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var parseResult = await ParseUploadedAudioAsync(context, config, ct);
            if (!parseResult.Success)
            {
                _logger?.Warn($"Rejected /api/voice/query request: {parseResult.Error}");
                return Results.BadRequest(new ErrorResponse(parseResult.Error!));
            }

            VoiceQueryOptions options;
            try
            {
                options = await ParseVoiceQueryOptionsAsync(context, config, ct);
            }
            catch (InvalidOperationException ex)
            {
                _logger?.Warn($"Rejected /api/voice/query request: {ex.Message}");
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }

            try
            {
                await EnsureSttInitializedAsync(config, ct);
                var transcription = await TranscribeAudioSerializedAsync(parseResult.PcmAudio!, ct);
                var voiceResult = await ExecuteVoiceQueryAsync(
                    transcription,
                    options,
                    ollamaHost,
                    config,
                    ct);

                return Results.Ok(voiceResult);
            }
            catch (InvalidOperationException ex)
            {
                _logger?.Warn($"Rejected /api/voice/query request: {ex.Message}");
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
            catch (SttUnavailableException ex)
            {
                _logger?.Error($"STT unavailable for /api/voice/query: {ex.Message}");
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                _logger?.Error($"Voice query failed: {ex.Message}");
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }
        });

        _app = app;
        CurrentBaseUrl = $"http://{bindAddress}:{networkPort}";
        await app.StartAsync(cancellationToken);
        _logger?.Info($"Network API started at {CurrentBaseUrl}");
        LogMessage?.Invoke($"Network API started at {CurrentBaseUrl}");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _logger?.Info("Network API stopped.");
            LogMessage?.Invoke("Network API stopped.");
        }
        catch (Exception ex)
        {
            _logger?.Error($"Error stopping Network API: {ex.Message}");
            LogMessage?.Invoke($"Network API stop error: {ex.Message}");
        }
        finally
        {
            _app = null;
            CurrentBaseUrl = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private static async Task WriteNdjsonAsync(HttpResponse response, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        await response.WriteAsync(json + "\n", Encoding.UTF8, ct);
        await response.Body.FlushAsync(ct);
    }

    private static string? ValidateChatRequest(ChatRequest request)
    {
        if (request is null)
        {
            return "Request body is required.";
        }

        if (string.IsNullOrWhiteSpace(request.Model))
        {
            return "'model' is required.";
        }

        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return "'prompt' is required.";
        }

        if (request.Model.Length > 200)
        {
            return "'model' is too long.";
        }

        if (request.Prompt.Length > 64000)
        {
            return "'prompt' exceeds maximum length.";
        }

        return null;
    }

    private async Task<VoiceQueryResponse> ExecuteVoiceQueryAsync(
        string transcription,
        VoiceQueryOptions options,
        string ollamaHost,
        PortableConfig config,
        CancellationToken cancellationToken)
    {
        var trimmedTranscription = transcription.Trim();
        if (!options.SendToChat || string.IsNullOrWhiteSpace(trimmedTranscription))
        {
            return new VoiceQueryResponse(trimmedTranscription, null, Array.Empty<string>(), false);
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new InvalidOperationException("'model' is required when sending transcription to chat.");
        }

        var chat = await _chatService.SendPromptAsync(options.Model.Trim(), trimmedTranscription, ollamaHost, config);

        var ttsTriggered = false;
        string? audioBase64 = null;
        string? audioMime = null;

        if (options.SpeakResponse && !string.IsNullOrWhiteSpace(chat.ResponseText) && config.NetworkAllowTts)
        {
            var tts = _ttsProvider.Current;
            if (tts is not null)
            {
                if (options.ReturnAudio)
                {
                    // Companion wants to play the audio locally (e.g. VR machine). Synthesize
                    // to an in-memory WAV instead of kicking off host playback, and skip the
                    // fire-and-forget host TTS path to avoid double audio.
                    try
                    {
                        var wavBytes = await tts.SynthesizeToWavAsync(chat.ResponseText, cancellationToken);
                        if (wavBytes is { Length: > 0 })
                        {
                            var maxBytes = (long)Math.Max(1, config.NetworkMaxAudioUploadMB) * 1024L * 1024L;
                            if (wavBytes.LongLength > maxBytes)
                            {
                                _logger?.Warn($"Synthesized TTS WAV ({wavBytes.LongLength} bytes) exceeds networkMaxAudioUploadMB={config.NetworkMaxAudioUploadMB}; omitting audio from response.");
                            }
                            else
                            {
                                audioBase64 = Convert.ToBase64String(wavBytes);
                                audioMime = "audio/wav";
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error($"TTS synthesize-to-bytes failed: {ex.Message}");
                    }
                }
                else
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await tts.SpeakAsync(chat.ResponseText, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Error($"Host TTS failed after trigger: {ex.Message}");
                        }
                    }, cancellationToken);
                    ttsTriggered = true;
                }
            }
        }

        return new VoiceQueryResponse(
            trimmedTranscription,
            chat.ResponseText,
            chat.Sources ?? new List<string>(),
            ttsTriggered,
            audioBase64,
            audioMime);
    }

    private static async Task<ParsedAudioUpload> ParseUploadedAudioAsync(
        HttpContext context,
        PortableConfig config,
        CancellationToken ct)
    {
        if (!context.Request.HasFormContentType)
        {
            return ParsedAudioUpload.Fail("Content-Type must be multipart/form-data.");
        }

        var maxBytes = (long)Math.Max(1, config.NetworkMaxAudioUploadMB) * 1024L * 1024L;
        if (context.Request.ContentLength is long length && length > maxBytes)
        {
            return ParsedAudioUpload.Fail($"Upload exceeds max size of {config.NetworkMaxAudioUploadMB} MB.");
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(ct);
        }
        catch (InvalidDataException ex)
        {
            return ParsedAudioUpload.Fail($"Malformed multipart form data: {ex.Message}");
        }
        var file = form.Files.GetFile("audio");
        if (file is null)
        {
            return ParsedAudioUpload.Fail("Missing uploaded file field 'audio'.");
        }

        if (file.Length <= 0)
        {
            return ParsedAudioUpload.Fail("Uploaded file is empty.");
        }

        if (file.Length > maxBytes)
        {
            return ParsedAudioUpload.Fail($"Upload exceeds max size of {config.NetworkMaxAudioUploadMB} MB.");
        }

        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var data = ms.ToArray();

        var format = (form["format"].FirstOrDefault() ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(format))
        {
            format = InferAudioFormat(file.ContentType, file.FileName, data);
        }

        return format switch
        {
            "wav" => TryParseWavToPcm(data),
            "pcm16le" => ParsedAudioUpload.Ok(data),
            _ => ParsedAudioUpload.Fail("Unsupported audio format. Use WAV (PCM16 mono 16kHz) or PCM16LE.")
        };
    }

    private static async Task<VoiceQueryOptions> ParseVoiceQueryOptionsAsync(
        HttpContext context,
        PortableConfig config,
        CancellationToken ct)
    {
        var form = await context.Request.ReadFormAsync(ct);

        static bool? ParseOptionalBool(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return bool.TryParse(value, out var parsed) ? parsed : throw new InvalidOperationException("Boolean form values must be 'true' or 'false'.");
        }

        var autoSend = ParseOptionalBool(form["autoSendToChat"].FirstOrDefault());
        var speakResponse = ParseOptionalBool(form["speakResponse"].FirstOrDefault()) ?? false;
        var returnAudio = ParseOptionalBool(form["returnAudio"].FirstOrDefault()) ?? false;
        var model = form["model"].FirstOrDefault()?.Trim();

        return new VoiceQueryOptions(
            autoSend ?? config.NetworkVoiceAutoSendToChat,
            speakResponse,
            model,
            returnAudio);
    }

    private static string InferAudioFormat(string? contentType, string? fileName, byte[] data)
    {
        if (LooksLikeWav(data))
        {
            return "wav";
        }

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var normalized = contentType.Trim().ToLowerInvariant();
            if (normalized.Contains("wav"))
            {
                return "wav";
            }

            if (normalized is "application/octet-stream" or "audio/l16")
            {
                return "pcm16le";
            }
        }

        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return ext switch
        {
            ".wav" => "wav",
            ".pcm" or ".raw" => "pcm16le",
            _ => string.Empty
        };
    }

    private async Task EnsureSttInitializedAsync(PortableConfig config, CancellationToken ct)
    {
        if (_sttService.IsModelLoaded)
        {
            return;
        }

        await _sttInitGate.WaitAsync(ct);
        try
        {
            if (_sttService.IsModelLoaded)
            {
                return;
            }

            await _sttService.InitializeAsync(_ssdRoot, config);
        }
        catch (Exception ex)
        {
            throw new SttUnavailableException("Failed to initialize speech-to-text service.", ex);
        }
        finally
        {
            _sttInitGate.Release();
        }
    }

    private async Task<string> TranscribeAudioSerializedAsync(byte[] audioData, CancellationToken ct)
    {
        await _sttTranscribeGate.WaitAsync(ct);
        try
        {
            return await _sttService.TranscribeAudioAsync(audioData, ct);
        }
        finally
        {
            _sttTranscribeGate.Release();
        }
    }

    private sealed class SttUnavailableException : Exception
    {
        public SttUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private static ParsedAudioUpload TryParseWavToPcm(byte[] wavData)
    {
        try
        {
            if (!LooksLikeWav(wavData))
            {
                return ParsedAudioUpload.Fail("Invalid WAV header.");
            }

            var offset = 12;
            short audioFormat = 0;
            short channels = 0;
            int sampleRate = 0;
            short bitsPerSample = 0;
            byte[]? pcmData = null;

            while (offset + 8 <= wavData.Length)
            {
                var chunkId = Encoding.ASCII.GetString(wavData, offset, 4);
                var chunkSize = BitConverter.ToInt32(wavData, offset + 4);
                offset += 8;

                if (chunkSize < 0 || offset + chunkSize > wavData.Length)
                {
                    return ParsedAudioUpload.Fail("Corrupt WAV payload.");
                }

                if (chunkId == "fmt ")
                {
                    if (chunkSize < 16)
                    {
                        return ParsedAudioUpload.Fail("WAV fmt chunk is too small.");
                    }

                    audioFormat = BitConverter.ToInt16(wavData, offset);
                    channels = BitConverter.ToInt16(wavData, offset + 2);
                    sampleRate = BitConverter.ToInt32(wavData, offset + 4);
                    bitsPerSample = BitConverter.ToInt16(wavData, offset + 14);
                }
                else if (chunkId == "data")
                {
                    pcmData = new byte[chunkSize];
                    Buffer.BlockCopy(wavData, offset, pcmData, 0, chunkSize);
                }

                offset += chunkSize;
                if ((chunkSize & 1) == 1 && offset < wavData.Length)
                {
                    offset++;
                }
            }

            if (audioFormat != 1 || channels != 1 || sampleRate != 16000 || bitsPerSample != 16)
            {
                return ParsedAudioUpload.Fail("WAV must be PCM 16-bit mono at 16kHz.");
            }

            if (pcmData is null || pcmData.Length == 0)
            {
                return ParsedAudioUpload.Fail("WAV file contains no audio data.");
            }

            return ParsedAudioUpload.Ok(pcmData);
        }
        catch
        {
            return ParsedAudioUpload.Fail("Failed to parse WAV upload.");
        }
    }

    private static bool LooksLikeWav(byte[] data)
    {
        if (data.Length < 12)
        {
            return false;
        }

        return data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
               data[8] == 'W' && data[9] == 'A' && data[10] == 'V' && data[11] == 'E';
    }

    private static async Task WriteErrorAsync(HttpContext context, HttpStatusCode statusCode, string message)
    {
        context.Response.StatusCode = (int)statusCode;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(message));
    }

    private static string NormalizeBindAddress(string? configured)
    {
        // Default to loopback so an unset/blank value cannot silently expose the LAN API.
        var addr = string.IsNullOrWhiteSpace(configured) ? "127.0.0.1" : configured.Trim();
        if (!IPAddress.TryParse(addr, out _))
        {
            throw new InvalidOperationException("NetworkBindAddress must be a valid IPv4 or IPv6 address.");
        }

        return addr;
    }

    private static bool IsLoopbackAddress(string address)
    {
        return IPAddress.TryParse(address, out var parsed) && IPAddress.IsLoopback(parsed);
    }

    private static int ValidatePort(int configuredPort)
    {
        if (configuredPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("NetworkPort must be between 1 and 65535.");
        }

        return configuredPort;
    }

    private static string? TryReadApiKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out var authValues))
        {
            var auth = authValues.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return auth["Bearer ".Length..].Trim();
            }
        }

        if (request.Headers.TryGetValue("X-API-Key", out var keyValues))
        {
            return keyValues.ToString().Trim();
        }

        return null;
    }

    private static bool ConstantTimeEquals(string expected, string? provided)
    {
        if (provided is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    public sealed record ChatRequest(string Model, string Prompt);
    public sealed record ChatResultResponse(string ResponseText, IReadOnlyList<string> Sources, bool UsedRagContext);
    public sealed record TtsSpeakRequest(string Text);
    public sealed record SttTranscribeResponse(string Transcription);
    public sealed record VoiceQueryResponse(
        string Transcription,
        string? ResponseText,
        IReadOnlyList<string> Sources,
        bool TtsTriggeredOnHost,
        string? AudioBase64 = null,
        string? AudioMime = null);
    public sealed record ErrorResponse(string Error);

    private sealed record VoiceQueryOptions(bool SendToChat, bool SpeakResponse, string? Model, bool ReturnAudio);

    private sealed record ParsedAudioUpload(bool Success, byte[]? PcmAudio, string? Error)
    {
        public static ParsedAudioUpload Ok(byte[] pcmAudio) => new(true, pcmAudio, null);
        public static ParsedAudioUpload Fail(string error) => new(false, null, error);
    }
}
