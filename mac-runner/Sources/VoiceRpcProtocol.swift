import Foundation

// MARK: - Voice RPC protocol (task #106, Phase 2 VR companion)
//
// Pure, framework-free wire format for the sidecar<->Swift voice round-trip.
// The Swift half of the protocol; the C# half is mac-runner-host/VoiceRpcProtocol.cs
// and the two must stay in lockstep. Imports only Foundation so it compiles into
// the standalone unit-test binary (same pattern as VoiceTextProcessing).
//
// The Mac sidecar (mac-runner-host) has no native STT/TTS — the macOS Speech /
// AVSpeechSynthesizer frameworks live here in the Swift app (decision #166/#168).
// So /api/voice/query on a Mac host ships audio to us over the existing stdio
// channel: the sidecar writes "voice-*-request" frames on its stdout (which we
// already read), we do the native work, and reply with "voice-*-response" frames
// on its stdin (which its command loop already reads). Each pair is correlated
// by an integer id.

/// A decoded request from the sidecar.
enum VoiceRpcRequest: Equatable {
    /// Base64 WAV (16-bit mono 16 kHz) to transcribe on-device.
    case stt(id: Int, wavBase64: String)
    /// Text to render to a WAV, plus the host's configured voice settings.
    case tts(id: Int, text: String, voiceId: String?, rate: Int, volume: Int)
}

enum VoiceRpcProtocol {
    static let sttRequestPrefix = "voice-stt-request"
    static let ttsRequestPrefix = "voice-tts-request"
    static let sttResponsePrefix = "voice-stt-response"
    static let ttsResponsePrefix = "voice-tts-response"

    static func isVoiceRequest(_ line: String) -> Bool {
        return line.hasPrefix(sttRequestPrefix) || line.hasPrefix(ttsRequestPrefix)
    }

    /// Parse a "voice-*-request <json>" line into a typed request, or nil if the
    /// line isn't a recognizable, well-formed request frame.
    static func parseRequest(_ line: String) -> VoiceRpcRequest? {
        guard let (prefix, json) = splitFrame(line) else { return nil }
        guard let obj = try? JSONSerialization.jsonObject(with: Data(json.utf8)),
              let dict = obj as? [String: Any],
              let id = intValue(dict["id"]) else { return nil }

        switch prefix {
        case sttRequestPrefix:
            guard let wav = dict["wavBase64"] as? String else { return nil }
            return .stt(id: id, wavBase64: wav)
        case ttsRequestPrefix:
            guard let text = dict["text"] as? String else { return nil }
            let voiceId = dict["voiceId"] as? String
            let rate = intValue(dict["rate"]) ?? 0
            let volume = intValue(dict["volume"]) ?? 100
            return .tts(id: id, text: text, voiceId: voiceId, rate: rate, volume: volume)
        default:
            return nil
        }
    }

    /// Build a "voice-stt-response <json>" line. Pass a non-nil `error` to signal
    /// failure (the C# side maps it to a TranscriptionResult.Failure → 500).
    static func sttResponse(id: Int, text: String?, error: String?) -> String {
        return encode(prefix: sttResponsePrefix, body: [
            "id": id,
            "text": text as Any? ?? NSNull(),
            "error": error as Any? ?? NSNull(),
        ])
    }

    /// Build a "voice-tts-response <json>" line. Pass a non-nil `error` to signal
    /// failure (the C# side logs it and omits audio from the response).
    static func ttsResponse(id: Int, wavBase64: String?, error: String?) -> String {
        return encode(prefix: ttsResponsePrefix, body: [
            "id": id,
            "wavBase64": wavBase64 as Any? ?? NSNull(),
            "error": error as Any? ?? NSNull(),
        ])
    }

    // MARK: - WAV writer

    /// Wrap a raw little-endian 16-bit PCM buffer in a canonical 44-byte
    /// RIFF/WAVE header. Mirrors VoiceRpcProtocol.WrapPcm16ToWav on the C# side;
    /// used to package AVSpeechSynthesizer output for the companion to play.
    static func wrapPcm16ToWav(_ pcm: Data, sampleRate: Int, channels: Int) -> Data {
        let bitsPerSample = 16
        let byteRate = sampleRate * channels * bitsPerSample / 8
        let blockAlign = channels * bitsPerSample / 8
        let dataLen = pcm.count

        var header = Data()
        header.append(ascii: "RIFF")
        header.appendLE(UInt32(36 + dataLen))
        header.append(ascii: "WAVE")
        header.append(ascii: "fmt ")
        header.appendLE(UInt32(16))             // fmt chunk size
        header.appendLE(UInt16(1))              // PCM
        header.appendLE(UInt16(channels))
        header.appendLE(UInt32(sampleRate))
        header.appendLE(UInt32(byteRate))
        header.appendLE(UInt16(blockAlign))
        header.appendLE(UInt16(bitsPerSample))
        header.append(ascii: "data")
        header.appendLE(UInt32(dataLen))

        var wav = header
        wav.append(pcm)
        return wav
    }

    // MARK: - Helpers

    private static func splitFrame(_ line: String) -> (prefix: String, json: String)? {
        guard let spaceIdx = line.firstIndex(of: " ") else { return nil }
        let prefix = String(line[line.startIndex..<spaceIdx])
        let json = String(line[line.index(after: spaceIdx)...])
        guard !json.isEmpty else { return nil }
        return (prefix, json)
    }

    private static func encode(prefix: String, body: [String: Any]) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: body, options: [.sortedKeys]),
              let json = String(data: data, encoding: .utf8) else {
            return "\(prefix) {\"id\":0,\"error\":\"encode failed\"}"
        }
        return "\(prefix) \(json)"
    }

    /// JSONSerialization decodes integers as NSNumber; accept either an Int or a
    /// numeric NSNumber so the field survives a round-trip through JSON.
    private static func intValue(_ raw: Any?) -> Int? {
        if let i = raw as? Int { return i }
        if let n = raw as? NSNumber { return n.intValue }
        return nil
    }
}

private extension Data {
    mutating func append(ascii: String) {
        append(contentsOf: ascii.utf8)
    }

    mutating func appendLE(_ value: UInt16) {
        append(UInt8(value & 0xFF))
        append(UInt8((value >> 8) & 0xFF))
    }

    mutating func appendLE(_ value: UInt32) {
        append(UInt8(value & 0xFF))
        append(UInt8((value >> 8) & 0xFF))
        append(UInt8((value >> 16) & 0xFF))
        append(UInt8((value >> 24) & 0xFF))
    }
}
