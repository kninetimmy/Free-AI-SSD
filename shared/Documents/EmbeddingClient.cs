using System.Net.Http.Json;

namespace FreeAiSsd.Shared.Documents;

public sealed class EmbeddingClient
{
    private readonly HttpClient _http;

    public EmbeddingClient(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient();
    }

    public async Task<float[]> EmbedAsync(string host, string model, string input, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model,
            input
        };

        using var response = await _http.PostAsJsonAsync($"http://{host}/api/embed", payload, cancellationToken);

        // C2: surface the actual /api/embed error body instead of the
        // generic EnsureSuccessStatusCode message. Pre-C2 a missing
        // embedder returned a 404 with `{"error":"model 'X' not found"}`
        // — that body was discarded and DocumentIngestor's catch-all
        // bumped a counter, so the user saw "embedding failures
        // exceeded threshold" with no clue WHY. Mirrors MAC40's
        // OllamaPullClient body-on-error pattern so Stage 2 of C2
        // makes the provisioning gap self-explanatory the next time
        // it happens.
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
        var embedding = parsed?.Embeddings?.FirstOrDefault();
        if (embedding is null)
        {
            throw new InvalidOperationException(
                $"/api/embed returned 200 but no embedding payload for model '{model}'. The model may be unavailable or the response shape changed.");
        }

        return embedding;
    }

    private sealed class EmbedResponse
    {
        public List<float[]>? Embeddings { get; set; }
    }
}
