using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace FreeAiSsd.PrepApp.Services;

/// <summary>
/// Fetches a live starter-model catalog from a public source. Returns
/// the same <see cref="StarterModelCatalog"/> shape the bundled
/// JSON loader produces so callers can flow live and bundled data
/// through one projection path.
/// </summary>
public interface ILiveModelCatalogService
{
    Task<LiveCatalogResult> FetchAsync(CancellationToken ct);
}

public sealed record LiveCatalogResult(
    StarterModelCatalog Catalog,
    DateTimeOffset FetchedAt,
    string SourceUrl);

public enum LiveCatalogFetchReason
{
    UrlNotAllowed,
    Timeout,
    NetworkError,
    NonSuccessStatus,
    EmptyResponse,
    SchemaDrift,
}

public sealed class LiveCatalogFetchException : Exception
{
    public LiveCatalogFetchReason Reason { get; }
    public string? StatusCode { get; }

    public LiveCatalogFetchException(
        LiveCatalogFetchReason reason,
        string message,
        Exception? inner = null,
        string? statusCode = null)
        : base(message, inner)
    {
        Reason = reason;
        StatusCode = statusCode;
    }
}

/// <summary>
/// HTML-scrape implementation against ollama.com/library. The page is
/// server-rendered with stable <c>x-test-*</c> attributes designed as
/// test selectors, so regex against those holds up reasonably under
/// site refreshes — but the failure mode is graceful: the typed
/// <see cref="LiveCatalogFetchException"/> lets callers fall back to
/// the bundled catalog without the user losing their session.
///
/// HuggingFace was considered as the primary source but its model IDs
/// (e.g. <c>bartowski/Meta-Llama-3.1-8B-Instruct-GGUF</c>) are not
/// Ollama-pullable tags, which is what PrepApp's picker exists to
/// surface. Ollama publishes no JSON list API as of 2026-05-07.
/// </summary>
public sealed class LiveModelCatalogService : ILiveModelCatalogService, IDisposable
{
    /// <summary>
    /// HTTPS-only source allowlist. Code constant by design — adding a
    /// new source requires a PR (mirrors <c>OllamaPackageTrustPolicy</c>
    /// posture). Any URL not in this list is refused before the request
    /// leaves the process so CI grep can audit the network surface.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedSources = new[]
    {
        "https://ollama.com/library",
    };

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _sourceUrl;
    private readonly TimeSpan _timeout;

    public LiveModelCatalogService()
        : this(httpClient: null, sourceUrl: null, timeout: null)
    {
    }

    public LiveModelCatalogService(
        HttpClient? httpClient,
        string? sourceUrl = null,
        TimeSpan? timeout = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
        _sourceUrl = sourceUrl ?? AllowedSources[0];
        _timeout = timeout ?? DefaultTimeout;
    }

    public string SourceUrl => _sourceUrl;

    public async Task<LiveCatalogResult> FetchAsync(CancellationToken ct)
    {
        if (!AllowedSources.Contains(_sourceUrl, StringComparer.Ordinal))
        {
            throw new LiveCatalogFetchException(
                LiveCatalogFetchReason.UrlNotAllowed,
                $"Source URL not in allowlist: {_sourceUrl}");
        }

        if (!_sourceUrl.StartsWith("https://", StringComparison.Ordinal))
        {
            throw new LiveCatalogFetchException(
                LiveCatalogFetchReason.UrlNotAllowed,
                $"Source URL must be HTTPS: {_sourceUrl}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(_sourceUrl, timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new LiveCatalogFetchException(
                LiveCatalogFetchReason.Timeout,
                $"Request timed out after {_timeout.TotalSeconds}s",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new LiveCatalogFetchException(
                LiveCatalogFetchReason.NetworkError,
                $"Network error: {ex.Message}",
                ex);
        }

        string body;
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LiveCatalogFetchException(
                    LiveCatalogFetchReason.NonSuccessStatus,
                    $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}",
                    statusCode: ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                throw new LiveCatalogFetchException(
                    LiveCatalogFetchReason.NetworkError,
                    $"Failed to read response body: {ex.Message}",
                    ex);
            }
        }
        finally
        {
            response.Dispose();
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new LiveCatalogFetchException(
                LiveCatalogFetchReason.EmptyResponse,
                "Server returned empty body");
        }

        var catalog = ParseOllamaLibraryHtml(body);
        if (catalog.Models.Count == 0)
        {
            throw new LiveCatalogFetchException(
                LiveCatalogFetchReason.SchemaDrift,
                "Parsed 0 models from response — Ollama site structure may have changed");
        }

        return new LiveCatalogResult(catalog, DateTimeOffset.UtcNow, _sourceUrl);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    // ── Parser ──────────────────────────────────────────────────────────

    // Selectors anchor on x-test-* attributes Ollama exposes for testing —
    // see /tmp/ollama-library.html captured 2026-05-07. If Ollama redesigns
    // the catalog page these patterns will need a refresh; the typed
    // SchemaDrift failure is the visible signal.
    private static readonly Regex CardRegex = new(
        @"<li\s+x-test-model\b[^>]*>(?<body>.*?)</li>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HrefNameRegex = new(
        @"href=""/library/(?<name>[^""/]+)""",
        RegexOptions.Compiled);

