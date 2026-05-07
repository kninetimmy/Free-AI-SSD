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
}

/// Display projection for the picker. Mirrors the Windows
/// StarterCatalogEntry(Tag, SizeTier, BestAt) record so a future
/// Windows ↔ Mac row-comparison test (or visual review) sees the
/// same field set on both sides.
struct StarterModelDisplayEntry: Identifiable, Hashable {
    let tag: String
    let sizeTier: String
    let bestAt: String
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
            bestAt: bestAt)
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
