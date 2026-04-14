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
    private readonly Func<ITextToSpeechService?> _ttsServiceFactory;
    private readonly SsdLogger? _logger;
    private WebApplication? _app;

    public RunnerLocalApiService(
        IChatService chatService,
        Func<ITextToSpeechService?> ttsServiceFactory,
        SsdLogger? logger)
    {
        _chatService = chatService;
        _ttsServiceFactory = ttsServiceFactory;
        _logger = logger;
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
                onToken: token =>
                {
                    WriteNdjsonAsync(context.Response, new { type = "token", token }, ct).GetAwaiter().GetResult();
                },
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

            var tts = _ttsServiceFactory();
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

            var tts = _ttsServiceFactory();
            if (tts is null)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            tts.Stop();
            return Results.Ok(new { status = "stopped" });
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

    private static async Task WriteErrorAsync(HttpContext context, HttpStatusCode statusCode, string message)
    {
        context.Response.StatusCode = (int)statusCode;
        await context.Response.WriteAsJsonAsync(new ErrorResponse(message));
    }

    private static string NormalizeBindAddress(string? configured)
    {
        var addr = string.IsNullOrWhiteSpace(configured) ? "0.0.0.0" : configured.Trim();
        if (!IPAddress.TryParse(addr, out _))
        {
            throw new InvalidOperationException("NetworkBindAddress must be a valid IPv4 or IPv6 address.");
        }

        return addr;
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
    public sealed record ErrorResponse(string Error);
}
