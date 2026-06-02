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
        var (promptToSend, sources, usedContext, ragError) = await PrepareRagContextAsync(userPrompt, host, config);

        var request = BuildGenerateRequest(model, promptToSend, stream: false, config);

        try
        {
            using var response = await _http.PostAsJsonAsync($"http://{host}/api/generate", request);
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
        var (promptToSend, sources, usedContext, ragError) = await PrepareRagContextAsync(userPrompt, host, config);

        var request = BuildGenerateRequest(model, promptToSend, stream: true, config);

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

            using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync(cancellationToken);
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
                                await onToken(token);
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
                await onToken($"\n\n[Error: {ex.Message}]");
            }
            return new ChatResult.Failure(SanitizeError(ex));
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeatTask; } catch { /* heartbeat task swallows its own cancel */ }
        }
    }

    private async Task<(string Prompt, List<string>? Sources, bool UsedContext, string? RagError)> PrepareRagContextAsync(
        string userPrompt, string host, PortableConfig config)
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
                var queryEmbedding = await embedder.EmbedAsync(host, config.EmbeddingModelName, userPrompt);
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
                var rag = RagPromptBuilder.Build(userPrompt, results, maxContextChars: 4500, librarySearched: true);

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
        string model, string prompt, bool stream, PortableConfig config)
    {
        var request = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["prompt"] = prompt,
            ["stream"] = stream
        };

        var options = BuildOllamaOptions(config);
        if (options.Count > 0)
        {
            request["options"] = options;
        }

        return request;
    }

    internal static Dictionary<string, object?> BuildOllamaOptions(PortableConfig config)
    {
        var options = new Dictionary<string, object?>();
        if (config.ModelContextWindow > 0)
        {
            options["num_ctx"] = config.ModelContextWindow;
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

    private static string SanitizeError(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } =>
            "Chat service unreachable — is Ollama running?",
        HttpRequestException => $"Chat request failed: {ex.Message}",
        TaskCanceledException => "Chat request timed out.",
        _ => "Chat service error."
    };
}
