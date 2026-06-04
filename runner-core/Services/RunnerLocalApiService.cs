using System.Net;
using System.Text;
using System.Text.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace FreeAiSsd.Runner.Services;

public sealed class RunnerLocalApiService : IRunnerLocalApiService
{
    private readonly IChatService _chatService;
    private readonly ISpeechToTextService _sttService;
    private readonly ITtsProvider _ttsProvider;
    private readonly IModelManagementService? _modelService;
    private readonly IDocumentOperationsService? _docOps;
    private readonly DocumentLibraryManager? _libraryManager;
    private readonly SsdLogger? _logger;
    private readonly string _ssdRoot;
    private readonly string? _staticFilesRoot;
    private readonly SemaphoreSlim _sttInitGate = new(1, 1);
    private readonly SemaphoreSlim _sttTranscribeGate = new(1, 1);
    private readonly IndexingActivity _indexing = new();
    private WebApplication? _app;

    /// <summary>
    /// True while a document ingest, sweep, or rebuild is running on the host.
    /// The chat path warns callers when this is set and clients surface a
    /// "documents indexing / not ready" state rather than querying a partial
    /// index (task #99).
    /// </summary>
    public bool IndexingInProgress => _indexing.InProgress;

    /// <summary>
    /// Slack added on top of <see cref="PortableConfig.MaxDocumentSizeMB"/> when
    /// sizing the ingest request-body / multipart transport limits, covering
    /// multipart boundaries and part headers so a file at exactly the per-file
    /// limit isn't rejected by transport framing overhead.
    /// </summary>
    private const int IngestMultipartHeadroomMB = 16;

    /// <summary>
    /// How long the progress pump waits with no real frame before emitting an
    /// <c>indexing-heartbeat</c> keepalive, so a client's per-packet read
    /// timeout survives quiet stretches like OCR-before-embed (task #99).
    /// </summary>
    private static readonly TimeSpan ProgressHeartbeatInterval = TimeSpan.FromSeconds(15);

