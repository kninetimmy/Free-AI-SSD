import Foundation

// Test runner for VoiceRpcProtocol (task #106). Compiled as a standalone binary
// by the mac-runner-build CI job, mirroring the VoiceTextProcessing test target.
// Covers the Swift half of the sidecar<->Swift voice wire format: request parsing,
// response encoding, and the PCM->WAV header. Local invocation:
//
//     swiftc mac-runner/Sources/VoiceRpcProtocol.swift \
//            mac-runner/Tests/VoiceRpcProtocolTests.swift \
//            -parse-as-library -target "arm64-apple-macos11.0" \
//            -o /tmp/voice-rpc-tests && /tmp/voice-rpc-tests

@main
struct VoiceRpcProtocolTestsMain {
    static func main() {
        let runner = RpcTestRunner()

        // MARK: - Request parsing (sidecar -> Swift)

        runner.test("parses a well-formed STT request") {
            let line = "voice-stt-request {\"id\":7,\"wavBase64\":\"QUJD\"}"
            try rpcExpect(VoiceRpcProtocol.isVoiceRequest(line), "should be recognized as a request")
            guard case .stt(let id, let wav)? = VoiceRpcProtocol.parseRequest(line) else {
                throw RpcTestFailure("did not parse as .stt")
            }
            try rpcExpect(id == 7, "id mismatch")
            try rpcExpect(wav == "QUJD", "wavBase64 mismatch")
        }

        runner.test("parses a well-formed TTS request with all fields") {
            let line = "voice-tts-request {\"id\":3,\"text\":\"hi there\",\"voiceId\":\"com.apple.voice.x\",\"rate\":5,\"volume\":80}"
            guard case .tts(let id, let text, let voiceId, let rate, let volume)? = VoiceRpcProtocol.parseRequest(line) else {
                throw RpcTestFailure("did not parse as .tts")
            }
            try rpcExpect(id == 3, "id mismatch")
            try rpcExpect(text == "hi there", "text mismatch")
            try rpcExpect(voiceId == "com.apple.voice.x", "voiceId mismatch")
            try rpcExpect(rate == 5, "rate mismatch")
            try rpcExpect(volume == 80, "volume mismatch")
        }

        runner.test("TTS request tolerates a null voiceId and defaults rate/volume") {
            let line = "voice-tts-request {\"id\":1,\"text\":\"x\",\"voiceId\":null}"
            guard case .tts(_, _, let voiceId, let rate, let volume)? = VoiceRpcProtocol.parseRequest(line) else {
                throw RpcTestFailure("did not parse as .tts")
            }
            try rpcExpect(voiceId == nil, "voiceId should be nil")
            try rpcExpect(rate == 0, "rate should default to 0")
            try rpcExpect(volume == 100, "volume should default to 100")
        }

        runner.test("rejects malformed / non-voice lines") {
            try rpcExpect(VoiceRpcProtocol.parseRequest("voice-stt-request not-json") == nil, "non-JSON payload should be nil")
            try rpcExpect(VoiceRpcProtocol.parseRequest("voice-stt-request {\"wavBase64\":\"QUJD\"}") == nil, "missing id should be nil")
            try rpcExpect(VoiceRpcProtocol.parseRequest("voice-stt-request") == nil, "no payload should be nil")
            try rpcExpect(VoiceRpcProtocol.parseRequest("log: hello") == nil, "log line is not a request")
            try rpcExpect(!VoiceRpcProtocol.isVoiceRequest("ready: http://x"), "ready line is not a request")
        }

        // MARK: - Response encoding (Swift -> sidecar), round-trips through JSON

        runner.test("STT response carries text and a null error") {
            let line = VoiceRpcProtocol.sttResponse(id: 9, text: "engine start", error: nil)
            try rpcExpect(line.hasPrefix("voice-stt-response "), "wrong prefix")
            let dict = try decodeBody(line, prefix: "voice-stt-response")
            try rpcExpect((dict["id"] as? NSNumber)?.intValue == 9, "id mismatch")
            try rpcExpect(dict["text"] as? String == "engine start", "text mismatch")
            try rpcExpect(dict["error"] is NSNull, "error should be JSON null")
        }

        runner.test("STT error response carries a non-null error and null text") {
            let line = VoiceRpcProtocol.sttResponse(id: 2, text: nil, error: "denied")
            let dict = try decodeBody(line, prefix: "voice-stt-response")
            try rpcExpect(dict["text"] is NSNull, "text should be JSON null")
            try rpcExpect(dict["error"] as? String == "denied", "error mismatch")
        }

        runner.test("TTS response carries base64 audio") {
            let line = VoiceRpcProtocol.ttsResponse(id: 4, wavBase64: "QUJD", error: nil)
            let dict = try decodeBody(line, prefix: "voice-tts-response")
            try rpcExpect((dict["id"] as? NSNumber)?.intValue == 4, "id mismatch")
            try rpcExpect(dict["wavBase64"] as? String == "QUJD", "audio mismatch")
            try rpcExpect(dict["error"] is NSNull, "error should be JSON null")
        }

        // MARK: - PCM -> WAV header

        runner.test("wrapPcm16ToWav writes a canonical 16k mono header") {
            let pcm = Data([0x01, 0x02, 0x03, 0x04])
            let wav = VoiceRpcProtocol.wrapPcm16ToWav(pcm, sampleRate: 16000, channels: 1)
            try rpcExpect(wav.count == 44 + 4, "total length should be header + data")
            try rpcExpect(ascii(wav, 0, 4) == "RIFF", "missing RIFF")
            try rpcExpect(ascii(wav, 8, 4) == "WAVE", "missing WAVE")
            try rpcExpect(ascii(wav, 12, 4) == "fmt ", "missing fmt ")
            try rpcExpect(ascii(wav, 36, 4) == "data", "missing data")
            try rpcExpect(leU32(wav, 4) == UInt32(36 + 4), "RIFF chunk size")
            try rpcExpect(leU16(wav, 20) == 1, "audioFormat should be PCM(1)")
            try rpcExpect(leU16(wav, 22) == 1, "channels should be 1")
            try rpcExpect(leU32(wav, 24) == 16000, "sampleRate")
            try rpcExpect(leU32(wav, 28) == 16000 * 2, "byteRate = rate*channels*2")
            try rpcExpect(leU16(wav, 32) == 2, "blockAlign = channels*2")
            try rpcExpect(leU16(wav, 34) == 16, "bitsPerSample")
            try rpcExpect(leU32(wav, 40) == 4, "data chunk size")
            try rpcExpect(Array(wav[44..<48]) == [0x01, 0x02, 0x03, 0x04], "PCM payload preserved")
        }

        runner.summarizeAndExit()
    }

