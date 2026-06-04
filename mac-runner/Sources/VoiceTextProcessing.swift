import Foundation

// MARK: - Voice text processing (task #94)
//
// Pure, framework-free logic shared by the Mac runner's native TTS. This is the
// Swift port of the Windows runner's `StreamingTtsSpeaker` sentence-chunking +
// citation-stripping (runner/Services/StreamingTtsSpeaker.cs) and the rate/volume
// remaps that bridge the cross-OS PortableConfig encoding (`ttsRate` -10…10,
// `ttsVolume` 0…100) to AVSpeechUtterance's 0…1 ranges.
//
// Deliberately imports only Foundation so it compiles standalone for the unit
// test target (same pattern as IngestProgressFormatter / NdjsonFrameBuffer) —
// the AVFoundation/Speech glue lives in VoiceController.swift, which is never
// linked into the test binary.

/// Buffers streamed LLM tokens and emits complete, citation-stripped sentences
/// as soon as they finish, so speech can start before the full response lands.
/// Mirrors `StreamingTtsSpeaker.FeedToken` / `Finish` exactly: a bracket-depth
/// scan keeps inline citation labels (which embed periods, e.g. "p.12") intact
/// until a real sentence boundary follows, at which point they're muted.
final class SentenceChunker {
    private var buffer: String = ""

    /// Feed an incremental token. Returns the speakable sentence(s) that just
    /// completed (citations stripped, trimmed), or nil if the buffer doesn't yet
    /// end a sentence. Like the C# original, at most one chunk is returned per
    /// call (everything up to and including the last clean sentence-ender).
    func feed(_ token: String) -> String? {
        buffer.append(token)
        return flushCompleteSentences()
    }

    /// Signal end of stream. Returns any trailing buffered text as a final
    /// speakable chunk (citations stripped, trimmed), or nil if nothing remains.
    func finish() -> String? {
        let remaining = VoiceTextProcessing.stripCitations(buffer)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        buffer = ""
        return remaining.isEmpty ? nil : remaining
    }

    private func flushCompleteSentences() -> String? {
        let chars = Array(buffer)

        // Find the last sentence-ender NOT inside a citation bracket. With no
        // brackets present (citations off) depth stays 0 and this is exactly the
        // "last sentence-ender wins" behaviour.
        var lastEnd = -1
        var depth = 0
        for i in 0..<chars.count {
            let c = chars[i]
            if c == "[" {
                depth += 1
            } else if c == "]" {
                if depth > 0 { depth -= 1 }
            } else if depth == 0 && VoiceTextProcessing.isSentenceEnd(chars, i) {
                lastEnd = i
            }
        }

        if lastEnd < 0 { return nil }

        // Include trailing whitespace/quotes with the sentence.
        var cutIndex = lastEnd + 1
        while cutIndex < chars.count {
            let c = chars[cutIndex]
            if c.isWhitespace || c == "\"" || c == "'" {
                cutIndex += 1
            } else {
                break
            }
        }

        let sentences = String(chars[0..<cutIndex])
            .trimmingCharacters(in: .whitespacesAndNewlines)
        buffer = String(chars[cutIndex..<chars.count])

        let speakable = VoiceTextProcessing.stripCitations(sentences)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return speakable.isEmpty ? nil : speakable
    }
}

enum VoiceTextProcessing {
    /// Matches an inline citation label (and the space before it) so the
    /// synthesizer never reads "[guide.pdf §Engine Start p.12]" aloud. Ported
    /// verbatim from `StreamingTtsSpeaker.CitationSpanRx`. No-op when none present.
    private static let citationPattern =
        #"\s*\[[^\]]*(?:§|\bp\.\s*\d|\.(?:pdf|txt|md|markdown|csv|json)\b)[^\]]*\]"#

    private static let citationRegex: NSRegularExpression? = {
        try? NSRegularExpression(pattern: citationPattern, options: [.caseInsensitive])
    }()

    static func stripCitations(_ text: String) -> String {
        guard let rx = citationRegex, !text.isEmpty else { return text }
        let range = NSRange(text.startIndex..<text.endIndex, in: text)
        return rx.stringByReplacingMatches(in: text, options: [], range: range, withTemplate: "")
    }

    /// Sentence-ender test mirroring `StreamingTtsSpeaker.IsSentenceEnd`: avoids
    /// false positives on decimals ("3.14") and unspaced abbreviations ("e.g.").
    static func isSentenceEnd(_ chars: [Character], _ index: Int) -> Bool {
        let ch = chars[index]
        if ch == "!" || ch == "?" || ch == "\n" { return true }
        if ch != "." { return false }

        // Decimal number: digit before AND after the dot.
        if index > 0 && chars[index - 1].isNumber
            && index + 1 < chars.count && chars[index + 1].isNumber {
            return false
        }
        // Abbreviation: a letter immediately follows the dot (no space).
        if index + 1 < chars.count && chars[index + 1].isLetter {
            return false
        }
        return true
    }

    // MARK: - Cross-OS config encoding bridges

    /// Map the PortableConfig `ttsRate` (-10…10, 0 = neutral) onto an
    /// AVSpeechUtterance rate, given the platform's min/default/max constants.
    /// Piecewise-linear about the default so 0 lands exactly on the natural rate.
    static func avSpeechRate(fromConfigRate rate: Int,
                             min minRate: Float,
                             defaultRate: Float,
                             max maxRate: Float) -> Float {
        let clamped = Float(Swift.max(-10, Swift.min(10, rate)))
        if clamped == 0 { return defaultRate }
        if clamped < 0 {
            return defaultRate + (clamped / 10.0) * (defaultRate - minRate)
        }
        return defaultRate + (clamped / 10.0) * (maxRate - defaultRate)
    }

    /// Map the PortableConfig `ttsVolume` (0…100) onto AVSpeechSynthesizer's 0…1.
    static func avSpeechVolume(fromConfigVolume volume: Int) -> Float {
        return Float(Swift.max(0, Swift.min(100, volume))) / 100.0
    }
}