    public RunnerLocalApiService(
        IChatService chatService,
        ISpeechToTextService sttService,
        ITtsProvider ttsProvider,
        SsdLogger? logger,
        string? ssdRoot = null,
        string? staticFilesRoot = null,
        IDocumentOperationsService? docOps = null,
        DocumentLibraryManager? libraryManager = null,
        IModelManagementService? modelService = null)
    {
        _chatService = chatService;
        _sttService = sttService;
        _ttsProvider = ttsProvider;
        _modelService = modelService;
        _docOps = docOps;
        _libraryManager = libraryManager;
        _logger = logger;
        _ssdRoot = string.IsNullOrWhiteSpace(ssdRoot) ? AppContext.BaseDirectory : ssdRoot;
        _staticFilesRoot = string.IsNullOrWhiteSpace(staticFilesRoot) ? null : staticFilesRoot;
        _chatService.LogMessage += OnChatLogMessage;
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

        // #85: the host always runs once Ollama is up so the /chat/ web UI is
        // reachable on this device without any opt-in. NetworkModeEnabled no
        // longer gates whether the API runs — it now means "expose on the LAN".
        //   - device-only (false): force loopback, do not enforce an API key
        //     (loopback has no remote attack surface, same posture as Ollama).
        //   - LAN (true): bind the configured address and enforce the key.
        // Keying the no-key rule on NetworkModeEnabled (not the bind address)
        // keeps the auth tests — enabled + loopback + RequireApiKey — meaningful.
        var lanExposed = config.NetworkModeEnabled;
        var bindAddress = lanExposed ? NormalizeBindAddress(config.NetworkBindAddress) : "127.0.0.1";
        var enforceApiKey = lanExposed && config.NetworkRequireApiKey;
        var configuredPort = ValidatePort(config.NetworkPort);

        // MAC39: scan for a free port from configuredPort..+20. The previous
        // hard bind on the configured value failed with "address already in
        // use" on Mac after Lock + re-unlock — the kernel was still holding
        // the prior listener's port (TIME_WAIT or slow socket cleanup) and
        // the runner became unrecoverable until the user quit the app. Mirror
        // of OllamaLifecycleService.ResolvePort. The Mac sidecar's
        // mac-runner-host announces the actual baseUrl via "ready: <url>" on
        // stdout, so the Swift client picks up the shifted port transparently.
        var networkPort = ResolveAvailablePort(bindAddress, configuredPort);
        if (networkPort != configuredPort)
        {
            var notice = $"Configured port {configuredPort} on {bindAddress} unavailable; using {networkPort} instead.";
            _logger?.Info(notice);
            LogMessage?.Invoke(notice);
        }

        if (!IsLoopbackAddress(bindAddress))
        {
            var warning = $"Network Mode bind address is not loopback: exposing Runner API on {bindAddress}:{networkPort}. There is no TLS. Only use on a trusted LAN.";
            _logger?.Warn(warning);
            LogMessage?.Invoke(warning);
        }

        // Ingest uploads (POST /api/library/{id}/files) arrive as one multipart
        // body per file. Kestrel's default MaxRequestBodySize (30 MB) and the
        // default FormOptions.MultipartBodyLengthLimit (128 MB) both sit far
        // below the app-layer per-file policy (config.MaxDocumentSizeMB, 512 MB
        // default), so a large PDF was rejected at the transport layer with a
        // 413 before the size check at HandleIngestUploadAsync ever ran — RAG
        // then had nothing to retrieve and the model hallucinated. (Windows
        // ingests in-process and never hits an HTTP body limit; this gap was
        // Mac/sidecar-only.) Size both transport limits to the per-file policy
        // plus headroom for multipart framing so MaxDocumentSizeMB is the single
        // authoritative limit and oversized files get the clean per-file
        // rejection at line ~829 instead of an opaque 413.
        var maxUploadBytes =
            ((long)Math.Max(1, config.MaxDocumentSizeMB) + IngestMultipartHeadroomMB) * 1024L * 1024L;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost
            .UseKestrel(options => options.Limits.MaxRequestBodySize = maxUploadBytes)
            .UseUrls($"http://{bindAddress}:{networkPort}");

        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = maxUploadBytes;
        });

        builder.Services.AddRouting();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        });

        var app = builder.Build();

        // Static-file plumbing for the future X4 SPA (web chat UI). When the
        // host's content root contains a wwwroot/ directory we serve it via
        // ASP.NET's default-files + static-files middleware so the same Mac
        // and Windows Kestrel can serve /chat/ once X4 ships its assets.
        // No assets are bundled in MAC6 — the directory is created empty so
        // builds carry it; if a host has zero SPA files the middleware
        // falls through cleanly and the API endpoints below behave as before.
        var wwwroot = ResolveWwwroot(_staticFilesRoot);
        if (wwwroot is not null)
        {
            var fileProvider = new PhysicalFileProvider(wwwroot);
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                FileProvider = fileProvider,
                RequestPath = string.Empty
            });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = fileProvider,
                RequestPath = string.Empty
            });
        }

        app.Use(async (context, next) =>
        {
            if (string.Equals(context.Request.Path, "/api/health", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (!enforceApiKey)
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
            requireApiKey = enforceApiKey,
            ollamaRunning = !string.IsNullOrWhiteSpace(ollamaHost),
            // Task #99: lets a client (e.g. a second device on the LAN) tell
            // whether the library is mid-index before relying on RAG answers.
            indexingInProgress = _indexing.InProgress,
            timestampUtc = DateTime.UtcNow
        }));

        api.MapGet("/models", () =>
        {
            // MAC33: prefer disk truth via IModelManagementService when injected.
            // Fallback to config.Models for back-compat with older test harnesses
            // and any caller that constructs the service without the model
            // service. Both paths produce the same shape on a healthy SSD.
            List<string> models;
            if (_modelService is not null)
            {
                models = _modelService.GetInstalledModelNames(config)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                models = config.Models
                    .Where(m => m.Status == ModelInstallStatus.Installed)
                    .Select(m => m.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            // Workstream C: proactive embedding-model readiness for the Mac sidecar UI,
            // which can't call ModelManagementService in-process the way the WPF runner
            // does. Additive field — the models array is unchanged. False when no model
            // service is wired (older test harnesses); they don't surface the hint.
            var embeddingModelInstalled = _modelService?.IsEmbeddingModelInstalled(config) ?? false;

            return Results.Ok(new { models, embeddingModelInstalled });
        });

        // M14: defense-in-depth recovery path for the embedding model. C2 made
        // PrepApp auto-pull during Download/Finalize, but a user who lands on
        // the runner with the embedder missing (manual SSD tamper, partial
        // prep, mid-chat delete) has no in-app recovery without this route.
        // The Windows runner already has a Pull-embedding button that calls
        // ModelManagementService directly in-process (runner/MainWindow.xaml.cs).
        // The Mac runner UI lives in a separate process and reaches the
        // sidecar over HTTP, so the recovery has to be an API surface.
        //
        // MAC35 deferred this work over a daemon-restart concern; reading the
        // code disproves it — PullEmbeddingModelAsync is just POST /api/pull
        // against the running Ollama daemon, which handles concurrent
        // chat + pull without restart. The Windows runner has done this for
        // the entire project lifetime.
        api.MapPost("/models/embedding/pull", async () =>
        {
            if (_modelService is null)
            {
                return Results.Problem(
                    detail: "Model management service is not available on this runner.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(ollamaHost))
            {
                return Results.Problem(
                    detail: "Ollama host is not running. Start Ollama before pulling the embedding model.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var modelName = config.EmbeddingModelName?.Trim();
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return Results.Problem(
                    detail: "Embedding model name is not configured.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var success = await _modelService.PullEmbeddingModelAsync(ollamaHost, modelName);
            if (!success)
            {
                return Results.Problem(
                    detail: $"Unable to pull embedding model '{modelName}'. Connect to the internet and try again.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new { success = true, model = modelName });
        });

        api.MapPost("/chat", async (HttpContext context, ChatRequest request, CancellationToken ct) =>
        {
            var error = ValidateChatRequest(request);
            if (error is not null)
            {
                return Results.BadRequest(new ErrorResponse(error));
            }

            var result = await _chatService.SendPromptAsync(request.Model.Trim(), request.Prompt.Trim(), ollamaHost, config, BuildOverrides(request));
            return result switch
            {
                ChatResult.Success s => SetRagStatusHeader(context, "success",
                    Results.Ok(new ChatResultResponse(s.Response.ResponseText, s.Response.Sources ?? new List<string>(), s.Response.UsedRagContext))),
                ChatResult.RagRetrievalFailed r => SetRagStatusHeader(context, "retrieval-failed",
                    Results.Ok(new ChatResultResponse(r.Response.ResponseText, r.Response.Sources ?? new List<string>(), r.Response.UsedRagContext, r.RagError))),
                ChatResult.Failure f => Results.Problem(detail: f.ErrorMessage, statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => throw new System.Diagnostics.UnreachableException()
            };
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

            // C1: serialize NDJSON writes through a per-request gate. The
            // heartbeat handler below fires from a thread-pool tick that
            // cannot await the response write without blocking the timer,
            // so we fire-and-forget it — but onToken and completion writes
            // run on the request-pipeline thread and could otherwise
            // interleave with an in-flight heartbeat write on the same
            // HttpResponse stream (not concurrent-safe).
            using var writeGate = new SemaphoreSlim(1, 1);
            async Task WriteFrameAsync(object payload)
            {
                await writeGate.WaitAsync(ct);
                try { await WriteNdjsonAsync(context.Response, payload, ct); }
                finally { writeGate.Release(); }
            }

            await WriteFrameAsync(new
            {
                type = "start",
                model = request.Model.Trim(),
                usedRagContext = false,
                sources = Array.Empty<string>()
            });

            // Task #99: if a library ingest/sweep/rebuild is mid-flight, the
            // vector index is incomplete, so RAG retrieval may return nothing
            // and the model will answer ungrounded. Warn the client up front so
            // it can surface "documents still indexing" rather than letting the
            // user mistake a premature answer for a bug. Same surface as the
            // RAG-retrieval warning; the answer still streams.
            if (_indexing.InProgress)
            {
                await WriteFrameAsync(new
                {
                    type = "indexing-warning",
                    message = "Documents are still indexing — answers may be incomplete until indexing finishes."
                });
            }

            // C1: forward ChatService.FirstTokenPending heartbeats as NDJSON
            // `loading` frames. Each frame keeps the Mac URLSession 180s
            // per-packet timer alive across cold-load and lets the client paint
            // a "Loading <model>… NNs" indicator.
            void OnFirstTokenPending(int seconds)
            {
                _ = WriteFrameAsync(new { type = "loading", elapsedSeconds = seconds });
            }
            _chatService.FirstTokenPending += OnFirstTokenPending;

            ChatResult streamResult;
            try
            {
                streamResult = await _chatService.SendPromptStreamingAsync(
                    request.Model.Trim(),
                    request.Prompt.Trim(),
                    ollamaHost,
                    config,
                    onToken: token => WriteFrameAsync(new { type = "token", token }),
                    cancellationToken: ct,
                    overrides: BuildOverrides(request));
            }
            finally
            {
                _chatService.FirstTokenPending -= OnFirstTokenPending;
            }

            switch (streamResult)
            {
                case ChatResult.Success s:
                    await WriteFrameAsync(new
                    {
                        type = "complete",
                        usedRagContext = s.Response.UsedRagContext,
                        sources = s.Response.Sources ?? new List<string>(),
                        responseText = s.Response.ResponseText
                    });
                    break;
                case ChatResult.RagRetrievalFailed r:
                    await WriteFrameAsync(new { type = "rag-warning", message = r.RagError });
                    await WriteFrameAsync(new
                    {
                        type = "complete",
                        usedRagContext = r.Response.UsedRagContext,
                        sources = r.Response.Sources ?? new List<string>(),
                        responseText = r.Response.ResponseText
                    });
                    break;
                case ChatResult.Failure f:
                    await WriteFrameAsync(new { type = "error", message = f.ErrorMessage });
                    break;
            }
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
                var transcriptionResult = await TranscribeAudioSerializedAsync(parseResult.PcmAudio!, ct);
                return transcriptionResult switch
                {
                    TranscriptionResult.Success s => Results.Ok(new SttTranscribeResponse(s.Text)),
                    TranscriptionResult.Failure f => Results.Problem(detail: f.ErrorMessage, statusCode: StatusCodes.Status500InternalServerError),
                    _ => throw new System.Diagnostics.UnreachableException()
                };
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
                var transcriptionResult = await TranscribeAudioSerializedAsync(parseResult.PcmAudio!, ct);
                if (transcriptionResult is TranscriptionResult.Failure sttFailure)
                {
                    _logger?.Error($"STT transcription failed for /api/voice/query: {sttFailure.ErrorMessage}");
                    return Results.Problem(detail: sttFailure.ErrorMessage, statusCode: StatusCodes.Status500InternalServerError);
                }
                var transcription = ((TranscriptionResult.Success)transcriptionResult).Text;

                var voiceResult = await ExecuteVoiceQueryAsync(
                    transcription,
                    options,
                    ollamaHost,
                    config,
                    ct);

                return Results.Ok(voiceResult);
            }
            catch (ChatServiceFailureException ex)
            {
                _logger?.Error($"Chat service failed for /api/voice/query: {ex.Message}");
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
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

        if (_docOps is not null && _libraryManager is not null)
        {
            MapLibraryEndpoints(api, config, ollamaHost);
        }

        _app = app;
        CurrentBaseUrl = $"http://{bindAddress}:{networkPort}";
        await app.StartAsync(cancellationToken);
        _logger?.Info($"Network API started at {CurrentBaseUrl}");
        LogMessage?.Invoke($"Network API started at {CurrentBaseUrl}");
    }

    private void MapLibraryEndpoints(RouteGroupBuilder api, PortableConfig config, string ollamaHost)
    {
        var docOps = _docOps!;
        var libraryManager = _libraryManager!;
        var library = api.MapGroup("/library");

        library.MapGet("", () => Results.Ok(BuildLibraryListResponse(config)));

        library.MapPost("", async (CreateLibraryRequest request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ErrorResponse("'name' is required."));
            }

            try
            {
                var manifest = await docOps.CreateLibraryAsync(config, _ssdRoot, request.Name.Trim());
                return Results.Ok(new CreateLibraryResponse(BuildLibraryDetail(manifest), manifest.Id));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new ErrorResponse(ex.Message));
            }
        });

        library.MapPut("/active", async (SetActiveLibraryRequest? request) =>
        {
            var libraryId = string.IsNullOrWhiteSpace(request?.LibraryId) ? null : request!.LibraryId!.Trim();
            if (libraryId is not null)
            {
                var registry = libraryManager.LoadRegistry();
                if (!registry.Libraries.Any(l => l.Id == libraryId))
                {
                    return Results.NotFound(new ErrorResponse($"Library '{libraryId}' not found."));
                }
            }

            var manifest = await docOps.SetActiveLibraryAsync(config, _ssdRoot, libraryId);
            var detail = manifest is null ? null : BuildLibraryDetail(manifest);
            return Results.Ok(new SetActiveLibraryResponse(libraryId, detail));
        });

        library.MapPost("/{libraryId}/files", async (HttpContext context, string libraryId, CancellationToken ct) =>
        {
            await HandleIngestUploadAsync(context, libraryId, config, ollamaHost, ct);
        });

        library.MapDelete("/{libraryId}/files/{*relPath}", async (string libraryId, string relPath) =>
        {
            var manifest = LoadManifestIfExists(libraryId);
            if (manifest is null)
            {
                return Results.NotFound(new ErrorResponse($"Library '{libraryId}' not found."));
            }

            // ASP.NET catch-all routing decodes most percent-encoded chars
            // but deliberately leaves '%2F' encoded (decoding would change
            // route structure). Decode here so clients can use either
            // EscapeDataString-style ('files%2F<sha>_<name>') or unencoded
            // forward slashes — both round-trip to the manifest's
            // StoredRelativePath of 'files/<sha>_<name>'.
            var decoded = Uri.UnescapeDataString((relPath ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(decoded))
            {
                return Results.BadRequest(new ErrorResponse("Stored file path is required."));
            }

            // Path-traversal guard: the decoded relpath must resolve under the
            // library's files directory. Reject anything outside (e.g. "../").
            var libRoot = libraryManager.GetLibraryPath(libraryId);
            var filesRoot = libraryManager.GetFilesPath(libraryId);
            var candidate = Path.Combine(libRoot, decoded.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                PathGuards.EnsureUnderRoot(filesRoot, candidate);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }

            await docOps.RemoveFileAsync(manifest, decoded);
            var refreshed = libraryManager.LoadManifest(libraryId);
            return Results.Ok(new { library = BuildLibraryDetail(refreshed) });
        });

        library.MapPost("/{libraryId}/folders", async (string libraryId, AddWatchedFolderRequest? request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("'path' is required."));
            }

            var manifest = LoadManifestIfExists(libraryId);
            if (manifest is null)
            {
                return Results.NotFound(new ErrorResponse($"Library '{libraryId}' not found."));
            }

            var folder = request.Path.Trim();
            if (!Directory.Exists(folder))
            {
                return Results.BadRequest(new ErrorResponse($"Folder does not exist: {folder}"));
            }

            var added = await docOps.AddWatchedFolderAsync(manifest, folder);
            var refreshed = libraryManager.LoadManifest(libraryId);
            return Results.Ok(new AddWatchedFolderResponse(added, refreshed.WatchedFolders.ToList(), BuildLibraryDetail(refreshed)));
        });

        // DELETE carries the folder path in the body; [FromBody] is required because
        // minimal APIs refuse to *infer* a body parameter on DELETE (doing so throws
        // at endpoint-datasource build time and 500s every route in the group).
        library.MapDelete("/{libraryId}/folders", async (string libraryId, [FromBody] RemoveWatchedFolderRequest? request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("'path' is required."));
            }

            var manifest = LoadManifestIfExists(libraryId);
            if (manifest is null)
            {
                return Results.NotFound(new ErrorResponse($"Library '{libraryId}' not found."));
            }

            var removed = await docOps.RemoveWatchedFolderAsync(manifest, request.Path.Trim());
            var refreshed = libraryManager.LoadManifest(libraryId);
            return Results.Ok(new RemoveWatchedFolderResponse(removed, refreshed.WatchedFolders.ToList(), BuildLibraryDetail(refreshed)));
        });

        library.MapPatch("/{libraryId}", async (string libraryId, RenameLibraryRequest? request) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new ErrorResponse("'name' is required."));
            }

            if (LoadManifestIfExists(libraryId) is null)
            {
                return Results.NotFound(new ErrorResponse($"Library '{libraryId}' not found."));
            }

            try
            {
                var manifest = await docOps.RenameLibraryAsync(libraryId, request.Name.Trim());
                return Results.Ok(new { library = BuildLibraryDetail(manifest) });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new ErrorResponse(ex.Message));
            }
        });

        library.MapDelete("/{libraryId}", async (string libraryId) =>
        {
            if (LoadManifestIfExists(libraryId) is null)
            {
                return Results.NotFound(new ErrorResponse($"Library '{libraryId}' not found."));
            }

            await docOps.DeleteLibraryAsync(config, _ssdRoot, libraryId);
            return Results.Ok(BuildLibraryListResponse(config));
        });

        library.MapPost("/{libraryId}/sweep", async (HttpContext context, string libraryId, CancellationToken ct) =>
        {
            await HandleProgressedOpAsync(context, libraryId, ollamaHost, config, ct,
                (manifest, host, cfg, progress) => docOps.SweepFoldersAsync(manifest, host, cfg, progress));
        });

        library.MapPost("/{libraryId}/rebuild", async (HttpContext context, string libraryId, CancellationToken ct) =>
        {
            await HandleProgressedOpAsync(context, libraryId, ollamaHost, config, ct,
                (manifest, host, cfg, progress) => docOps.RebuildIndexAsync(manifest, host, cfg, progress));
        });
    }

    private DocumentLibraryManifest? LoadManifestIfExists(string libraryId)
    {
        var registry = _libraryManager!.LoadRegistry();
        if (!registry.Libraries.Any(l => l.Id == libraryId))
        {
            return null;
        }
        return _libraryManager.LoadManifest(libraryId);
    }

    private LibraryListResponse BuildLibraryListResponse(PortableConfig config)
    {
        var registry = _libraryManager!.LoadRegistry();
        var libraries = registry.Libraries
            .Select(l => new LibrarySummary(l.Id, string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name))
            .ToList();

        LibraryDetail? activeDetail = null;
        string? activeId = null;
        if (!string.IsNullOrWhiteSpace(config.ActiveDocumentLibraryId) &&
            libraries.Any(l => l.Id == config.ActiveDocumentLibraryId))
        {
            activeId = config.ActiveDocumentLibraryId;
            var manifest = _libraryManager.LoadManifest(activeId!);
            activeDetail = BuildLibraryDetail(manifest);
        }

        return new LibraryListResponse(libraries, activeId, activeDetail);
    }

    private static LibraryDetail BuildLibraryDetail(DocumentLibraryManifest manifest)
    {
        var files = manifest.Files
            .Select(f => new LibraryFileSummary(
                f.FileName,
                f.StoredRelativePath,
                f.SizeBytes,
                f.ImportedAtUtc))
            .ToList();

        return new LibraryDetail(
            manifest.Id,
            manifest.Name,
            manifest.Files.Count,
            files,
            manifest.WatchedFolders.ToList(),
            manifest.LastEmbeddingModel,
            manifest.LastEmbeddingDimension,
            manifest.LastIndexedUtc);
    }

    private async Task HandleIngestUploadAsync(
        HttpContext context,
        string libraryId,
        PortableConfig config,
        string ollamaHost,
        CancellationToken ct)
    {
        var manifest = LoadManifestIfExists(libraryId);
        if (manifest is null)
        {
            await WriteJsonErrorAsync(context.Response, HttpStatusCode.NotFound,
                $"Library '{libraryId}' not found.");
            return;
        }

        if (!context.Request.HasFormContentType)
        {
            await WriteJsonErrorAsync(context.Response, HttpStatusCode.BadRequest,
                "Content-Type must be multipart/form-data.");
            return;
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(ct);
        }
        catch (InvalidDataException ex)
        {
            await WriteJsonErrorAsync(context.Response, HttpStatusCode.BadRequest,
                $"Malformed multipart form data: {ex.Message}");
            return;
        }

        if (form.Files.Count == 0)
        {
            await WriteJsonErrorAsync(context.Response, HttpStatusCode.BadRequest,
                "No files were uploaded. Use multipart/form-data with file parts.");
            return;
        }

        var maxBytes = (long)Math.Max(1, config.MaxDocumentSizeMB) * 1024L * 1024L;
        var rejected = new List<(string FileName, string Reason)>();
        var accepted = new List<(string FileName, string TempPath)>();
        var tempDir = Path.Combine(Path.GetTempPath(), "freeai-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            foreach (var file in form.Files)
            {
                var fileName = string.IsNullOrWhiteSpace(file.FileName)
                    ? "(unnamed)"
                    : Path.GetFileName(file.FileName);

                if (file.Length <= 0)
                {
                    rejected.Add((fileName, "Empty file."));
                    continue;
                }

                if (file.Length > maxBytes)
                {
                    rejected.Add((fileName, $"Exceeds max document size of {config.MaxDocumentSizeMB} MB."));
                    continue;
                }

                if (!DocumentParser.IsSupported(fileName))
                {
                    rejected.Add((fileName, "Unsupported file type. Supported: PDF, TXT, MD, JSON, CSV."));
                    continue;
                }

                var tempPath = Path.Combine(tempDir, fileName);
                try
                {
                    PathGuards.EnsureUnderRoot(tempDir, tempPath);
                }
                catch (InvalidOperationException ex)
                {
                    rejected.Add((fileName, ex.Message));
                    continue;
                }

                await using var fs = File.Create(tempPath);
                await file.CopyToAsync(fs, ct);
                accepted.Add((fileName, tempPath));
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/x-ndjson";

            await WriteNdjsonAsync(context.Response, new
            {
                type = "start",
                totalFiles = accepted.Count,
                rejectedCount = rejected.Count
            }, ct);

            foreach (var (fileName, reason) in rejected)
            {
                await WriteNdjsonAsync(context.Response, new
                {
                    type = "file-rejected",
                    fileName,
                    reason
                }, ct);
            }

            if (accepted.Count > 0)
            {
                // Task #99: mark the host busy for the duration so the chat path
                // can warn and /health reports the in-flight index.
                using var _ = _indexing.Begin();
                var error = await PumpProgressAsync(context, ct, async progress =>
                {
                    await _docOps!.IngestFilesAsync(
                        manifest,
                        accepted.Select(a => a.TempPath).ToArray(),
                        ollamaHost,
                        config,
                        progress);
                });

                if (error is not null)
                {
                    await WriteNdjsonAsync(context.Response, new { type = "error", message = error }, ct);
                    return;
                }
            }

            var refreshed = _libraryManager!.LoadManifest(manifest.Id);
            await WriteNdjsonAsync(context.Response, new
            {
                type = "complete",
                library = BuildLibraryDetail(refreshed)
            }, ct);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private async Task HandleProgressedOpAsync(
        HttpContext context,
        string libraryId,
        string ollamaHost,
        PortableConfig config,
        CancellationToken ct,
        Func<DocumentLibraryManifest, string, PortableConfig, Action<IndexingProgress>?, Task> operation)
    {
        var manifest = LoadManifestIfExists(libraryId);
        if (manifest is null)
        {
            await WriteJsonErrorAsync(context.Response, HttpStatusCode.NotFound,
                $"Library '{libraryId}' not found.");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/x-ndjson";

        await WriteNdjsonAsync(context.Response, new { type = "start" }, ct);

        // Task #99: sweep/rebuild re-embed the library and can run for minutes;
        // mark the host busy so chat warns and /health reports it, same as ingest.
        using var _ = _indexing.Begin();
        var error = await PumpProgressAsync(context, ct, async progress =>
        {
            await operation(manifest, ollamaHost, config, progress);
        });

        if (error is not null)
        {
            await WriteNdjsonAsync(context.Response, new { type = "error", message = error }, ct);
            return;
        }

        var refreshed = _libraryManager!.LoadManifest(libraryId);
        await WriteNdjsonAsync(context.Response, new
        {
            type = "complete",
            library = BuildLibraryDetail(refreshed)
        }, ct);
    }

    /// <summary>
    /// Bridges a synchronous <see cref="Action{IndexingProgress}"/> callback to
    /// NDJSON progress frames on the response, streaming each frame to the
    /// client as it is produced (task #99). The operation's per-chunk callback
    /// fires from many embed worker threads, so it only enqueues onto a
    /// <see cref="System.Threading.Channels.Channel{T}"/>; a single consumer
    /// drains the channel and is the *sole* writer to the response while the
    /// operation runs — preserving the "one thread touches the HttpResponse"
    /// invariant the old buffer-and-replay design protected, but without
    /// withholding every frame until completion. The caller does not write the
    /// terminal frame until this method returns (the consumer has finished
    /// draining), so writes never interleave. Returns null on success, or the
    /// operation's failure message.
    /// </summary>
    private static async Task<string?> PumpProgressAsync(
        HttpContext context,
        CancellationToken ct,
        Func<Action<IndexingProgress>, Task> runOperation)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<IndexingProgress>(
            new System.Threading.Channels.UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        Action<IndexingProgress> progressCallback = p => channel.Writer.TryWrite(p);

        // Drain frames to the response on one thread while runOperation executes.
        // When no real frame flows for a while — e.g. OCR runs over a PDF's
        // images before any chunk is embedded, a multi-minute quiet stretch on
        // a large document — emit a lightweight heartbeat so the client's
        // per-packet read timeout doesn't fire mid-ingest. Clients ignore the
        // unknown frame type; it exists only to keep bytes flowing (task #99).
        var pump = Task.Run(async () =>
        {
            var reader = channel.Reader;
            try
            {
                while (true)
                {
                    var readTask = reader.WaitToReadAsync(ct).AsTask();
                    var winner = await Task.WhenAny(readTask, Task.Delay(ProgressHeartbeatInterval, ct)).ConfigureAwait(false);
                    if (winner != readTask)
                    {
                        await WriteNdjsonAsync(context.Response, new { type = "indexing-heartbeat" }, ct).ConfigureAwait(false);
                        continue;
                    }

                    if (!await readTask.ConfigureAwait(false))
                    {
                        break; // writer completed; all frames drained
                    }

                    while (reader.TryRead(out var progress))
                    {
                        await WriteNdjsonAsync(context.Response, new
                        {
                            type = "progress",
                            totalFiles = progress.TotalFiles,
                            completedFiles = progress.CompletedFiles,
                            currentFile = progress.CurrentFile,
                            totalChunks = progress.TotalChunks,
                            embeddedChunks = progress.EmbeddedChunks,
                            failedChunks = progress.FailedChunks,
                            skippedFiles = progress.SkippedFiles
                        }, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected / request aborted — stop pumping quietly.
            }
        }, ct);

        string? errorMessage = null;
        try
        {
            await runOperation(progressCallback);
        }
        catch (OperationCanceledException)
        {
            errorMessage = "Operation was cancelled.";
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }
        finally
        {
            channel.Writer.Complete();
        }

        // Flush all remaining frames before the caller writes the terminal frame.
        // A cancelled request (client disconnected) surfaces here as the drain
        // unwinds; prefer the operation's own message and don't rethrow.
        try
        {
            await pump;
        }
        catch (OperationCanceledException)
        {
            errorMessage ??= "Operation was cancelled.";
        }

        return errorMessage;
    }

    private static async Task WriteJsonErrorAsync(HttpResponse response, HttpStatusCode statusCode, string message)
    {
        response.StatusCode = (int)statusCode;
        await response.WriteAsJsonAsync(new ErrorResponse(message));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            // ConfigureAwait(false) so a sync-blocking caller on the UI thread
            // (Runner OnClosing) can't deadlock waiting for these continuations.
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
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
        _chatService.LogMessage -= OnChatLogMessage;
        await StopAsync();
    }

    private void OnChatLogMessage(string message)
    {
        _logger?.Info(message);
        LogMessage?.Invoke(message);
    }

    /// <summary>
    /// Resolves the wwwroot directory served by the static-file middleware. In
    /// production this is the published app/content root alongside the assembly;
    /// tests may inject a throwaway root so they do not contend on a shared
    /// <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    private static string? ResolveWwwroot(string? staticFilesRoot)
    {
        if (!string.IsNullOrWhiteSpace(staticFilesRoot))
        {
            return Directory.Exists(staticFilesRoot) ? staticFilesRoot : null;
        }

        var baseLocal = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        return Directory.Exists(baseLocal) ? baseLocal : null;
    }

    private static readonly JsonSerializerOptions NdjsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static async Task WriteNdjsonAsync(HttpResponse response, object payload, CancellationToken ct)
    {
        // Match ConfigureHttpJsonOptions: every NDJSON frame uses camelCase so
        // nested records (LibraryDetail, etc.) serialize with the same property
        // shape clients see from regular IResult responses. Without this,
        // anonymous-type fields ("type", "library") render camelCase but
        // record properties ("FileCount", "Files") render PascalCase, and the
        // resulting mixed-case JSON fails any consumer that expects camelCase.
        var json = JsonSerializer.Serialize(payload, NdjsonSerializerOptions);
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

        // Optional per-request model parameters: validate ranges only when present.
        if (request.Temperature is { } temp && (temp < 0 || temp > 2))
        {
            return "'temperature' must be between 0 and 2.";
        }

        if (request.TopP is { } topP && (topP < 0 || topP > 1))
        {
            return "'topP' must be between 0 and 1.";
        }

        if (request.MaxOutputTokens is { } maxTokens && (maxTokens <= 0 || maxTokens > 131072))
        {
            return "'maxOutputTokens' must be between 1 and 131072.";
        }

        if (request.ContextWindow is { } ctx && (ctx <= 0 || ctx > 1048576))
        {
            return "'contextWindow' must be between 1 and 1048576.";
        }

        if (request.Think is { } think && think.Trim().Length > 0)
        {
            switch (think.Trim().ToLowerInvariant())
            {
                case "off":
                case "low":
                case "medium":
                case "high":
                    break;
                default:
                    return "'think' must be one of: off, low, medium, high.";
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the per-request <see cref="ChatParameterOverrides"/> from a
    /// <see cref="ChatRequest"/>, or null when the request carries no overrides
    /// (so untouched requests behave exactly as before). Validation has already
    /// range-checked any present values.
    /// </summary>
    private static ChatParameterOverrides? BuildOverrides(ChatRequest request)
    {
        var think = string.IsNullOrWhiteSpace(request.Think) ? null : request.Think!.Trim();
        if (request.Temperature is null && request.TopP is null && request.MaxOutputTokens is null
            && think is null && request.ContextWindow is null)
        {
            return null;
        }

        return new ChatParameterOverrides(
            request.Temperature,
            request.TopP,
            request.MaxOutputTokens,
            think,
            request.ContextWindow);
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

        var chatResult = await _chatService.SendPromptAsync(options.Model.Trim(), trimmedTranscription, ollamaHost, config);
        if (chatResult is ChatResult.Failure chatFailure)
        {
            throw new ChatServiceFailureException(chatFailure.ErrorMessage);
        }
        var chat = chatResult switch
        {
            ChatResult.Success s => s.Response,
            ChatResult.RagRetrievalFailed r => r.Response,
            _ => throw new System.Diagnostics.UnreachableException()
        };

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

    private async Task<TranscriptionResult> TranscribeAudioSerializedAsync(byte[] audioData, CancellationToken ct)
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

    private static IResult SetRagStatusHeader(HttpContext context, string status, IResult result)
    {
        context.Response.Headers["X-RAG-Status"] = status;
        return result;
    }

    private sealed class SttUnavailableException : Exception
    {
        public SttUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class ChatServiceFailureException : Exception
    {
        public ChatServiceFailureException(string message) : base(message) { }
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

    /// <summary>
    /// MAC39: scans up to 20 ports starting at <paramref name="preferredPort"/>
    /// for one that can currently be bound on <paramref name="bindAddress"/>.
    /// Falls back to the preferred value if all 20 are taken (Kestrel will
    /// then raise the canonical bind error). Used to route around the
    /// "address already in use" failure on Mac after Lock + re-unlock, where
    /// the kernel still holds the prior listener's port even after the
    /// process has exited.
    /// </summary>
    internal static int ResolveAvailablePort(string bindAddress, int preferredPort)
    {
        if (!IPAddress.TryParse(bindAddress, out var address))
        {
            address = IPAddress.Loopback;
        }

        for (var port = preferredPort; port < preferredPort + 20 && port <= 65535; port++)
        {
            System.Net.Sockets.TcpListener? listener = null;
            try
            {
                listener = new System.Net.Sockets.TcpListener(address, port);
                listener.Start();
                return port;
            }
            catch (System.Net.Sockets.SocketException) { }
            finally
            {
                listener?.Stop();
            }
        }

        return preferredPort;
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

    // Optional per-request model parameters (X4 web chat UI). Null = fall back to
    // the host's saved PortableConfig. These NEVER write back to config — they are
    // applied for this one request only (the WPF/Mac runner shares the saved config).
    public sealed record ChatRequest(
        string Model,
        string Prompt,
        double? Temperature = null,
        double? TopP = null,
        int? MaxOutputTokens = null,
        string? Think = null,
        int? ContextWindow = null);
    public sealed record ChatResultResponse(string ResponseText, IReadOnlyList<string> Sources, bool UsedRagContext, string? RagWarning = null);
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

    // Library management DTOs (MAC8). Shared between the Mac Swift UI client and
    // future Companion / RunnerCli clients; the same shape is returned regardless
    // of host (Windows in-process or Mac sidecar). Mutating endpoints return
    // updated activeLibraryId so Mac clients can persist via Swift's
    // SsdEncryption (the host's IConfigStore is a no-op on Mac to preserve the
    // MAC5/MAC6 plaintext-config invariant).
    public sealed record LibrarySummary(string Id, string Name);

    public sealed record LibraryFileSummary(
        string FileName,
        string StoredRelativePath,
        long SizeBytes,
        DateTime ImportedAtUtc);

    public sealed record LibraryDetail(
        string Id,
        string Name,
        int FileCount,
        IReadOnlyList<LibraryFileSummary> Files,
        IReadOnlyList<string> WatchedFolders,
        string? LastEmbeddingModel,
        int? LastEmbeddingDimension,
        DateTime? LastIndexedUtc);

    public sealed record LibraryListResponse(
        IReadOnlyList<LibrarySummary> Libraries,
        string? ActiveLibraryId,
        LibraryDetail? ActiveLibrary);

    public sealed record CreateLibraryRequest(string Name);
    public sealed record CreateLibraryResponse(LibraryDetail Library, string ActiveLibraryId);

    public sealed record SetActiveLibraryRequest(string? LibraryId);
    public sealed record SetActiveLibraryResponse(string? ActiveLibraryId, LibraryDetail? ActiveLibrary);

    public sealed record AddWatchedFolderRequest(string Path);
    public sealed record AddWatchedFolderResponse(
        bool Added,
        IReadOnlyList<string> WatchedFolders,
        LibraryDetail Library);

    public sealed record RemoveWatchedFolderRequest(string Path);
    public sealed record RemoveWatchedFolderResponse(
        bool Removed,
        IReadOnlyList<string> WatchedFolders,
        LibraryDetail Library);

    public sealed record RenameLibraryRequest(string Name);

    private sealed record VoiceQueryOptions(bool SendToChat, bool SpeakResponse, string? Model, bool ReturnAudio);

    private sealed record ParsedAudioUpload(bool Success, byte[]? PcmAudio, string? Error)
    {
        public static ParsedAudioUpload Ok(byte[] pcmAudio) => new(true, pcmAudio, null);
        public static ParsedAudioUpload Fail(string error) => new(false, null, error);
    }
}
