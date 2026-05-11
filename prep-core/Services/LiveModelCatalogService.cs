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

    // F2a: pull-count is rendered like "114.1M" / "1.2K" / "1.5B" /
    // bare integer. Captured at the model-card level so every size
    // variant shares the same value (Ollama exposes pull counts
    // per-model, not per-tag).
    private static readonly Regex PullCountRegex = new(
        @"x-test-pull-count\b[^>]*>(?<count>[^<]+)<",
        RegexOptions.Compiled);

    // C5: ollama.com renders relative dates like "yesterday",
    // "2 days ago", "3 weeks ago", "1 year ago" in an x-test-updated
    // span per model card. Anchored at the card so size variants
    // share the same value (Ollama exposes the date per-model).
    private static readonly Regex UpdatedRegex = new(
        @"x-test-updated\b[^>]*>(?<updated>[^<]+)<",
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

            var pullCount = PullCountRegex.Match(cardBody) is { Success: true } pullMatch
                ? ParsePullCount(DecodeHtmlText(pullMatch.Groups["count"].Value))
                : null;

            var lastUpdated = UpdatedRegex.Match(cardBody) is { Success: true } updMatch
                ? ParseRelativeDate(DecodeHtmlText(updMatch.Groups["updated"].Value), DateTimeOffset.UtcNow)
                : null;

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
                    PullCount = pullCount,
                    LastUpdated = lastUpdated,
                    ParametersBillion = ParseParamsBillions(size),
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
    /// Parses a pull-count string like "114.1M", "1.2K", "1.5B", or a
    /// bare integer "999" into an approximate count. Returns null on
    /// empty or unparseable input — callers treat that as "unknown
    /// popularity" and exclude the entry from the F2a top-N filter.
    /// </summary>
    internal static long? ParsePullCount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return null;

        var lastChar = trimmed[^1];
        var multiplier = char.ToLowerInvariant(lastChar) switch
        {
            'k' => 1_000L,
            'm' => 1_000_000L,
            'b' => 1_000_000_000L,
            _ => 1L,
        };
        var numericPart = multiplier == 1L ? trimmed : trimmed[..^1];

        if (!double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }
        if (value < 0) return null;

        return (long)Math.Round(value * multiplier);
    }

    /// <summary>
    /// Parses ollama.com/library's <c>x-test-updated</c> relative-date
    /// strings ("yesterday", "2 days ago", "3 weeks ago", "1 year ago")
    /// into an approximate <see cref="DateTimeOffset"/> anchored to
    /// <paramref name="now"/>. Months and years use 30/365-day
    /// approximations because the only consumer is ordinal sort
    /// ("Newest" picker mode); calendar accuracy is unnecessary.
    /// Returns null on empty or unparseable input — callers treat that
    /// as "unknown date" and the entry sorts last under newest-first.
    /// </summary>
    internal static DateTimeOffset? ParseRelativeDate(string? raw, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().ToLowerInvariant();
        if (trimmed == "yesterday") return now.AddDays(-1);
        if (trimmed == "today" || trimmed == "just now") return now;

        // Match "<n> <unit>[s] ago" — the only template ollama.com emits.
        var match = RelativeDateRegex.Match(trimmed);
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            || n < 0)
        {
            return null;
        }

        var unit = match.Groups["unit"].Value;
        return unit switch
        {
            "second" => now.AddSeconds(-n),
            "minute" => now.AddMinutes(-n),
            "hour" => now.AddHours(-n),
            "day" => now.AddDays(-n),
            "week" => now.AddDays(-7L * n),
            "month" => now.AddDays(-30L * n),
            "year" => now.AddDays(-365L * n),
            _ => null,
        };
    }

    private static readonly Regex RelativeDateRegex = new(
        @"^(?<n>\d+)\s+(?<unit>second|minute|hour|day|week|month|year)s?\s+ago$",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts a numeric parameter count in billions from the size
    /// token ("8b" → 8.0, "1.5b" → 1.5, "335m" → 0.335). Mixture-of-
    /// experts notation like "128x17b" returns the larger component
    /// (128) so the C3 parameter cap excludes MoE models from
    /// memory-constrained buckets — they nominally load every expert
    /// even though only one fires per token, and surfacing them under
    /// "≤14B" would mislead users sizing for VRAM.
    /// Returns null on empty or unparseable input.
    /// </summary>
    internal static double? ParseParamsBillions(string? sizeToken)
    {
        if (string.IsNullOrWhiteSpace(sizeToken)) return null;

        var trimmed = sizeToken.Trim().ToLowerInvariant();
        var unit = trimmed[^1];
        if (unit != 'b' && unit != 'm') return null;

        var numericPart = trimmed[..^1];
        if (numericPart.Length == 0) return null;

        // MoE notation "AxB" — pick the larger of the two numbers and
        // interpret it as the same unit as the trailing letter.
        if (numericPart.Contains('x'))
        {
            var parts = numericPart.Split('x', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;
            double largest = 0.0;
            foreach (var part in parts)
            {
                if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                    || p < 0)
                {
                    return null;
                }
                if (p > largest) largest = p;
            }
            return unit == 'm' ? largest / 1000.0 : largest;
        }

        if (!double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            return null;
        }
        return unit == 'm' ? value / 1000.0 : value;
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
