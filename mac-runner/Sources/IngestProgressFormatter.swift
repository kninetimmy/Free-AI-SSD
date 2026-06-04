import Foundation

/// Pure display logic for the document ingest / sweep / rebuild progress
/// indicator (task #99). Kept in a standalone file with no SwiftUI / AppKit
/// dependency so it compiles into the mac-runner test target the same way
/// `NdjsonFrameBuffer` does. The view-model folds streamed `progress` NDJSON
/// frames through these helpers to drive a determinate progress bar, a
/// "1,847 / 3,273 chunks · ~2m" detail line, and an ETA.
struct IngestProgressState: Equatable {
    /// 0...1 completion for the current file, or nil when the chunk total
    /// isn't known yet (the bar shows indeterminate until the first frame
    /// carries a total).
    var fraction: Double?
    /// One-line detail, e.g. "1,847 / 3,273 chunks · ~2m" or "Preparing…".
    var detail: String
}

enum IngestProgressFormatter {
    /// Groups a non-negative integer with comma thousands separators, e.g.
    /// `1847 -> "1,847"`. Hand-rolled rather than `NumberFormatter` so the
    /// output is locale-independent and deterministic under test.
    static func formatCount(_ value: Int) -> String {
        let negative = value < 0
        var digits = Array(String(abs(value)))
        var grouped: [Character] = []
        var count = 0
        while let d = digits.popLast() {
            if count > 0 && count % 3 == 0 { grouped.append(",") }
            grouped.append(d)
            count += 1
        }
        return (negative ? "-" : "") + String(grouped.reversed())
    }

    /// Formats a remaining-seconds estimate as a compact "~Ns" / "~Nm" /
    /// "~Hh Mm" string. Returns nil for non-positive or non-finite input so
    /// the caller can drop the ETA suffix entirely.
    static func formatEta(_ seconds: Double?) -> String? {
        guard let seconds, seconds.isFinite, seconds > 0 else { return nil }
        let total = Int(seconds.rounded())
        if total < 60 { return "~\(max(1, total))s" }
        if total < 3600 {
            let minutes = Int((seconds / 60).rounded())
            return "~\(max(1, minutes))m"
        }
        let hours = total / 3600
        let minutes = (total % 3600) / 60
        return minutes > 0 ? "~\(hours)h \(minutes)m" : "~\(hours)h"
    }

    /// Estimates remaining seconds from work done so far. Returns nil until
    /// there's enough signal (at least one chunk embedded over a positive
    /// elapsed interval) to avoid a wildly wrong first estimate.
    static func etaSeconds(embeddedChunks: Int, totalChunks: Int, elapsedSeconds: Double) -> Double? {
        guard embeddedChunks > 0, totalChunks > embeddedChunks, elapsedSeconds > 0 else { return nil }
        let rate = Double(embeddedChunks) / elapsedSeconds  // chunks per second
        guard rate > 0 else { return nil }
        return Double(totalChunks - embeddedChunks) / rate
    }

    /// Folds a single embed-progress reading into a display state.
    static func state(embeddedChunks: Int, totalChunks: Int, etaSeconds: Double?) -> IngestProgressState {
        guard totalChunks > 0 else {
            return IngestProgressState(fraction: nil, detail: "Preparing…")
        }
        let fraction = min(1.0, max(0.0, Double(embeddedChunks) / Double(totalChunks)))
        var detail = "\(formatCount(embeddedChunks)) / \(formatCount(totalChunks)) chunks"
        if let eta = formatEta(etaSeconds) {
            detail += " · \(eta)"
        }
        return IngestProgressState(fraction: fraction, detail: detail)
    }
}
