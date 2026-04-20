using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.RunnerCli;

public sealed class RunnerApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly Uri _baseUri;
    private readonly bool _ownsHttpClient;

    public RunnerApiClient(Uri baseUri, string? apiKey, HttpClient? httpClient = null)
    {
        _baseUri = baseUri ?? throw new ArgumentNullException(nameof(baseUri));
        if (_baseUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Runner URL must use http or https.", nameof(baseUri));
        }

        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient();

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
    }

    public Uri BaseUri => _baseUri;

    public async Task<HealthResponse> GetHealthAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(Combine("/api/health"), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<HealthResponse>(JsonOptions, ct))
            ?? throw new InvalidOperationException("Empty health response.");
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(Combine("/api/models"), ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ModelsResponse>(JsonOptions, ct);
        return body?.Models ?? Array.Empty<string>();
    }

    public async Task<ChatResponse> ChatAsync(string model, string prompt, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            Combine("/api/chat"),
            new ChatRequest(model, prompt),
            JsonOptions,
            ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(await ReadErrorBodyAsync(response), null, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChatResponse>(JsonOptions, ct))
            ?? throw new InvalidOperationException("Empty chat response.");
    }

    public async IAsyncEnumerable<StreamEvent> ChatStreamAsync(
        string model,
        string prompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Combine("/api/chat/stream"))
        {
            Content = JsonContent.Create(new ChatRequest(model, prompt), options: JsonOptions),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            StreamEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<StreamEvent>(line, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Malformed NDJSON from server: {line}", ex);
            }

            if (evt is null)
            {
                continue;
            }

            yield return evt;
        }
    }

    private async Task<string> ReadErrorBodyAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("detail", out var detail))
                return detail.GetString() ?? response.ReasonPhrase ?? "Unknown error";
        }
        catch { }
        return response.ReasonPhrase ?? "Unknown error";
    }

    private Uri Combine(string path) => new(_baseUri, path);

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    public sealed record ChatRequest(string Model, string Prompt);

    public sealed record ChatResponse(
        string ResponseText,
        IReadOnlyList<string> Sources,
        bool UsedRagContext);

    public sealed record HealthResponse(
        string Status,
        bool NetworkModeEnabled,
        bool RequireApiKey,
        bool OllamaRunning,
        DateTime TimestampUtc);

    private sealed record ModelsResponse([property: JsonPropertyName("models")] IReadOnlyList<string> Models);

    public sealed record StreamEvent(
        string Type,
        string? Model = null,
        string? Token = null,
        bool UsedRagContext = false,
        IReadOnlyList<string>? Sources = null,
        string? ResponseText = null,
        string? Message = null);
}
