using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.PrepApp.Services;

/// <summary>
/// C27 Stage 1: fetches a starter-model catalog from the Hugging Face
/// Search API. Mirrors <see cref="ILiveModelCatalogService"/>'s shape so
/// the PrepApp picker can flow Ollama and Hugging Face results through
/// the same projection path.
///
/// Anonymous read only in Stage 1 — token auth (private/gated repos)
/// defers to Stage 3 alongside encrypted-config storage of the token.
/// Pulls (Stage 2), per-quant variant surfacing + disk-budget warnings
/// (Stages 2 + 4) layer onto this catalog adapter.
/// </summary>
public interface IHuggingFaceCatalogService
{
    Task<HuggingFaceCatalogResult> SearchAsync(HuggingFaceSearchQuery query, CancellationToken ct);
}

/// <summary>C27 Stage 1: shape of an HF Search API call. Defaults mirror
/// the "popular GGUF" empty-search-box behavior the picker exposes when
/// the user toggles to the Hugging Face source.</summary>
public sealed record HuggingFaceSearchQuery(
    string? Search = null,
    int Limit = 50,
    string Sort = "downloads");

/// <summary>C27 Stage 1: result shape mirrors <see cref="LiveCatalogResult"/>
/// so the projection code path can stay one branch. <see cref="Query"/>
/// is the user-typed search string (null/empty = popular GGUF default),
/// surfaced back so empty-state captions can read "No GGUF repos match
/// '<query>'".</summary>
public sealed record HuggingFaceCatalogResult(
    StarterModelCatalog Catalog,
    DateTimeOffset FetchedAt,
    string SourceUrl,
    string? Query);

/// <summary>
/// HTTPS-only, allowlisted-host implementation against
/// <c>https://huggingface.co/api/models</c>. Same security posture as
/// <see cref="LiveModelCatalogService"/>: source URL is a code constant,
/// HTTPS-only check before the request leaves the process, CI grep can
/// audit the network surface.
///
/// Caching: short-lived in-memory cache keyed by (search, limit, sort).
/// Scoped to the lifetime of the service instance (which matches the
/// MainWindow / HostLifetime lifetime), so the cache resets on app
/// close. Persistent disk cache defers to Stage 2 alongside per-quant
/// <c>siblings[].size</c> pre-fetch.
///
/// Rate limit posture: HF documents ~1000 anonymous requests/hour. With
/// the picker's 350ms debounce on the search box, the worst case is a
/// brief 3 req/s burst — well inside the hourly budget. 429 surfaces as
/// <see cref="LiveCatalogFetchReason.NonSuccessStatus"/> with status
/// code "429" so the UI can render a "wait a minute and retry" caption.
/// </summary>
public sealed class HuggingFaceCatalogService : IHuggingFaceCatalogService, IDisposable
{
    /// <summary>
    /// HTTPS-only source allowlist. Code constant by design — adding a
    /// new source requires a PR (same posture as
    /// <see cref="LiveModelCatalogService.AllowedSources"/>). Any URL not
    /// in this list is refused before the request leaves the process.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedSources = new[]
    {
        "https://huggingface.co/api/models",
    };

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>C27 Stage 1: default page size. Larger pages invite
    /// rate-limit pressure without a UX payoff — the picker fits about
    /// 50 rows before scrolling cost dominates.</summary>
    public const int DefaultLimit = 50;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly string _sourceUrl;
    private readonly TimeSpan _timeout;
    private readonly string _userAgent;

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, HuggingFaceCatalogResult> _cache =
        new(StringComparer.Ordinal);

    public HuggingFaceCatalogService()
        : this(httpClient: null, sourceUrl: null, timeout: null, userAgent: null)
    {
    }

    public HuggingFaceCatalogService(
        HttpClient? httpClient,
        string? sourceUrl = null,
        TimeSpan? timeout = null,
        string? userAgent = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
        _sourceUrl = sourceUrl ?? AllowedSources[0];
        _timeout = timeout ?? DefaultTimeout;
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "Free-AI-SSD/1.0" : userAgent!;
    }

    public string SourceUrl => _sourceUrl;

