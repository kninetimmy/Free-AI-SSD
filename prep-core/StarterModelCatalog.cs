using System.Reflection;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Root object for the starter model catalog JSON, containing a schema version
/// for forward compatibility and a list of recommended starter models.
/// </summary>
public sealed record StarterModelCatalog
{
    public int SchemaVersion { get; init; }
    public List<StarterModelEntry> Models { get; init; } = new();
}

/// <summary>
/// A single entry in the starter model catalog, describing a recommended
/// LLM model with its parameter count, size tier, and typical use cases.
/// </summary>
public sealed record StarterModelEntry
{
    /// <summary>Ollama model tag (e.g., "llama3.2:3b").</summary>
    public string Tag { get; init; } = string.Empty;
    /// <summary>Human-readable parameter count (e.g., "3B").</summary>
    public string Params { get; init; } = string.Empty;
    /// <summary>Size classification: "Small", "Medium", or "Large".</summary>
    public string SizeTier { get; init; } = string.Empty;
    /// <summary>Brief description of the model's capabilities.</summary>
    public string Description { get; init; } = string.Empty;
    /// <summary>List of typical use cases (e.g., "chat", "code", "translation").</summary>
    public List<string> UseCases { get; init; } = new();
    /// <summary>
    /// Approximate pull count from ollama.com/library, populated by
    /// <c>LiveModelCatalogService</c>. Null for the bundled catalog
    /// (the curated 12-entry list pre-dates this field). The picker
    /// uses it to surface the F2a "Most popular" filter.
    /// </summary>
    public long? PullCount { get; init; }

    /// <summary>
    /// Approximate "last updated" timestamp scraped from
    /// ollama.com/library's <c>x-test-updated</c> span (e.g.
    /// "yesterday", "3 weeks ago"). Anchored to fetch time and
    /// approximate by design — used for ordinal "Sort by newest" only.
    /// Null for the bundled catalog (predates this field) and for
    /// entries whose updated string can't be parsed.
    /// </summary>
    public DateTimeOffset? LastUpdated { get; init; }

    /// <summary>
    /// Numeric parameter count in billions extracted from the size
    /// token (e.g. "8b" → 8.0, "335m" → 0.335, "128x17b" → 128.0 by
    /// the largest-billion-number-wins rule). Null when the size token
    /// is empty or unparseable, or for the bundled catalog (predates
    /// this field). Drives the C3 parameter-count cap filter.
    /// </summary>
    public double? ParametersBillion { get; init; }
}

/// <summary>
/// Result of loading the starter model catalog, including the catalog data
/// and an optional warning if the primary catalog file was unavailable
/// and the embedded fallback was used instead.
/// </summary>
public sealed record StarterModelCatalogLoadResult(StarterModelCatalog Catalog, string? Warning);

/// <summary>
/// Loads the starter model catalog from a JSON file on disk, falling back to
/// an embedded resource if the file is missing or corrupt. The catalog provides
/// a curated list of recommended models for first-time users.
///
/// Load priority:
/// 1. File at Resources/starter-models.json relative to the app directory.
/// 2. Embedded assembly resource (compiled into the binary as a fallback).
/// 3. Empty catalog with a warning (graceful degradation).
/// </summary>
public static class StarterModelCatalogLoader
{
    private const string RelativeCatalogPath = "Resources/starter-models.json";
    private const string EmbeddedCatalogResourceName = "FreeAiSsd.PrepApp.Resources.starter-models.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Loads the starter model catalog with fallback logic.
    /// Validates entries (requires non-empty tag, params, size tier, and description)
    /// and trims/deduplicates use case strings.
    /// </summary>
    /// <param name="baseDirectory">Application base directory to search for the catalog file.</param>
    /// <returns>Load result with the catalog and optional warning message.</returns>
    public static StarterModelCatalogLoadResult Load(string baseDirectory)
    {
        var warningReasons = new List<string>();
        var filePath = Path.Combine(baseDirectory, RelativeCatalogPath);

        // Attempt 1: Load from file on disk.
        if (File.Exists(filePath))
        {
            try
            {
                var fileCatalog = DeserializeCatalog(File.ReadAllText(filePath));
                if (fileCatalog is not null)
                {
                    return new StarterModelCatalogLoadResult(fileCatalog, null);
                }

                warningReasons.Add($"Catalog file is empty or invalid at '{filePath}'.");
            }
            catch (Exception ex)
            {
                warningReasons.Add($"Catalog file failed to parse at '{filePath}' ({ex.Message}).");
            }
        }
        else
        {
            warningReasons.Add($"Catalog file not found at '{filePath}'.");
        }

        // Attempt 2: Load from embedded assembly resource.
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedCatalogResourceName);
            if (stream is null)
            {
                warningReasons.Add($"Embedded fallback resource '{EmbeddedCatalogResourceName}' not found.");
            }
            else
            {
                using var reader = new StreamReader(stream);
                var fallbackCatalog = DeserializeCatalog(reader.ReadToEnd());
                if (fallbackCatalog is not null)
                {
                    var warning = "Starter catalog loaded from embedded fallback. " + string.Join(" ", warningReasons);
                    return new StarterModelCatalogLoadResult(fallbackCatalog, warning);
                }

                warningReasons.Add("Embedded fallback catalog is invalid.");
            }
        }
        catch (Exception ex)
        {
            warningReasons.Add($"Embedded fallback failed to load ({ex.Message}).");
        }

        // Attempt 3: Return empty catalog with accumulated warnings.
        var failedWarning = "Starter model catalog unavailable. Free-form model entry remains available. " + string.Join(" ", warningReasons);
        return new StarterModelCatalogLoadResult(new StarterModelCatalog { SchemaVersion = 1 }, failedWarning);
    }

    /// <summary>
    /// Deserializes and validates a catalog JSON string. Filters out entries
    /// with missing required fields and normalizes string values.
    /// </summary>
    private static StarterModelCatalog? DeserializeCatalog(string json)
    {
        var catalog = JsonSerializer.Deserialize<StarterModelCatalog>(json, JsonOptions);
        if (catalog is null)
        {
            return null;
        }

        var validModels = catalog.Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Tag)
                        && !string.IsNullOrWhiteSpace(m.Params)
                        && !string.IsNullOrWhiteSpace(m.SizeTier)
                        && !string.IsNullOrWhiteSpace(m.Description))
            .Select(m => m with
            {
                Tag = m.Tag.Trim(),
                Params = m.Params.Trim(),
                SizeTier = m.SizeTier.Trim(),
                Description = m.Description.Trim(),
                UseCases = m.UseCases
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();

        if (validModels.Count == 0)
        {
            return null;
        }

        return catalog with { Models = validModels };
    }
}
