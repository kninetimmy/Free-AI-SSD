using System.Net.Http;
using System.Text;
using System.Text.Json;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Streams a model pull from Ollama's HTTP API
/// (<c>POST /api/pull</c>) as a sequence of <see cref="OllamaPullProgress"/>
/// frames.
///
/// Replaces the prior MAC31 path that spawned <c>ollama pull</c> as a
/// subprocess and parsed its TUI stdout. The TUI rendering shifted
/// shape between Ollama versions (the v1.3.20 field test surfaced
/// <c>pulling &lt;hash&gt;: NN%</c> where MAC31 was anchored on
/// <c>pulling &lt;hash&gt;... NN%</c>); the JSON API is the canonical
/// source the CLI itself consumes, so a contract that's stable across
/// upstream releases.
///
/// The CT is honored at the HttpClient send + per-line read; cancelling
/// closes the connection and the server-side resumable state remains on
/// disk as <c>&lt;digest&gt;-partial-N</c> blobs (a subsequent pull
/// resumes naturally — same behavior as the CLI).
/// </summary>
public static class OllamaPullClient
{
    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Streams <c>POST {ollamaHost}/api/pull</c> for <paramref name="modelTag"/>,
    /// invoking <paramref name="onProgress"/> for each NDJSON frame the
    /// server emits. Throws on transport failure, non-2xx response, or
    /// any frame that carries an <c>"error"</c> field. A throwing
    /// <paramref name="onProgress"/> is contained so a misbehaving UI
    /// dispatcher cannot leave the response stream unread.
    /// </summary>
    /// <param name="ollamaHost">
    /// Host string in the format Ollama returns from its
    /// <c>OllamaServerHandle</c> (<c>127.0.0.1:NNNNN</c> with no scheme).
    /// A leading <c>http://</c>/<c>https://</c> is accepted and passed
    /// through; otherwise <c>http://</c> is prepended.
    /// </param>
    /// <param name="handler">
    /// Optional <see cref="HttpMessageHandler"/> seam for tests so the
    /// NDJSON consumer can be exercised without a real Ollama server.
    /// Production callers pass <c>null</c>; the default handler is used.
    /// </param>
    public static async Task PullAsync(
        string ollamaHost,
        string modelTag,
        Action<OllamaPullProgress> onProgress,
        CancellationToken ct,
        HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(ollamaHost))
            throw new ArgumentException("ollamaHost is required for HTTP pulls.", nameof(ollamaHost));
        if (string.IsNullOrWhiteSpace(modelTag))
            throw new ArgumentException("modelTag is required.", nameof(modelTag));

        var baseUrl = ollamaHost.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || ollamaHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? ollamaHost.TrimEnd('/')
            : $"http://{ollamaHost.TrimEnd('/')}";
        var pullUrl = $"{baseUrl}/api/pull";

        // Per-request timeout disabled — multi-GB pulls run for many
        // minutes and the caller's CT is the authoritative cancellation
        // surface. Without this, HttpClient's default 100 s timer would
        // abort mid-pull on slow connections.
        using var http = handler is null
            ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        var requestBody = JsonSerializer.Serialize(new { model = modelTag, stream = true }, RequestOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, pullUrl)
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            // Surface the server's body when present so callers see
            // Ollama's structured error (e.g. the MAC38 "412: requires
            // a newer version of Ollama" case) instead of a bare HTTP
            // status code.
            string? body = null;
            try { body = await response.Content.ReadAsStringAsync(ct); } catch { /* best-effort */ }
            var detail = string.IsNullOrWhiteSpace(body) ? string.Empty : $": {body.Trim()}";
            throw new InvalidOperationException(
                $"Ollama pull failed for {modelTag} ({(int)response.StatusCode} {response.ReasonPhrase}){detail}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 8192);

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var frame = TryParseFrame(line);
            if (frame is null) continue;

            if (!string.IsNullOrEmpty(frame.Error))
            {
                throw new InvalidOperationException($"Ollama pull failed for {modelTag}: {frame.Error}");
            }

            if (string.IsNullOrEmpty(frame.Status)) continue;

            try { onProgress(new OllamaPullProgress(frame.Status!, frame.Digest, frame.Total, frame.Completed)); }
            catch
            {
                // A misbehaving onProgress must not stop the consumer —
                // mirrors the prior ModelOperations.Consume invariant.
            }
        }
    }

    /// <summary>
    /// Parses one NDJSON line into the loose-shape DTO. Tolerant of
    /// missing fields and unknown extras; returns <c>null</c> only on
    /// malformed JSON so a stray non-JSON line (rare but possible if a
    /// proxy is in the path) doesn't abort the stream.
    /// </summary>
    private static PullFrame? TryParseFrame(string ndjsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(ndjsonLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var frame = new PullFrame();
            if (root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
                frame.Status = statusEl.GetString();
            if (root.TryGetProperty("digest", out var digestEl) && digestEl.ValueKind == JsonValueKind.String)
                frame.Digest = digestEl.GetString();
            if (root.TryGetProperty("total", out var totalEl) && totalEl.TryGetInt64(out var total))
                frame.Total = total;
            if (root.TryGetProperty("completed", out var completedEl) && completedEl.TryGetInt64(out var completed))
                frame.Completed = completed;
            if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String)
                frame.Error = errorEl.GetString();
            return frame;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class PullFrame
    {
        public string? Status { get; set; }
        public string? Digest { get; set; }
        public long? Total { get; set; }
        public long? Completed { get; set; }
        public string? Error { get; set; }
    }
}
