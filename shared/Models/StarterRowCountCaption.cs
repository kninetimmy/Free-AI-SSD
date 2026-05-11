namespace FreeAiSsd.Shared.Models;

/// <summary>
/// M11 + C3/C4/C5: caption that announces the picker's visible row count +
/// active filter reasons. M11 closed the original "Most popular doesn't
/// do anything" perception bug by surfacing the top-15 cap; C3/C4/C5
/// extend the same caption to cover the parameter cap, capability
/// filters, and sort mode so each new control's effect is visible too.
///
/// Returns empty when no filter, search, or non-default sort is active —
/// the catalog status text already reports the total in that case; only
/// changes from the default view need their own line.
///
/// Mirrors the Swift <c>formatStarterRowCountCaption</c> in
/// <c>mac-prep-app/Sources/StarterCatalogTypes.swift</c> — keep wording
/// byte-identical so cross-OS smoke testing reads matching strings.
/// </summary>
public static class StarterRowCountCaption
{
    public static string Format(
        int visible,
        int total,
        bool showOnlyMostPopular,
        bool hasSearch,
        double? maxParametersBillion = null,
        IReadOnlyCollection<string>? requiredCapabilities = null,
        ModelSortMode sortMode = ModelSortMode.Popular)
    {
        if (total == 0) return string.Empty;

        var extra = DescribeExtraFilters(maxParametersBillion, requiredCapabilities);
        var hasExtra = extra.Length > 0;

        var primary = (showOnlyMostPopular, hasSearch, hasExtra) switch
        {
            (true,  true,  true)  => $"Showing top {visible} of {total} by pulls (filtered by search; {extra}).",
            (true,  true,  false) => $"Showing top {visible} of {total} by pulls (filtered by search).",
            (true,  false, true)  => $"Showing top {visible} of {total} by pulls ({extra}).",
            (true,  false, false) => $"Showing top {visible} of {total} by pulls.",
            (false, true,  true)  => $"Showing {visible} of {total} matching search ({extra}).",
            (false, true,  false) => $"Showing {visible} of {total} matching search.",
            (false, false, true)  => $"Showing {visible} of {total} matching filter ({extra}).",
            (false, false, false) => string.Empty,
        };

        var sortSuffix = sortMode switch
        {
            ModelSortMode.Newest       => "Sorted by newest.",
            ModelSortMode.Alphabetical => "Sorted A–Z.",
            _ => string.Empty,
        };

        if (primary.Length == 0 && sortSuffix.Length == 0) return string.Empty;
        if (sortSuffix.Length == 0) return primary;
        if (primary.Length == 0) return sortSuffix;
        return $"{primary} {sortSuffix}";
    }

    private static string DescribeExtraFilters(double? cap, IReadOnlyCollection<string>? caps)
    {
        var parts = new List<string>();
        if (cap.HasValue) parts.Add($"≤{FormatParamCap(cap.Value)}");
        if (caps is { Count: > 0 })
        {
            var ordered = caps.OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
            parts.Add(string.Join("+", ordered));
        }
        return string.Join(", ", parts);
    }

    private static string FormatParamCap(double billions)
    {
        if (billions >= 1.0) return $"{billions:0.##}B";
        return $"{billions * 1000:0}M";
    }
}
