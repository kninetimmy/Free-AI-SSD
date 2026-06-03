using System.Net.Http.Json;

namespace FreeAiSsd.Shared.Documents;

public sealed class EmbeddingClient
{
    private readonly HttpClient _http;

    public EmbeddingClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Embeds a single input. Sends <c>input</c> as a JSON string (unchanged wire
    /// format) — used by the chat-query path and as the per-chunk fallback when a
    /// batch request fails.
    /// </summary>
    public async Task<float[]> EmbedAsync(string host, string model, string input, CancellationToken cancellationToken = default)
    {
        var embeddings = await PostEmbedAsync(host, model, input, expectedCount: 1, cancellationToken);
        return embeddings[0];
    }

    /// <summary>
    /// Embeds many inputs in a single /api/embed request and returns the embeddings
    /// 1:1 in input order. Batching collapses the per-chunk HTTP round-trips that
    /// dominate ingestion of large documents. Ollama accepts <c>input</c> as an array
    /// and returns <c>embeddings</c> in the same order.
    /// </summary>
    public async Task<float[][]> EmbedBatchAsync(string host, string model, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default)
    {
        if (inputs is null || inputs.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        return await PostEmbedAsync(host, model, inputs, inputs.Count, cancellationToken);
    }

    /// <summary>
    /// Shared POST + parse for both shapes. <paramref name="input"/> is serialized by
    /// its runtime type — a string for single embed, a string[] for a batch.
    /// </summary>
    private async Task<float[][]> PostEmbedAsync(string host, string model, object input, int expectedCount, CancellationToken cancellationToken)
    {
        var payload = new
        {
            model,
            input
        };

        using var response = await _http.PostAsJsonAsync($"http://{host}/api/embed", payload, cancellationToken);

        // C2: surface the actual /api/embed error body instead of the generic
        // EnsureSuccessStatusCode message (e.g. `{"error":"model 'X' not found"}`),
        // so the ingestor's threshold-abort message can name the real cause.
        if (!response.IsSuccessStatusCode)
        {
            string body;
            try { body = await response.Content.ReadAsStringAsync(cancellationToken); }
            catch { body = string.Empty; }

            var snippet = string.IsNullOrWhiteSpace(body) ? "(empty body)" : body.Trim();
            if (snippet.Length > 512) snippet = snippet[..512] + "…";

            throw new HttpRequestException(
                $"/api/embed returned {(int)response.StatusCode} {response.ReasonPhrase} for model '{model}': {snippet}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var parsed = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: cancellationToken);
        var embeddings = parsed?.Embeddings;
        if (embeddings is null || embeddings.Count != expectedCount)
        {
            throw new InvalidOperationException(
                $"/api/embed returned {embeddings?.Count ?? 0} embedding(s) for {expectedCount} input(s) with model '{model}'. " +
                "The model may be unavailable or the response shape changed.");
        }

        return embeddings.ToArray();
    }

    private sealed class EmbedResponse
    {
        public List<float[]>? Embeddings { get; set; }
    }
}
