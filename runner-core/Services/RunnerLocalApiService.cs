using System.Net;
using System.Text;
using System.Text.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
    private readonly IDocumentOperationsService? _docOps;
    private readonly DocumentLibraryManager? _libraryManager;
    private readonly SsdLogger? _logger;
    private readonly string _ssdRoot;
    private readonly string? _staticFilesRoot;
    private readonly SemaphoreSlim _sttInitGate = new(1, 1);
    private readonly SemaphoreSlim _sttTranscribeGate = new(1, 1);
    private WebApplication? _app;

    public RunnerLocalApiService(
        IChatService chatService,
        ISpeechToTextService sttService,
        ITtsProvider ttsProvider,
        SsdLogger? logger,
        string? ssdRoot = null,
        string? staticFilesRoot = null,
        IDocumentOperationsService? docOps = null,
        DocumentLibraryManager? libraryManager = null)
    {
        _chatService = chatService;
        _sttService = sttService;
        _ttsProvider = ttsProvider;
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

        api.MapPost("/chat", async (HttpContext context, ChatRequest request, CancellationToken ct) =>
        {
            var error = ValidateChatRequest(request);
            if (error is not null)
            {
                return Results.BadRequest(new ErrorResponse(error));
            }

            var result = await _chatService.SendPromptAsync(request.Model.Trim(), request.Prompt.Trim(), ollamaHost, config);
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

            await WriteNdjsonAsync(context.Response, new
            {
                type = "start",
                model = request.Model.Trim(),
                usedRagContext = false,
                sources = Array.Empty<string>()
            }, ct);

            var streamResult = await _chatService.SendPromptStreamingAsync(
                request.Model.Trim(),
                request.Prompt.Trim(),
                ollamaHost,
                config,
                onToken: token => WriteNdjsonAsync(context.Response, new { type = "token", token }, ct),
                cancellationToken: ct);

            switch (streamResult)
            {
                case ChatResult.Success s:
                    await WriteNdjsonAsync(context.Response, new
                    {
                        type = "complete",
                        usedRagContext = s.Response.UsedRagContext,
                        sources = s.Response.Sources ?? new List<string>(),
                        responseText = s.Response.ResponseText
                    }, ct);
                    break;
                case ChatResult.RagRetrievalFailed r:
                    await WriteNdjsonAsync(context.Response, new { type = "rag-warning", message = r.RagError }, ct);
                    await WriteNdjsonAsync(context.Response, new
                    {
                        type = "complete",
                        usedRagContext = r.Response.UsedRagContext,
                        sources = r.Response.Sources ?? new List<string>(),
                        responseText = r.Response.ResponseText
                    }, ct);
                    break;
                case ChatResult.Failure f:
                    await WriteNdjsonAsync(context.Response, new { type = "error", message = f.ErrorMessage }, ct);
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

        library.MapGet("", () =>
        {
            var registry = libraryManager.LoadRegistry();
            var libraries = registry.Libraries
                .Select(l => new LibrarySummary(l.Id, string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name))
                .ToList();

            LibraryDetail? activeDetail = null;
            string? activeId = null;
            if (!string.IsNullOrWhiteSpace(config.ActiveDocumentLibraryId) &&
                libraries.Any(l => l.Id == config.ActiveDocumentLibraryId))
            {
                activeId = config.ActiveDocumentLibraryId;
                var manifest = libraryManager.LoadManifest(activeId!);
                activeDetail = BuildLibraryDetail(manifest);
            }

            return Results.Ok(new LibraryListResponse(libraries, activeId, activeDetail));
        });

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

            // ASP.NET catch-all routing has already URL-decoded path segments.
            var decoded = (relPath ?? string.Empty).Trim();
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
    /// NDJSON progress frames on the response. Buffers progress events in a
    /// thread-safe queue during the operation, then drains the queue back to
    /// the response after the operation completes — keeps response writes on
    /// a single thread (no Task.Run cross-thread to Kestrel's pipe), at the
    /// cost of replaying all progress at once instead of live-streaming.
    /// Returns null on success, or the operation's failure message.
    /// </summary>
    private static async Task<string?> PumpProgressAsync(
        HttpContext context,
        CancellationToken ct,
        Func<Action<IndexingProgress>, Task> runOperation)
    {
        var buffered = new System.Collections.Concurrent.ConcurrentQueue<IndexingProgress>();
        Action<IndexingProgress> progressCallback = p => buffered.Enqueue(p);

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

        while (buffered.TryDequeue(out var progress))
        {
            await WriteNdjsonAsync(context.Response, new
            {
                type = "progress",
                totalFiles = progress.TotalFiles,
                completedFiles = progress.CompletedFiles,
                currentFile = progress.CurrentFile,
                totalChunks = progress.TotalChunks,
                embeddedChunks = progress.EmbeddedChunks,
                failedChunks = progress.FailedChunks
            }, ct);
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

    private sealed record VoiceQueryOptions(bool SendToChat, bool SpeakResponse, string? Model, bool ReturnAudio);

    private sealed record ParsedAudioUpload(bool Success, byte[]? PcmAudio, string? Error)
    {
        public static ParsedAudioUpload Ok(byte[] pcmAudio) => new(true, pcmAudio, null);
        public static ParsedAudioUpload Fail(string error) => new(false, null, error);
    }
}