    // MARK: - Helpers

    private static func decodeBody(_ line: String, prefix: String) throws -> [String: Any] {
        let json = String(line.dropFirst(prefix.count + 1))
        guard let obj = try JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] else {
            throw RpcTestFailure("response body is not a JSON object")
        }
        return obj
    }

    private static func ascii(_ data: Data, _ offset: Int, _ count: Int) -> String {
        String(bytes: data[offset..<(offset + count)], encoding: .ascii) ?? ""
    }

    private static func leU16(_ d: Data, _ o: Int) -> UInt16 {
        UInt16(d[o]) | (UInt16(d[o + 1]) << 8)
    }

    private static func leU32(_ d: Data, _ o: Int) -> UInt32 {
        UInt32(d[o]) | (UInt32(d[o + 1]) << 8) | (UInt32(d[o + 2]) << 16) | (UInt32(d[o + 3]) << 24)
    }
}

// MARK: - Test harness (file-private to avoid colliding with sibling test files).

private struct RpcTestFailure: Error { let message: String; init(_ m: String) { message = m } }

private func rpcExpect(_ cond: @autoclosure () -> Bool, _ msg: String) throws {
    if !cond() { throw RpcTestFailure(msg) }
}

private final class RpcTestRunner {
    private var passed = 0
    private var failed: [(String, String)] = []

    func test(_ name: String, _ body: () throws -> Void) {
        do {
            try body()
            passed += 1
            print("  ok   \(name)")
        } catch let e as RpcTestFailure {
            failed.append((name, e.message))
            print("  FAIL \(name): \(e.message)")
        } catch {
            failed.append((name, String(describing: error)))
            print("  FAIL \(name): \(error)")
        }
    }

    func summarizeAndExit() -> Never {
        print("---")
        print("\(passed) passed, \(failed.count) failed")
        if !failed.isEmpty {
            for (n, m) in failed { print("  - \(n): \(m)") }
            exit(1)
        }
        exit(0)
    }
}
