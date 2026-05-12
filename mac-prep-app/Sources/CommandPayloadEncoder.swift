import Foundation

// M19 — JSON encoder for sidecar command payloads (currently
// `set-hf-token` and `search-hf`). PrepViewModel previously hand-rolled
// the JSON with `\\` + `\"` escaping only, which produces malformed JSON
// for inputs containing newlines, tabs, or other control characters —
// the sidecar's `JsonDocument.Parse` rejects those with a non-obvious
// `payload parse failed` error. `JSONSerialization` handles every JSON
// escape rule (control chars, surrogates, embedded quotes) for us.
//
// Kept as a free-standing utility (no SwiftUI deps) so the test runner
// in `mac-prep-app/Tests/PrepAppTests.swift` can include it without
// pulling in `PrepViewModel`.

enum CommandPayloadEncoder {
    /// Encode `dict` as a single-line JSON object string. Returns `nil`
    /// when `JSONSerialization` rejects the input. For the `[String:
    /// String]` shape every caller uses today this can't fail at
    /// runtime, but the optional return guards future callers from
    /// emitting garbage on serialization failure.
    static func encode(_ dict: [String: String]) -> String? {
        guard JSONSerialization.isValidJSONObject(dict),
              let data = try? JSONSerialization.data(
                withJSONObject: dict, options: []),
              let text = String(data: data, encoding: .utf8)
        else {
            return nil
        }
        return text
    }
}
