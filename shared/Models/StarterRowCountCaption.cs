namespace FreeAiSsd.Shared.Models;

/// <summary>
/// M11: caption that announces the picker's visible row count + cap reason.
/// The "Most popular" toggle's effect is invisible without this — ollama.com's
/// natural order is already popularity-desc, so capping the top-15 produces the
/// same first-screenful, and field testers reported the toggle "doesn't do
/// anything." Returning empty when no filter is active keeps the UI quiet
/// (the existing "N models" status text already reports the total); only the
/// *change* needs a dedicated line.
///
/// Mirrors the Swift <c>formatStarterRowCountCaption</c> in
/// <c>mac-prep-app/Sources/StarterCatalogTypes.swift</c> — keep wording in
/// sync so cross-OS smoke testing reads identical strings.
/// </summary>
public static class StarterRowCountCaption
{
    public static string Format(int visible, int total, bool showOnlyMostPopular, bool hasSearch)
    {
        if (total == 0) return string.Empty;
        return (showOnlyMostPopular, hasSearch) switch
        {
            (true,  true)  => $"Showing top {visible} of {total} by pulls (filtered by search).",
            (true,  false) => $"Showing top {visible} of {total} by pulls.",
            (false, true)  => $"Showing {visible} of {total} matching search.",
            (false, false) => string.Empty,
        };
    }
}
