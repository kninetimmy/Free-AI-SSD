using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Runner.Services;

public sealed class ChatService : IChatService
{
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

    public async Task<ChatResult> SendPromptAsync(
        string model, string userPrompt, string host, PortableConfig config)
    {
        var (promptToSend, sources, usedContext, ragError) = await PrepareRagContextAsync(userPrompt, host, config);

        var request = new
        {
            model,
            prompt = promptToSend,
            stream = false
        };

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

        var request = new
        {
            model,
            prompt = promptToSend,
            stream = true
        };

        var assembled = new StringBuilder();

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

            var chatResponse = new ChatResponse(assembled.ToString(), sources, usedContext);
            return ragError is not null
                ? new ChatResult.RagRetrievalFailed(chatResponse, ragError)
                : new ChatResult.Success(chatResponse);
        }
        catch (OperationCanceledException)
        {
            LogMessage?.Invoke("Generation cancelled by user.");
            return new ChatResult.Success(new ChatResponse(assembled.ToString(), sources, usedContext));
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Streaming failed: {ex.Message}");
            var partial = assembled.ToString();
            if (partial.Length > 0)
            {
                await onToken($"\n\n[Error: {ex.Message}]");
            }
            return new ChatResult.Failure(SanitizeError(ex));
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
                var results = index.Search(manifest.Id, queryEmbedding, config.RetrievalTopK,
                    config.MinimumSimilarityThreshold, _logger);
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

    private static string SanitizeError(Exception ex) => ex switch
    {
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } =>
            "Chat service unreachable — is Ollama running?",
        HttpRequestException => $"Chat request failed: {ex.Message}",
        TaskCanceledException => "Chat request timed out.",
        _ => "Chat service error."
    };
}
