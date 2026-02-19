using System.Text.RegularExpressions;

namespace FreeAiSsd.Shared;

/// <summary>
/// Hardware requirement estimates for a specific model, including RAM,
/// VRAM, and disk space needed. Used to warn users before pulling
/// models that may not run well on their hardware.
/// </summary>
public sealed record ModelSizing(
    string? ModelTagPattern,
    string? ModelTag,
    string? DisplayName,
    int MinSystemRamGb,
    int RecommendedSystemRamGb,
    int? MinVramGb,
    int? RecommendedVramGb,
    int ApproxDiskGb,
    string Notes);

/// <summary>
/// Provides hardware sizing estimates for LLM models based on their tag names.
/// Uses a built-in catalog for known models and falls back to heuristic sizing
/// by parsing the parameter count (e.g., "7b") from unknown model tags.
/// </summary>
public static class ModelSizingCatalog
{
    /// <summary>
    /// Built-in sizing data for well-known models and wildcard size tiers.
    /// Entries are matched by exact model tag first, then by parameter count heuristics.
    /// </summary>
    private static readonly IReadOnlyList<ModelSizing> BuiltIn = new List<ModelSizing>
    {
        new(null, "llama3.2:1b", "Llama 3.2 1B", 4, 8, null, null, 2, "Small starter model; CPU-only is possible but slower."),
        new(null, "llama3.2:3b", "Llama 3.2 3B", 8, 16, 4, 6, 4, "Good quality/speed balance on mainstream laptops."),
        new(null, "qwen2.5:3b", "Qwen 2.5 3B", 8, 16, 4, 6, 4, "Starter-friendly model for mixed hardware."),
        new(null, "qwen2.5:7b", "Qwen 2.5 7B", 12, 16, 6, 8, 8, "Common mid-size model; benefits from stronger GPU."),
        new(null, "mistral:7b", "Mistral 7B", 12, 16, 6, 8, 8, "Widely used 7B class model."),
        new(null, "*:1b", "~1B class model", 4, 8, null, null, 2, "Heuristic sizing for unknown 1B tags."),
        new(null, "*:3b", "~3B class model", 8, 16, 4, 6, 4, "Heuristic sizing for unknown 3B tags."),
        new(null, "*:7b", "~7B class model", 12, 16, 6, 8, 8, "Heuristic sizing for unknown 7B tags."),
        new(null, "*:8b", "~8B class model", 12, 16, 6, 8, 10, "Heuristic sizing for unknown 8B tags."),
        new(null, "*:13b", "~13B class model", 16, 32, 10, 12, 18, "Heuristic sizing for unknown 13B tags."),
        new(null, "*:14b", "~14B class model", 16, 32, 10, 12, 20, "Heuristic sizing for unknown 14B tags."),
        new(null, "*:30b", "~30B+ class model", 32, 64, 20, 24, 40, "Heuristic sizing for unknown 30B+ tags.")
    };

    /// <summary>
    /// Suggests hardware requirements for a given model tag. Tries exact match
    /// against the built-in catalog first, then falls back to parsing the parameter
    /// count suffix (e.g., ":7b") and applying size-tier heuristics.
    /// </summary>
    /// <param name="modelTag">The Ollama model tag (e.g., "llama3.2:3b").</param>
    /// <returns>Sizing estimates for the model, possibly heuristic-based.</returns>
    public static ModelSizing Suggest(string modelTag)
    {
        if (string.IsNullOrWhiteSpace(modelTag))
        {
            return BuildFallback("unknown", 12, 16, 6, 8, 10, "Unknown model size; medium heuristic applied.");
        }

        var normalized = modelTag.Trim();

        // Try exact match against known model tags.
        var exact = BuiltIn.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ModelTag) && string.Equals(x.ModelTag, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // Parse the parameter count from the tag suffix (e.g., ":7b" → 7).
        var billions = TryParseBillions(normalized);
        if (billions is null)
        {
            return BuildFallback(normalized, 12, 16, 6, 8, 10, "Could not parse parameter size; medium heuristic applied.");
        }

        // Apply size-tier heuristics based on parameter count.
        if (billions <= 3)
        {
            return BuildFallback(normalized, 8, 16, 4, 6, 4, "1B-3B heuristic sizing.");
        }

        if (billions <= 8)
        {
            return BuildFallback(normalized, 12, 16, 6, 8, 10, "7B-8B heuristic sizing.");
        }

        if (billions <= 14)
        {
            return BuildFallback(normalized, 16, 32, 10, 12, 20, "13B-14B heuristic sizing.");
        }

        if (billions >= 30)
        {
            return BuildFallback(normalized, 32, 64, 20, 24, 40, "30B+ heuristic sizing.");
        }

        return BuildFallback(normalized, 20, 32, 12, 16, 24, "General mid/high heuristic sizing.");
    }

    /// <summary>
    /// Creates a fallback ModelSizing record for models not in the built-in catalog.
    /// </summary>
    private static ModelSizing BuildFallback(string modelTag, int minRam, int recRam, int? minVram, int? recVram, int disk, string note)
        => new(null, modelTag, modelTag, minRam, recRam, minVram, recVram, disk, note);

    /// <summary>
    /// Extracts the parameter count (in billions) from a model tag suffix.
    /// Matches patterns like ":7b", ":13b", ":1b" (case-insensitive).
    /// </summary>
    private static int? TryParseBillions(string modelTag)
    {
        var match = Regex.Match(modelTag, @":(?<size>\d{1,3})b", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["size"].Value, out var value) ? value : null;
    }
}
