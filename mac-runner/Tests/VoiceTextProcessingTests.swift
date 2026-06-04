import Foundation

// Test runner for VoiceTextProcessing + SentenceChunker (task #94). Compiled as a
// standalone binary by the mac-runner-build CI job and run before Runner.app is
// packaged. Mirrors the parity coverage the Windows StreamingTtsSpeaker carries.
// Local invocation:
//
//     swiftc mac-runner/Sources/VoiceTextProcessing.swift \
//            mac-runner/Tests/VoiceTextProcessingTests.swift \
//            -parse-as-library -target "arm64-apple-macos11.0" \
//            -o /tmp/voice-text-tests && /tmp/voice-text-tests

@main
struct VoiceTextProcessingTestsMain {
    static func main() {
        let runner = VoiceTestRunner()

        // MARK: - Citation stripping (parity with StreamingTtsSpeaker.StripCitations)

        runner.test("stripCitations removes section + page labels and the leading space") {
            try voiceExpect(
                VoiceTextProcessing.stripCitations("Start the engine [guide.pdf §Engine Start p.12].")
                    == "Start the engine.",
                "section+page label not stripped")
            try voiceExpect(
                VoiceTextProcessing.stripCitations("See the manual [F18.pdf p.3] now.")
                    == "See the manual now.",
                "page-only label not stripped")
        }

        runner.test("stripCitations strips known source extensions case-insensitively") {
            try voiceExpect(
                VoiceTextProcessing.stripCitations("Per the data [notes.MD].") == "Per the data.",
                "uppercase .MD not stripped")
            try voiceExpect(
                VoiceTextProcessing.stripCitations("Rows [data.csv] matter [more.json].")
                    == "Rows matter.",
                "csv/json not stripped")
        }

        runner.test("stripCitations leaves non-citation brackets alone") {
            try voiceExpect(
                VoiceTextProcessing.stripCitations("Press [Enter] to continue.")
                    == "Press [Enter] to continue.",
                "non-citation bracket should survive")
            try voiceExpect(
                VoiceTextProcessing.stripCitations("No labels here at all.")
                    == "No labels here at all.",
                "plain text should be untouched")
        }

        // MARK: - isSentenceEnd (parity with StreamingTtsSpeaker.IsSentenceEnd)

        runner.test("isSentenceEnd treats ! ? newline as enders") {
            try voiceExpect(VoiceTextProcessing.isSentenceEnd(Array("Go!"), 2), "! is an ender")
            try voiceExpect(VoiceTextProcessing.isSentenceEnd(Array("Why?"), 3), "? is an ender")
            try voiceExpect(VoiceTextProcessing.isSentenceEnd(Array("Line\n"), 4), "newline is an ender")
        }

        runner.test("isSentenceEnd ignores decimals and unspaced abbreviations") {
            try voiceExpect(!VoiceTextProcessing.isSentenceEnd(Array("3.14"), 1), "decimal dot is not an ender")
            try voiceExpect(!VoiceTextProcessing.isSentenceEnd(Array("e.g. x"), 1), "abbreviation dot is not an ender")
            try voiceExpect(VoiceTextProcessing.isSentenceEnd(Array("Done. Next"), 4), "period before space IS an ender")
        }

        // MARK: - SentenceChunker streaming behaviour

        runner.test("chunker emits a sentence only once it completes") {
            let c = SentenceChunker()
            try voiceExpect(c.feed("Start the ") == nil, "partial should not flush")
            try voiceExpect(c.feed("engine") == nil, "still partial")
            try voiceExpect(c.feed(". Then ") == "Start the engine.", "completed sentence should flush")
            try voiceExpect(c.feed("wait") == nil, "next partial held")
            try voiceExpect(c.finish() == "Then wait", "finish flushes the trailing remainder")
        }

        runner.test("chunker keeps a split citation intact until the sentence ends") {
            let c = SentenceChunker()
            // The "p.12]" page ref embeds a period; depth tracking must not split there.
            try voiceExpect(c.feed("Set flaps [guide.pdf ") == nil, "open bracket holds the flush")
            try voiceExpect(c.feed("p.12]") == nil, "still inside the citation, no ender yet")
            try voiceExpect(c.feed(". Next") == "Set flaps.", "citation stripped once sentence completes")
        }

        runner.test("chunker finish strips citations from the trailing fragment") {
            let c = SentenceChunker()
            _ = c.feed("No trailing punctuation [ref.txt]")
            try voiceExpect(c.finish() == "No trailing punctuation", "trailing citation stripped on finish")
            try voiceExpect(SentenceChunker().finish() == nil, "empty chunker yields nothing")
        }

        // MARK: - Config-encoding bridges

        runner.test("avSpeechRate maps the -10…10 config range about the default") {
            // Using representative AVSpeechUtterance constants (min 0, default 0.5, max 1).
            try voiceExpect(VoiceTextProcessing.avSpeechRate(fromConfigRate: 0, min: 0, defaultRate: 0.5, max: 1) == 0.5,
                            "neutral rate maps to default")
            try voiceExpect(VoiceTextProcessing.avSpeechRate(fromConfigRate: -10, min: 0, defaultRate: 0.5, max: 1) == 0.0,
                            "min config rate maps to platform min")
            try voiceExpect(VoiceTextProcessing.avSpeechRate(fromConfigRate: 10, min: 0, defaultRate: 0.5, max: 1) == 1.0,
                            "max config rate maps to platform max")
            try voiceExpect(VoiceTextProcessing.avSpeechRate(fromConfigRate: 99, min: 0, defaultRate: 0.5, max: 1) == 1.0,
                            "out-of-range high clamps")
            let mid = VoiceTextProcessing.avSpeechRate(fromConfigRate: 5, min: 0, defaultRate: 0.5, max: 1)
            try voiceExpect(abs(mid - 0.75) < 0.0001, "halfway above default, got \(mid)")
        }

        runner.test("avSpeechVolume maps 0…100 to 0…1 with clamping") {
            try voiceExpect(VoiceTextProcessing.avSpeechVolume(fromConfigVolume: 0) == 0.0, "0%")
            try voiceExpect(VoiceTextProcessing.avSpeechVolume(fromConfigVolume: 100) == 1.0, "100%")
            try voiceExpect(VoiceTextProcessing.avSpeechVolume(fromConfigVolume: 50) == 0.5, "50%")
            try voiceExpect(VoiceTextProcessing.avSpeechVolume(fromConfigVolume: 250) == 1.0, "over-range clamps to 1")
            try voiceExpect(VoiceTextProcessing.avSpeechVolume(fromConfigVolume: -20) == 0.0, "negative clamps to 0")
        }

        runner.summarizeAndExit()
    }
}

// MARK: - Test harness (file-private to avoid colliding with sibling test files).

private struct VoiceTestFailure: Error { let message: String; init(_ m: String) { message = m } }

private func voiceExpect(_ cond: @autoclosure () -> Bool, _ msg: String) throws {
    if !cond() { throw VoiceTestFailure(msg) }
}

private final class VoiceTestRunner {
    private var passed = 0
    private var failed: [(String, String)] = []

    func test(_ name: String, _ body: () throws -> Void) {
        do {
            try body()
            passed += 1
            print("  ok   \(name)")
        } catch let e as VoiceTestFailure {
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
