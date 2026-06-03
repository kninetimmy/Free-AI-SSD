using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Runner.Services;

public sealed class ChatService : IChatService
{
    /// <summary>
    /// C1: heartbeat cadence while waiting for Ollama's first streamed token.
    /// 20s gives ~9 ticks before Mac URLSession's 180s per-packet timer would fire,
    /// keeping the chat stream alive across cold-loads.
    /// </summary>
    public const int HeartbeatIntervalSeconds = 20;

    private readonly HttpClient _http;
    private readonly DocumentLibraryManager _libraryManager;
    private readonly SsdLogger? _logger;

    /// <summary>Per-(host, model) cache of the model's trained context length from /api/show.</summary>
    private static readonly ConcurrentDictionary<string, int> _trainedContextCache = new();

    public ChatService(HttpClient http, DocumentLibraryManager libraryManager, SsdLogger? logger)
    {
        _http = http;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public event Action<string>? LogMessage;
    public event Action<int>? FirstTokenPending;

    public async Task<ChatResult> SendPromptAsync(
        string model, string userPrompt, string host, PortableConfig config)
    {
        var effectiveCtx = await ResolveEffectiveContextWindowAsync(host, model, config);
        var (promptToSend, sources, usedContext, ragError) = await PrepareRagContextAsync(userPrompt, host, config, effectiveCtx);

        var request = BuildGenerateRequest(model, promptToSend, stream: false, config, effectiveCtx);

        try
        {
            using var response = await _http.PostAsJsonAsync($"http://{host}/api/generate", request);
            var thinkError = await DetectThinkUnsupportedAsync(response, config);
            if (thinkError is not null) return new ChatResult.Failure(thinkError);
            response.EnsureSuccessStatusCode();

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var text = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;
            var chatResponse = new ChatResponse(text, sources, usedContext);
            return ragError is not null
                ? new ChatResult.RagRetrievalFailed(chatResponse, ragError)
                : new ChatResult.Success(chatResponse);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Generate failed: {ex.Message}");
            return new ChatResult.Failure(SanitizeError(ex));
        }
    }

    public async Task<ChatResult> SendPromptStreamingAsync(
        string model, string userPrompt, string host, PortableConfig config,
        Func<string, Task> onToken, CancellationToken cancellationToken = default)
    {
        var effectiveCtx = await ResolveEffectiveContextWindowAsync(host, model, config, cancellationToken).ConfigureAwait(false);
        var (promptToSend, sources, usedContext, ragError) = await PrepareRagContextAsync(userPrompt, host, config, effectiveCtx).ConfigureAwait(false);

        var request = BuildGenerateRequest(model, promptToSend, stream: true, config, effectiveCtx);

        var assembled = new StringBuilder();
        var requestStart = DateTimeOffset.UtcNow;
        var firstTokenSeen = 0;
        _logger?.Info($"chat stream begin (model={model}, host={host})");

        // C1: heartbeat task ticks every HeartbeatIntervalSeconds until the first
        // token arrives. Each tick raises FirstTokenPending so the API layer can
        // emit a `loading` NDJSON frame (resets the Mac URLSession 180s timer)
        // and the WPF UI can paint a "Loading model… NNs" indicator. Linked to
        // the caller's CT so Lock / cancel tears it down promptly.
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!heartbeatCts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), heartbeatCts.Token);
                    if (Volatile.Read(ref firstTokenSeen) != 0) return;
                    var elapsed = (int)(DateTimeOffset.UtcNow - requestStart).TotalSeconds;
                    try { FirstTokenPending?.Invoke(elapsed); }
                    catch (Exception handlerEx)
                    {
                        // Subscriber faults must never abort the chat. Log once and continue.
                        _logger?.Warn($"FirstTokenPending handler threw: {handlerEx.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, heartbeatCts.Token);

        try
        {
            var json = JsonSerializer.Serialize(request);
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"http://{host}/api/generate")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            // ConfigureAwait(false) throughout the read loop keeps it off the UI thread.
            // The WPF caller invokes this on the dispatcher; without it, every per-token
            // continuation resumes on the UI thread and the Normal-priority token appends
            // starve the lower-priority render pass, so the whole answer paints at once
            // when the stream ends. Off the UI thread, each onToken's Dispatcher.InvokeAsync
            // posts a small append the otherwise-idle UI thread renders incrementally.
            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var thinkError = await DetectThinkUnsupportedAsync(response, config, cancellationToken).ConfigureAwait(false);
            if (thinkError is not null) return new ChatResult.Failure(thinkError);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("response", out var tokenElement))
                    {
                            var token = tokenElement.GetString() ?? string.Empty;
                            if (token.Length > 0)
                            {
                                if (Interlocked.Exchange(ref firstTokenSeen, 1) == 0)
                                {
                                    var elapsedMs = (int)(DateTimeOffset.UtcNow - requestStart).TotalMilliseconds;
                                    _logger?.Info($"chat first-token in {elapsedMs}ms (model={model})");
                                }
                                assembled.Append(token);
                                await onToken(token).ConfigureAwait(false);
                            }
                        }
                }
                catch (JsonException)
                {
                    // Skip malformed chunks
                }
            }

            _logger?.Info($"chat stream complete in {(int)(DateTimeOffset.UtcNow - requestStart).TotalMilliseconds}ms (model={model})");
            var chatResponse = new ChatResponse(assembled.ToString(), sources, usedContext);
            return ragError is not null
                ? new ChatResult.RagRetrievalFailed(chatResponse, ragError)
                : new ChatResult.Success(chatResponse);
        }
        catch (OperationCanceledException)
        {
            _logger?.Info($"chat stream cancelled after {(int)(DateTimeOffset.UtcNow - requestStart).TotalMilliseconds}ms (model={model})");
            LogMessage?.Invoke("Generation cancelled by user.");
            return new ChatResult.Success(new ChatResponse(assembled.ToString(), sources, usedContext));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"chat stream failed after {(int)(DateTimeOffset.UtcNow - requestStart).TotalMilliseconds}ms (model={model}): {ex.Message}");
            LogMessage?.Invoke($"Streaming failed: {ex.Message}");
            var partial = assembled.ToString();
            if (partial.Length > 0)
            {
                await onToken($"\n\n[Error: {ex.Message}]").ConfigureAwait(false);
            }
            return new ChatResult.Failure(SanitizeError(ex));
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask.ConfigureAwait(false); } catch { /* heartbeat task swallows its own cancel */ }
        }
    }

    private async Task<(string Prompt, List<string>? Sources, bool UsedContext, string? RagError)> PrepareRagContextAsync(
        string userPrompt, string host, PortableConfig config, int effectiveContextWindow)
    {
        var promptToSend = userPrompt;
        List<string>? sources = null;
        var usedContext = false;

        if (!string.IsNullOrWhiteSpace(config.ActiveDocumentLibraryId))
        {
            try
            {
                var manifest = _libraryManager.LoadManifest(config.ActiveDocumentLibraryId);
                var index = new VectorIndex(_libraryManager.GetIndexPath(manifest.Id));
                var embedder = new EmbeddingClient(_http);
                // Pin num_ctx on the query embed too so switching between ingest and chat
                // doesn't make Ollama reload the embed model to resize its context.
                var embedNumCtx = config.EmbeddingContextTokens > 0 ? config.EmbeddingContextTokens : (int?)null;
                var embedMaxChars = config.EmbeddingMaxInputChars > 0 ? config.EmbeddingMaxInputChars : (int?)null;
                var queryEmbedding = await embedder.EmbedAsync(
                    host, config.EmbeddingModelName, userPrompt, default, embedNumCtx, embedMaxChars);
                index.CheckProvenance(manifest.Id, config.EmbeddingModelName, queryEmbedding.Length, _logger);
                var results = config.HybridRetrievalEnabled
                    ? index.SearchHybrid(manifest.Id, queryEmbedding, userPrompt, config.RetrievalTopK,
                        config.MinimumSimilarityThreshold, _logger)
                    : index.Search(manifest.Id, queryEmbedding, config.RetrievalTopK,
                        config.MinimumSimilarityThreshold, _logger);
                if (config.RetrievalNeighborRadius > 0 && results.Count > 0)
                {
                    results = index.ExpandNeighbors(manifest.Id, results, config.RetrievalNeighborRadius);
                }
                // Token-aware budget: size the reference context to the active model's
                // context window (minus reserved output) instead of a fixed char count.
                // TokenBudget converts back to a char budget for the char-based packer —
                // equivalent under a constant chars/token ratio. See TokenBudget for why
                // we estimate rather than tokenize. effectiveContextWindow is clamped to the
                // model's trained context so we don't pack a prompt Ollama would then truncate.
                var contextTokens = TokenBudget.ContextTokenBudget(effectiveContextWindow, config.ModelMaxOutputTokens);
                var rag = RagPromptBuilder.Build(userPrompt, results,
                    maxContextChars: TokenBudget.CharsForTokens(contextTokens), librarySearched: true);

                if (rag.UsedContext)
                {
                    promptToSend = rag.Prompt;
                    sources = rag.Sources;
                    usedContext = true;
                }
                else if (results.Count == 0)
                {
                    promptToSend = rag.Prompt;
                    LogMessage?.Invoke("No documents met the similarity threshold.");
                }
            }
            catch (EmbeddingModelMismatchException ex)
            {
                LogMessage?.Invoke($"[Error] RAG retrieval failed: {ex.Message}");
                return (promptToSend, null, false, ex.Message);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"RAG retrieval failed: {ex.Message}");
                return (promptToSend, null, false, ex.Message);
            }
        }

        return (promptToSend, sources, usedContext, null);
    }

    /// <summary>
    /// Builds the Ollama <c>/api/generate</c> request body. The <c>options</c>
    /// sub-object is only included when the user has overridden at least one
    /// model-parameter slider away from its sentinel. Keys are only emitted for
    /// non-sentinel values, so untouched sliders preserve each model's compiled-in
    /// defaults rather than forcing them to a single global value.
    /// </summary>
    internal static Dictionary<string, object?> BuildGenerateRequest(
        string model, string prompt, bool stream, PortableConfig config, int? contextWindowOverride = null)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["stream"] = stream
        };

        var options = BuildOllamaOptions(config, contextWindowOverride);
        if (options.Count > 0)
        {
            request["options"] = options;
        }

        // `think` is a top-level field, NOT an Ollama option. Only emitted when
        // the user has set it away from the default sentinel — omitting it lets
        // every model (thinking or not) behave exactly as it always has.
        if (TryGetThinkValue(config, out var think))
        {
            request["think"] = think;
        }

        return request;
    }

    /// <summary>
    /// Maps <see cref="PortableConfig.ModelThinkMode"/> to Ollama's top-level
    /// <c>think</c> value. <c>"off"</c> → <c>false</c>; <c>"low"/"medium"/"high"</c> →
    /// the level string. Empty/unrecognized → not set (omit the field).
    /// </summary>
    internal static bool TryGetThinkValue(PortableConfig config, out object? value)
    {
        switch (config.ModelThinkMode?.Trim().ToLowerInvariant())
        {
            case "off":
                value = false;
                return true;
            case "low":
            case "medium":
            case "high":
                value = config.ModelThinkMode!.Trim().ToLowerInvariant();
                return true;
            default:
                value = null;
                return false;
        }
    }

    internal static Dictionary<string, object?> BuildOllamaOptions(PortableConfig config, int? contextWindowOverride = null)
    {
        var options = new Dictionary<string, object?>();
        // Prefer the resolved effective window (clamped to the model's trained context)
        // over the raw config value, which can exceed what the model supports (e.g. a
        // 32768 config against llama3:8b's 8192) — Ollama would clamp + warn, and a prompt
        // packed for the larger window would be silently truncated.
        var numCtx = contextWindowOverride ?? config.ModelContextWindow;
        if (numCtx > 0)
        {
            options["num_ctx"] = numCtx;
        }
        if (config.ModelTemperature >= 0)
        {
            options["temperature"] = config.ModelTemperature;
        }
        if (config.ModelTopP >= 0)
        {
            options["top_p"] = config.ModelTopP;
        }
        if (config.ModelMaxOutputTokens >= 0)
        {
            options["num_predict"] = config.ModelMaxOutputTokens;
        }
        return options;
    }

    /// <summary>
    /// The context window to actually request: the smaller of the configured
    /// <see cref="PortableConfig.ModelContextWindow"/> and the model's trained context length
    /// (from /api/show). Asking for more than the model trained on makes Ollama clamp and
    /// emit a "requested context size too large" warning, and over-budgets RAG packing so the
    /// prompt is truncated. Falls back to the configured value when /api/show is unavailable.
    /// </summary>
    private async Task<int> ResolveEffectiveContextWindowAsync(
        string host, string model, PortableConfig config, CancellationToken cancellationToken = default)
    {
        var requested = config.ModelContextWindow;
        if (requested <= 0) return requested;

        var trained = await GetTrainedContextLengthAsync(host, model, cancellationToken).ConfigureAwait(false);
        if (trained is > 0 && trained.Value < requested)
        {
            _logger?.Info($"Clamping context window {requested}→{trained.Value} for model '{model}' (trained limit).");
            return trained.Value;
        }
        return requested;
    }

    /// <summary>
    /// Reads the model's trained context length from Ollama's /api/show <c>model_info</c>
    /// (the arch-specific <c>*.context_length</c> key), cached per (host, model). Returns null
    /// on any failure so the caller falls back to the configured window.
    /// </summary>
    private async Task<int?> GetTrainedContextLengthAsync(string host, string model, CancellationToken cancellationToken)
    {
        var key = $"{host}/{model}";
        if (_trainedContextCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            using var response = await _http.PostAsJsonAsync(
                $"http://{host}/api/show", new { model }, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("model_info", out var info) &&
                info.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in info.EnumerateObject())
                {
                    if (prop.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase) &&
                        prop.Value.ValueKind == JsonValueKind.Number &&
                        prop.Value.TryGetInt32(out var ctx) && ctx > 0)
                    {
                        _trainedContextCache[key] = ctx;
                        return ctx;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Info($"Could not read trained context for model '{model}': {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// When a <c>think</c> value was sent and Ollama rejected the request,
    /// returns an actionable message telling the user the model can't honor the
    /// Thinking control. Returns null otherwise (success, or a failure unrelated
    /// to thinking) so the normal error path runs. Ollama 400s on <c>think</c>
    /// for any model that does not declare thinking support — including
    /// <c>think: false</c> — so this also catches the "Off" case.
    /// </summary>
    private async Task<string?> DetectThinkUnsupportedAsync(
        HttpResponseMessage response, PortableConfig config, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode) return null;
        if (!TryGetThinkValue(config, out _)) return null;

        string body;
        try { body = await response.Content.ReadAsStringAsync(cancellationToken); }
        catch { return null; }

        if (body.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Warn($"chat rejected think='{config.ModelThinkMode}' ({(int)response.StatusCode}): {body.Trim()}");
            return "This model doesn't support the Thinking control. Set Thinking to Default, " +
                   "or use Max output to cap a runaway reasoning model.";
        }
        return null;
    }

    private static string SanitizeError(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } =>
            "Chat service unreachable — is Ollama running?",
        HttpRequestException => $"Chat request failed: {ex.Message}",
        TaskCanceledException => "Chat request timed out.",
        _ => "Chat service error."
    };
}
