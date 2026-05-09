import Foundation

/// Streaming NDJSON line splitter for the `/api/chat/stream` consumer
/// (MAC36b). The runner-core endpoint emits one JSON object per
/// `\n`-terminated line and `URLSessionDataDelegate` hands us bytes in
/// arbitrarily-sized chunks, so frames may be split across chunks or
/// arrive multiple per chunk. macOS 11 baseline rules out
/// `URLSession.bytes(for:)` — this is the bring-your-own-buffer path.
struct NdjsonFrameBuffer {
    private var tail = Data()

    /// Append a chunk and return any complete lines it produced (without
    /// the trailing `\n`). Bytes after the last newline stay buffered for
    /// the next call. A trailing `\r` on a line is stripped so `\r\n`
    /// terminators round-trip correctly.
    mutating func append(_ chunk: Data) -> [Data] {
        if chunk.isEmpty { return [] }
        var combined = tail
        combined.append(chunk)

        var lines: [Data] = []
        var cursor = combined.startIndex
        while let nl = combined[cursor..<combined.endIndex].firstIndex(of: 0x0A) {
            var line = combined.subdata(in: cursor..<nl)
            if line.last == 0x0D { line.removeLast() }
            lines.append(line)
            cursor = combined.index(after: nl)
        }

        tail = combined.subdata(in: cursor..<combined.endIndex)
        return lines
    }

    /// Return any unterminated tail bytes at end-of-stream and reset.
    /// The `/chat/stream` server always ends frames with `\n`, but this
    /// keeps the helper honest and useful for tests.
    mutating func flush() -> Data? {
        if tail.isEmpty { return nil }
        let out = tail
        tail = Data()
        return out
    }
}
