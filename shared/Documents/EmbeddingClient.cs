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
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: cancellationToken);
        var embedding = body?.Embeddings?.FirstOrDefault();
        if (embedding is null)
        {
            throw new InvalidOperationException("No embedding returned from Ollama. Ensure embedding model is installed.");
        }

        return embedding;
    }

    private sealed class EmbedResponse
    {
        public List<float[]>? Embeddings { get; set; }
    }
}