    public async Task<HuggingFaceCatalogResult> SearchAsync(HuggingFaceSearchQuery query, CancellationToken ct)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));

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

        var cacheKey = BuildCacheKey(query);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var requestUrl = BuildRequestUrl(_sourceUrl, query);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        // HF returns 403 on missing UA for some endpoints; defensive
        // header keeps the request portable across HF's API tiers.
        request.Headers.UserAgent.TryParseAdd(_userAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, timeoutCts.Token);
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

        StarterModelCatalog catalog;
        try
        {
            catalog = ParseHuggingFaceResponse(body);
        }
        catch (JsonException ex)
        {
            throw new LiveCatalogFetchException(
                LiveCatalogFetchReason.SchemaDrift,
                $"Failed to parse Hugging Face response: {ex.Message}",
                ex);
        }

        var result = new HuggingFaceCatalogResult(
            catalog,
            DateTimeOffset.UtcNow,
            _sourceUrl,
            string.IsNullOrWhiteSpace(query.Search) ? null : query.Search);

        lock (_cacheLock)
        {
            _cache[cacheKey] = result;
        }

        return result;
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    // ── Request building ────────────────────────────────────────────────

    /// <summary>
    /// Builds the HF Search API URL. <c>filter=gguf</c> is server-side
    /// (catalog-layer GGUF filter per the C27 Stage 1 plan); the parser
    /// double-checks <c>tags</c> as defense-in-depth against HF's filter
    /// occasionally returning false positives.
    /// Internal for direct unit testing.
    /// </summary>
    internal static string BuildRequestUrl(string baseUrl, HuggingFaceSearchQuery query)
    {
        var sort = string.IsNullOrWhiteSpace(query.Sort) ? "downloads" : query.Sort;
        var limit = query.Limit <= 0 ? DefaultLimit : query.Limit;
        var sb = new System.Text.StringBuilder();
        sb.Append(baseUrl);
        sb.Append("?filter=gguf");
        sb.Append("&sort=");
        sb.Append(Uri.EscapeDataString(sort));
        sb.Append("&limit=");
        sb.Append(limit.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            sb.Append("&search=");
            sb.Append(Uri.EscapeDataString(query.Search!));
        }
        return sb.ToString();
    }

    private static string BuildCacheKey(HuggingFaceSearchQuery query)
        => string.Join('|',
            query.Search ?? string.Empty,
            (query.Limit <= 0 ? DefaultLimit : query.Limit).ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(query.Sort) ? "downloads" : query.Sort);

    // ── Parser ──────────────────────────────────────────────────────────

    /// <summary>
    /// DTO for HF's Search API response item. Only the fields the
    /// picker consumes — the API returns many more (id, modelId,
    /// author, sha, createdAt, etc.) that we don't need in Stage 1.
    /// </summary>
    private sealed class HuggingFaceApiModel
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("downloads")] public long? Downloads { get; set; }
        [JsonPropertyName("likes")] public long? Likes { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("lastModified")] public DateTimeOffset? LastModified { get; set; }
        [JsonPropertyName("pipeline_tag")] public string? PipelineTag { get; set; }
    }

    /// <summary>
    /// Parses the HF Search API JSON response into a
    /// <see cref="StarterModelCatalog"/>. One row per repo in Stage 1 —
    /// per-quant variant rows defer to Stage 4 which walks
    /// <c>siblings</c> (requires <c>?full=true</c>).
    /// Internal for direct unit testing against captured response
    /// fixtures.
    /// </summary>
    internal static StarterModelCatalog ParseHuggingFaceResponse(string json)
    {
        var apiModels = JsonSerializer.Deserialize<List<HuggingFaceApiModel>>(json, JsonOptions)
            ?? new List<HuggingFaceApiModel>();

        var entries = new List<StarterModelEntry>();
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var apiModel in apiModels)
        {
            if (apiModel is null) continue;

            var id = (apiModel.Id ?? string.Empty).Trim();
            if (id.Length == 0) continue;

            // Defensive: id must look like "owner/repo". Reject anything
            // with embedded slashes (HF's API never emits more than one)
            // or whitespace — they would round-trip through "hf.co/<id>"
            // into an unpullable tag.
            if (id.Contains(' ') || id.Count(c => c == '/') != 1) continue;
            var slashIdx = id.IndexOf('/');
            if (slashIdx <= 0 || slashIdx == id.Length - 1) continue;

            // C27 Stage 1 plan D2: catalog-layer GGUF filter applied
            // server-side via `?filter=gguf`; this is the defense-in-
            // depth pass — HF's filter has been observed to occasionally
            // return false positives. Repos must actually carry the
            // `gguf` tag.
            var tags = apiModel.Tags ?? new List<string>();
            var hasGgufTag = tags.Any(t => string.Equals(t, "gguf", StringComparison.OrdinalIgnoreCase));
            if (!hasGgufTag) continue;

            // HF returns the Ollama-pullable tag form by prefixing
            // `hf.co/`. Stage 1 doesn't pull (UI gates the download
            // button when source is HF); Stage 2 wires this through
            // ModelManagementService.PullModelAsync.
            var tag = "hf.co/" + id;
            if (!seenTags.Add(tag)) continue;

            entries.Add(new StarterModelEntry
            {
                Tag = tag,
                // Params left empty: HF doesn't expose Ollama-style
                // size suffixes per row. The C3 cap filter consults
                // ParametersBillion (best-effort from the repo id),
                // not this string.
                Params = string.Empty,
                // HF rows fall outside the bundled S/M/L taxonomy.
                // "Custom" matches the same posture configured/on-disk
                // rows use when there's no catalog metadata.
                SizeTier = "Custom",
                // No HF-side description in the cheap-payload Search API
                // response. Stage 2 may surface README excerpts via the
                // model-details endpoint.
                Description = string.Empty,
                // Capability tags don't have a 1:1 mapping on HF.
                // pipeline_tag carries a single class ("text-generation"
                // etc.) but doesn't match the picker's tools/vision/
                // thinking/audio vocabulary. Empty list = C25 pass-
                // through marker applies when the user narrows by
                // capability chip.
                UseCases = new List<string>(),
                PullCount = apiModel.Downloads,
                LastUpdated = apiModel.LastModified,
                ParametersBillion = TryExtractParamsFromRepoId(id),
                Source = ModelSource.HuggingFace,
            });
        }

        return new StarterModelCatalog
        {
            SchemaVersion = 1,
            Models = entries,
        };
    }

    /// <summary>
    /// Best-effort parameter-count extraction from HF repo IDs. Common
    /// patterns: <c>Qwen3-8B-GGUF</c>, <c>Llama-3.2-7B-Instruct</c>,
    /// <c>Mistral-7B-OpenOrca</c>, <c>Qwen3-30B-A3B-Instruct-GGUF</c>.
    /// Picks the first standalone "<n>B" or "<n>b" token; falls back to
    /// "<n>M" or "<n>m" for sub-billion models. Returns null on no match.
    ///
    /// MoE notation (e.g. <c>Mixtral-8x7B</c>) returns the larger
    /// component (8) so memory-constrained users sizing for VRAM aren't
    /// misled into seeing the model as a 7B entry under the <c>≤7B</c>
    /// cap — same posture as <see cref="LiveModelCatalogService.ParseParamsBillions"/>
    /// for ollama.com's "128x17b" tokens.
    /// </summary>
    internal static double? TryExtractParamsFromRepoId(string repoId)
    {
        if (string.IsNullOrWhiteSpace(repoId)) return null;

        // Strip the owner — params hints live in the repo name.
        var slashIdx = repoId.IndexOf('/');
        var name = slashIdx < 0 ? repoId : repoId[(slashIdx + 1)..];

        // MoE notation first ("AxB"): scan for digit-x-digit-letter and
        // pick the larger component. Captures "Mixtral-8x7B" → 8B,
        // "Qwen3-128x17b" → 128B.
        var moeMatch = MoeParamRegex.Match(name);
        if (moeMatch.Success)
        {
            if (double.TryParse(moeMatch.Groups["a"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                && double.TryParse(moeMatch.Groups["b"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
            {
                var larger = Math.Max(a, b);
                if (larger < 0) return null;
                var unitChar = char.ToLowerInvariant(moeMatch.Groups["unit"].Value[0]);
                return unitChar == 'm' ? larger / 1000.0 : larger;
            }
        }

        // Standalone "<n>B" or "<n>M" token bracketed by separators.
        var match = StandaloneParamRegex.Match(name);
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < 0)
        {
            return null;
        }
        var unit = char.ToLowerInvariant(match.Groups["unit"].Value[0]);
        return unit == 'm' ? value / 1000.0 : value;
    }

    // Standalone "<n>B" or "<n>M" — must be preceded by a separator
    // (start, dash, dot, underscore) and followed by a separator so we
    // don't catch "10B" inside a longer token like "10Billion".
    private static readonly Regex StandaloneParamRegex = new(
        @"(?:^|[-_\.])(?<n>\d+(?:\.\d+)?)(?<unit>[Bb]|[Mm])(?:$|[-_\.])",
        RegexOptions.Compiled);

    // MoE pattern: <n>x<n><B|M> — captures Mixtral-8x7B / 128x17b style.
    private static readonly Regex MoeParamRegex = new(
        @"(?:^|[-_\.])(?<a>\d+)x(?<b>\d+(?:\.\d+)?)(?<unit>[Bb]|[Mm])(?:$|[-_\.])",
        RegexOptions.Compiled);
}