    private static readonly Regex DescriptionRegex = new(
        @"<p\s+class=""max-w-lg[^""]*""[^>]*>(?<desc>[^<]*)</p>",
        RegexOptions.Compiled);

    private static readonly Regex SizeRegex = new(
        @"<span\s+x-test-size\b[^>]*>(?<size>[^<]+)</span>",
        RegexOptions.Compiled);

    private static readonly Regex CapabilityRegex = new(
        @"<span\s+x-test-capability\b[^>]*>(?<cap>[^<]+)</span>",
        RegexOptions.Compiled);

    /// <summary>
    /// Parses ollama.com/library HTML into a <see cref="StarterModelCatalog"/>.
    /// Emits one entry per (model, size) combination so the picker can
    /// surface 8b/70b/405b variants of the same model as separate rows
    /// (matches the existing bundled-catalog convention).
    ///
    /// Embedding-only models (capability == "embedding" and no other
    /// non-embedding capability) are skipped because the PrepApp picker
    /// is for chat starter models, not embedding indices.
    ///
    /// Internal for direct unit testing against captured HTML fixtures.
    /// </summary>
    internal static StarterModelCatalog ParseOllamaLibraryHtml(string html)
    {
        var entries = new List<StarterModelEntry>();
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match cardMatch in CardRegex.Matches(html))
        {
            var cardBody = cardMatch.Groups["body"].Value;

            var nameMatch = HrefNameRegex.Match(cardBody);
            if (!nameMatch.Success)
            {
                continue;
            }

            var name = nameMatch.Groups["name"].Value.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var description = DescriptionRegex.Match(cardBody) is { Success: true } descMatch
                ? DecodeHtmlText(descMatch.Groups["desc"].Value).Trim()
                : string.Empty;

            var capabilities = CapabilityRegex.Matches(cardBody)
                .Select(m => DecodeHtmlText(m.Groups["cap"].Value).Trim())
                .Where(c => c.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Skip pure-embedding entries — the PrepApp picker exists to
            // pull chat starter models, not embedding indices. A model
            // tagged both "embedding" and something else (rare) still gets
            // surfaced.
            var nonEmbeddingCapabilities = capabilities
                .Where(c => !c.Equals("embedding", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (capabilities.Count > 0 && nonEmbeddingCapabilities.Count == 0)
            {
                continue;
            }

            var sizes = SizeRegex.Matches(cardBody)
                .Select(m => DecodeHtmlText(m.Groups["size"].Value).Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // No size variants advertised → emit a single un-suffixed entry
            // so the model still appears (Ollama's `ollama pull <name>`
            // resolves to a default tag in that case).
            if (sizes.Count == 0)
            {
                sizes.Add(string.Empty);
            }

            foreach (var size in sizes)
            {
                var tag = string.IsNullOrEmpty(size) ? name : $"{name}:{size}";
                if (!seenTags.Add(tag))
                {
                    continue;
                }

                entries.Add(new StarterModelEntry
                {
                    Tag = tag,
                    Params = string.IsNullOrEmpty(size) ? string.Empty : size.ToUpperInvariant(),
                    SizeTier = ClassifySize(size),
                    Description = description,
                    UseCases = nonEmbeddingCapabilities,
                });
            }
        }

        return new StarterModelCatalog
        {
            SchemaVersion = 1,
            Models = entries,
        };
    }

    /// <summary>
    /// Maps a size token like "8b", "1.5b", "405b", "335m" to a tier
    /// label that matches the bundled catalog's vocabulary
    /// (Small / Medium / Large). Unknown / un-parseable sizes default
    /// to Medium so the model still surfaces with a reasonable group.
    /// </summary>
    internal static string ClassifySize(string size)
    {
        if (string.IsNullOrEmpty(size))
        {
            return "Medium";
        }

        var trimmed = size.Trim().ToLowerInvariant();
        var unit = trimmed[^1];
        var numericPart = trimmed[..^1];

        if (!double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return "Medium";
        }

        // Normalize to billions. "m" (millions) → < 1B; "b" (billions) → as-is.
        // Anything else → treat as billions for forgiveness.
        var billions = unit switch
        {
            'm' => value / 1000.0,
            'b' => value,
            _ => value,
        };

        if (billions < 3.0) return "Small";
        if (billions <= 13.0) return "Medium";
        return "Large";
    }

    /// <summary>
    /// Minimal HTML entity decode covering the entities Ollama's library
    /// page actually emits in description text (&amp;amp;, &amp;#39;,
    /// &amp;quot;, &amp;lt;, &amp;gt;). We deliberately avoid pulling in
    /// a full HTML parser dependency — these are the only entities
    /// observed in the captured fixture.
    /// </summary>
    private static string DecodeHtmlText(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        return raw
            .Replace("&amp;", "&")
            .Replace("&quot;", "\"")
            .Replace("&#39;", "'")
            .Replace("&apos;", "'")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">");
    }
}
