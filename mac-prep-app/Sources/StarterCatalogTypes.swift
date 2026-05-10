import Foundation

// MARK: - F2 catalog types
//
// Mirrors of prep-core's StarterModelEntry (rich) and the Windows
// StarterCatalogEntry projection (display). The Mac sidecar emits the
// rich shape via discover-catalog / refresh-catalog; the picker
// consumes the projected display shape so its row layout matches
// what Windows shows in the merged Models grid.

/// Rich entry the sidecar returns. Field names match the camelCase
/// JSON the C# host emits via JsonNamingPolicy.CamelCase, so this
/// decodes directly with JSONDecoder().
struct StarterModelEntry: Codable, Hashable {
    let tag: String
    let params: String
    let sizeTier: String
    let description: String
    let useCases: [String]
    /// F2a: Approximate pull count from ollama.com/library; nil for
    /// the bundled catalog (predates the field). Drives the
    /// "Most popular" filter in the picker.
    let pullCount: Int64?
}

/// Display projection for the picker. Mirrors the Windows
/// StarterCatalogEntry(Tag, SizeTier, BestAt) record so a future
/// Windows ↔ Mac row-comparison test (or visual review) sees the
/// same field set on both sides.
struct StarterModelDisplayEntry: Identifiable, Hashable {
    let tag: String
    let sizeTier: String
    let bestAt: String
    /// F2a: nil when the bundled catalog is in play; populated from
    /// ollama.com/library after Refresh.
    let pullCount: Int64?
    var id: String { tag }

    /// "Best at" combines description + comma-joined use cases —
    /// matches MainWindow.xaml.cs:ProjectCatalog so Mac and Windows
    /// pickers carry the same caption.
    static func from(_ entry: StarterModelEntry) -> StarterModelDisplayEntry {
        let useCases = entry.useCases.filter { !$0.isEmpty }
        let bestAt: String
        if entry.description.isEmpty {
            bestAt = useCases.joined(separator: ", ")
        } else if useCases.isEmpty {
            bestAt = entry.description
        } else {
            bestAt = "\(entry.description) (\(useCases.joined(separator: ", ")))"
        }
        return StarterModelDisplayEntry(
            tag: entry.tag,
            sizeTier: entry.sizeTier,
            bestAt: bestAt,
            pullCount: entry.pullCount)
    }
}

/// F2a: pure picker filter so unit tests can pin search + popular
/// behavior without constructing the @MainActor PrepViewModel (which
/// depends on AppKit/SwiftUI and isn't in the test binary's compile
/// list). The view-model delegates here.
///
/// Search is a case-insensitive substring against tag, sizeTier, and
/// bestAt. The "Most popular" filter sorts by pull count desc and
/// caps at `popularLimit`; entries without a pullCount drop out
/// entirely (the bundled catalog has no pull counts so toggling it
/// before Refresh yields zero rows — visible signal to Refresh first).
func applyStarterModelFilters(
    to source: [StarterModelDisplayEntry],
    search: String,
    showOnlyMostPopular: Bool,
    popularLimit: Int
) -> [StarterModelDisplayEntry] {
    var result = source
    let needle = search.trimmingCharacters(in: .whitespacesAndNewlines)
    if !needle.isEmpty {
        result = result.filter { entry in
            entry.tag.range(of: needle, options: .caseInsensitive) != nil
                || entry.sizeTier.range(of: needle, options: .caseInsensitive) != nil
                || entry.bestAt.range(of: needle, options: .caseInsensitive) != nil
        }
    }
    if showOnlyMostPopular {
        result = result
            .compactMap { entry -> (StarterModelDisplayEntry, Int64)? in
                guard let count = entry.pullCount else { return nil }
                return (entry, count)
            }
            .sorted { $0.1 > $1.1 }
            .prefix(popularLimit)
            .map { $0.0 }
    }
    return result
}

/// M11: caption that announces the picker's visible row count + cap
/// reason. The Most-popular toggle's effect is invisible without this
/// — ollama.com's natural order is already popularity-desc, so capping
/// the top-15 produces the same first-screenful, and field testers
/// reported the toggle "doesn't do anything." Returning empty when no
/// filter is active keeps the UI quiet (catalogStatusText already
/// reports the total count); only the *change* needs a dedicated line.
func formatStarterRowCountCaption(
    visible: Int,
    total: Int,
    showOnlyMostPopular: Bool,
    hasSearch: Bool
) -> String {
    if total == 0 { return "" }
    switch (showOnlyMostPopular, hasSearch) {
    case (true, true):
        return "Showing top \(visible) of \(total) by pulls (filtered by search)."
    case (true, false):
        return "Showing top \(visible) of \(total) by pulls."
    case (false, true):
        return "Showing \(visible) of \(total) matching search."
    case (false, false):
        return ""
    }
}

/// Decode the entries array out of a PrepHostResult payload. The
/// PrepHostController hands us a [String: Any] from JSONSerialization;
/// re-serializing the "entries" subtree and decoding via JSONDecoder
/// is the simplest path to typed values without introducing a custom
/// JSON wrapper around the existing controller protocol.
///
/// Returns empty if the payload has no entries field or the shape
/// doesn't decode — callers treat that as a soft "no catalog" case.
func decodeStarterEntries(from payload: [String: Any]) -> [StarterModelEntry] {
    guard let entries = payload["entries"] else { return [] }
    do {
        let data = try JSONSerialization.data(withJSONObject: entries, options: [])
        return try JSONDecoder().decode([StarterModelEntry].self, from: data)
    } catch {
        return []
    }
}
