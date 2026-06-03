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
    /// batch request fails. <paramref name="numCtx"/> pins Ollama's <c>options.num_ctx</c>
    /// and <paramref name="maxInputChars"/> clips the embedded text (see <see cref="EmbedBatchAsync"/>).
    /// </summary>
    public async Task<float[]> EmbedAsync(string host, string model, string input, CancellationToken cancellationToken = default, int? numCtx = null, int? maxInputChars = null)
    {
        var embeddings = await PostEmbedAsync(host, model, Clip(input, maxInputChars), expectedCount: 1, numCtx, cancellationToken).ConfigureAwait(false);
        return embeddings[0];
    }

    /// <summary>
    /// Embeds many inputs in a single /api/embed request and returns the embeddings
    /// 1:1 in input order. Batching collapses the per-chunk HTTP round-trips that
    /// dominate ingestion of large documents. Ollama accepts <c>input</c> as an array
    /// and returns <c>embeddings</c> in the same order.
    /// <para>
    /// <paramref name="numCtx"/> (when set) pins <c>options.num_ctx</c> so Ollama keeps the
    /// embed runner at a fixed context instead of reloading the model to grow context for a
    /// token-dense input; with the request's <c>truncate=true</c> an over-long input is
    /// truncated to fit rather than erroring. <paramref name="maxInputChars"/> (when &gt; 0)
    /// clips each input string first as a coarse safety net.
    /// </para>
    /// </summary>
    public async Task<float[][]> EmbedBatchAsync(string host, string model, IReadOnlyList<string> inputs, CancellationToken cancellationToken = default, int? numCtx = null, int? maxInputChars = null)
    {
        if (inputs is null || inputs.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var clipped = new string[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            clipped[i] = Clip(inputs[i], maxInputChars);
        }

        return await PostEmbedAsync(host, model, clipped, inputs.Count, numCtx, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clips <paramref name="text"/> to <paramref name="maxChars"/> when set and exceeded.</summary>
    private static string Clip(string text, int? maxChars)
        => maxChars is { } m && m > 0 && text.Length > m ? text[..m] : text;

    /// <summary>
    /// Shared POST + parse for both shapes. <paramref name="input"/> is serialized by
    /// its runtime type — a string for single embed, a string[] for a batch.
    /// </summary>
    private async Task<float[][]> PostEmbedAsync(string host, string model, object input, int expectedCount, int? numCtx, CancellationToken cancellationToken)
    {
        // truncate=true (Ollama default, set explicitly) makes an over-long input get
        // truncated to the model's context rather than returning an error; num_ctx pins
        // that context so Ollama never reloads the embed model to grow it for a dense chunk.
        object payload = numCtx is { } nc && nc > 0
            ? new { model, input, truncate = true, options = new { num_ctx = nc } }
            : new { model, input, truncate = true };

        using var response = await _http.PostAsJsonAsync($"http://{host}/api/embed", payload, cancellationToken).ConfigureAwait(false);

        // C2: surface the actual /api/embed error body instead of the generic
        // EnsureSuccessStatusCode message (e.g. `{"error":"model 'X' not found"}`),
        // so the ingestor's threshold-abort message can name the real cause.
        if (!response.IsSuccessStatusCode)
        {
            string body;
            try { body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
            catch { body = string.Empty; }

            var snippet = string.IsNullOrWhiteSpace(body) ? "(empty body)" : body.Trim();
            if (snippet.Length > 512) snippet = snippet[..512] + "…";

            throw new HttpRequestException(
                $"/api/embed returned {(int)response.StatusCode} {response.ReasonPhrase} for model '{model}': {snippet}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var parsed = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
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
