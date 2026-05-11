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
    /// C3: numeric parameter count in billions (8.0, 1.5, 0.335);
    /// nil when the source size token is empty or unparseable, or for
    /// the bundled catalog. Drives the parameter-cap filter.
    let parametersBillion: Double?
    /// C5: ISO 8601 string emitted by the host's System.Text.Json for
    /// the scraped <c>x-test-updated</c> timestamp. Stored as String
    /// (rather than Date) because ISO 8601 strings sort lexically the
    /// same way as the underlying instants — perfect for "Newest"
    /// ordering and avoids JSONDecoder dateDecodingStrategy fiddling
    /// that would risk breaking the existing decode path. Nil for the
    /// bundled catalog and unparseable scrape values.
    let lastUpdated: String?
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
    /// C4: capability tags ("tools", "vision", "thinking", "audio").
    /// Empty when not from the live catalog — capability filter
    /// treats empty as pass-through so configured/custom rows aren't
    /// hidden when filtering.
    let capabilities: [String]
    /// C3: numeric param count in billions; nil when unknown.
    let parametersBillion: Double?
    /// C5: ISO 8601 string from the live catalog; nil when unknown.
    let lastUpdated: String?
    var id: String { tag }

    /// Custom init with defaults for the C3/C4/C5 fields so existing
    /// fixtures (notably <c>makeF2aFixture</c> in PrepAppTests) keep
    /// compiling. Production code in <c>from(_:)</c> always passes
    /// every argument.
    init(
        tag: String,
        sizeTier: String,
        bestAt: String,
        pullCount: Int64?,
        capabilities: [String] = [],
        parametersBillion: Double? = nil,
        lastUpdated: String? = nil
    ) {
        self.tag = tag
        self.sizeTier = sizeTier
        self.bestAt = bestAt
        self.pullCount = pullCount
        self.capabilities = capabilities
        self.parametersBillion = parametersBillion
        self.lastUpdated = lastUpdated
    }

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
            pullCount: entry.pullCount,
            capabilities: useCases,
            parametersBillion: entry.parametersBillion,
            lastUpdated: entry.lastUpdated)
    }
}

/// C5: model picker sort mode — mirrors the C# `ModelSortMode` enum.
/// Popular is the default and matches the natural ollama.com order
/// (the catalog is already pull-count desc on the way in). Newest
/// orders by `lastUpdated` ISO 8601 string descending; entries with
/// nil sort last. Alphabetical sorts by tag ascending, case-insensitive.
enum PickerSortMode {
    case popular
    case newest
    case alphabetical
}

/// F2a + C3/C4/C5: pure picker filter so unit tests can pin search,
/// popular-cap, capability-AND, parameter-cap, and sort behavior
/// without constructing the @MainActor PrepViewModel (which depends
/// on AppKit/SwiftUI and isn't in the test binary's compile list).
/// The view-model delegates here.
///
/// Order of operations:
///   1. Search (case-insensitive substring on tag / sizeTier / bestAt).
///   2. Capability AND filter (entry must carry every selected cap;
///      entries with empty `capabilities` pass through so on-disk and
///      bundled rows aren't accidentally hidden).
///   3. Parameter cap (entries with nil parametersBillion pass
///      through — same posture as Most-popular for missing pullCount).
///   4. Most-popular cap (drops entries without a pullCount; sorts
///      desc by pullCount; takes top `popularLimit`).
///   5. Sort mode applied last so the popular-cap step can use its
///      own internal pull-count sort to pick the top-N first.
func applyStarterModelFilters(
    to source: [StarterModelDisplayEntry],
    search: String,
    showOnlyMostPopular: Bool,
    popularLimit: Int,
    maxParametersBillion: Double? = nil,
    requiredCapabilities: Set<String> = [],
    sortMode: PickerSortMode = .popular
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
    if !requiredCapabilities.isEmpty {
        result = result.filter { entry in
            if entry.capabilities.isEmpty { return true }
            let entryCaps = Set(entry.capabilities.map { $0.lowercased() })
            return requiredCapabilities.allSatisfy { entryCaps.contains($0.lowercased()) }
        }
    }
    if let cap = maxParametersBillion {
        result = result.filter { entry in
            guard let p = entry.parametersBillion else { return true }
            return p <= cap
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
    switch sortMode {
    case .popular:
        // No-op — the source already arrives in popularity-desc order
        // and the showOnlyMostPopular branch (when active) already
        // applied a deterministic pull-count sort.
        break
    case .newest:
        // ISO 8601 strings sort lexically the same as the underlying
        // instants. Nil values sort last under descending order via
        // the empty-string fallback (which is lexically less than any
        // valid ISO 8601 string).
        result.sort { ($0.lastUpdated ?? "") > ($1.lastUpdated ?? "") }
    case .alphabetical:
        result.sort { $0.tag.localizedCaseInsensitiveCompare($1.tag) == .orderedAscending }
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
    hasSearch: Bool,
    maxParametersBillion: Double? = nil,
    requiredCapabilities: Set<String> = [],
    sortMode: PickerSortMode = .popular
) -> String {
    if total == 0 { return "" }

    let extra = describeExtraStarterFilters(
        cap: maxParametersBillion,
        capabilities: requiredCapabilities)
    let hasExtra = !extra.isEmpty

    let primary: String
    switch (showOnlyMostPopular, hasSearch, hasExtra) {
    case (true, true, true):
        primary = "Showing top \(visible) of \(total) by pulls (filtered by search; \(extra))."
    case (true, true, false):
        primary = "Showing top \(visible) of \(total) by pulls (filtered by search)."
    case (true, false, true):
        primary = "Showing top \(visible) of \(total) by pulls (\(extra))."
    case (true, false, false):
        primary = "Showing top \(visible) of \(total) by pulls."
    case (false, true, true):
        primary = "Showing \(visible) of \(total) matching search (\(extra))."
    case (false, true, false):
        primary = "Showing \(visible) of \(total) matching search."
    case (false, false, true):
        primary = "Showing \(visible) of \(total) matching filter (\(extra))."
    case (false, false, false):
        primary = ""
    }

    let sortSuffix: String
    switch sortMode {
    case .newest:       sortSuffix = "Sorted by newest."
    case .alphabetical: sortSuffix = "Sorted A–Z."
    case .popular:      sortSuffix = ""
    }

    if primary.isEmpty && sortSuffix.isEmpty { return "" }
    if sortSuffix.isEmpty { return primary }
    if primary.isEmpty { return sortSuffix }
    return "\(primary) \(sortSuffix)"
}

private func describeExtraStarterFilters(cap: Double?, capabilities: Set<String>) -> String {
    var parts: [String] = []
    if let cap = cap {
        parts.append("≤\(formatStarterParamCap(cap))")
    }
    if !capabilities.isEmpty {
        let ordered = capabilities.sorted { $0.lowercased() < $1.lowercased() }
        parts.append(ordered.joined(separator: "+"))
    }
    return parts.joined(separator: ", ")
}

private func formatStarterParamCap(_ billions: Double) -> String {
    if billions >= 1.0 {
        // Match C# "0.##" — strip trailing zeros from up to 2 decimals.
        let formatted = String(format: "%.2f", billions)
        let trimmed = formatted
            .replacingOccurrences(of: #"0+$"#, with: "", options: .regularExpression)
            .replacingOccurrences(of: #"\.$"#, with: "", options: .regularExpression)
        return "\(trimmed)B"
    }
    return "\(Int((billions * 1000).rounded()))M"
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
