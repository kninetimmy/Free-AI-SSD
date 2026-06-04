import Foundation

// Test runner for IngestProgressFormatter (task #99). Compiled as a standalone
// binary by the mac-runner-build CI job and run before Runner.app is packaged.
// Local invocation:
//
//     swiftc mac-runner/Sources/IngestProgressFormatter.swift \
//            mac-runner/Tests/IngestProgressFormatterTests.swift \
//            -parse-as-library -target "arm64-apple-macos11.0" \
//            -o /tmp/ingest-progress-tests && /tmp/ingest-progress-tests

@main
struct IngestProgressFormatterTestsMain {
    static func main() {
        let runner = ProgressTestRunner()

        runner.test("formatCount groups thousands with commas") {
            try progressExpect(IngestProgressFormatter.formatCount(0) == "0", "0")
            try progressExpect(IngestProgressFormatter.formatCount(42) == "42", "42")
            try progressExpect(IngestProgressFormatter.formatCount(999) == "999", "999")
            try progressExpect(IngestProgressFormatter.formatCount(1000) == "1,000", "1000")
            try progressExpect(IngestProgressFormatter.formatCount(1847) == "1,847", "1847")
            try progressExpect(IngestProgressFormatter.formatCount(3273) == "3,273", "3273")
            try progressExpect(IngestProgressFormatter.formatCount(1234567) == "1,234,567", "1234567")
        }

        runner.test("formatEta buckets seconds / minutes / hours") {
            try progressExpect(IngestProgressFormatter.formatEta(nil) == nil, "nil input")
            try progressExpect(IngestProgressFormatter.formatEta(0) == nil, "zero")
            try progressExpect(IngestProgressFormatter.formatEta(-5) == nil, "negative")
            try progressExpect(IngestProgressFormatter.formatEta(.infinity) == nil, "infinite")
            try progressExpect(IngestProgressFormatter.formatEta(1) == "~1s", "1s")
            try progressExpect(IngestProgressFormatter.formatEta(45) == "~45s", "45s")
            try progressExpect(IngestProgressFormatter.formatEta(59) == "~59s", "59s")
            try progressExpect(IngestProgressFormatter.formatEta(120) == "~2m", "120s -> 2m")
            try progressExpect(IngestProgressFormatter.formatEta(125) == "~2m", "125s rounds to 2m")
            try progressExpect(IngestProgressFormatter.formatEta(3600) == "~1h", "3600s -> 1h")
            try progressExpect(IngestProgressFormatter.formatEta(3900) == "~1h 5m", "3900s -> 1h 5m")
        }

        runner.test("etaSeconds returns nil until there's signal") {
            try progressExpect(
                IngestProgressFormatter.etaSeconds(embeddedChunks: 0, totalChunks: 100, elapsedSeconds: 5) == nil,
                "no chunks embedded yet")
            try progressExpect(
                IngestProgressFormatter.etaSeconds(embeddedChunks: 50, totalChunks: 50, elapsedSeconds: 5) == nil,
                "already complete")
            try progressExpect(
                IngestProgressFormatter.etaSeconds(embeddedChunks: 10, totalChunks: 100, elapsedSeconds: 0) == nil,
                "no elapsed time")
        }

        runner.test("etaSeconds extrapolates from rate") {
            // 100 chunks in 10s -> 10/s; 900 remaining -> 90s.
            let eta = IngestProgressFormatter.etaSeconds(embeddedChunks: 100, totalChunks: 1000, elapsedSeconds: 10)
            try progressExpect(eta != nil, "expected an estimate")
            try progressExpect(abs((eta ?? 0) - 90) < 0.001, "expected ~90s, got \(String(describing: eta))")
        }

        runner.test("state with no total is indeterminate Preparing") {
            let s = IngestProgressFormatter.state(embeddedChunks: 0, totalChunks: 0, etaSeconds: nil)
            try progressExpect(s.fraction == nil, "fraction should be nil")
            try progressExpect(s.detail == "Preparing…", "detail mismatch: \(s.detail)")
        }

        runner.test("state computes fraction and detail with ETA") {
            let s = IngestProgressFormatter.state(embeddedChunks: 1847, totalChunks: 3273, etaSeconds: 120)
            try progressExpect(s.fraction != nil, "fraction present")
            try progressExpect(abs((s.fraction ?? 0) - (1847.0 / 3273.0)) < 0.0001, "fraction value")
            try progressExpect(s.detail == "1,847 / 3,273 chunks · ~2m", "detail mismatch: \(s.detail)")
        }

        runner.test("state clamps fraction to 1 and drops ETA when absent") {
            let s = IngestProgressFormatter.state(embeddedChunks: 3300, totalChunks: 3273, etaSeconds: nil)
            try progressExpect(s.fraction == 1.0, "fraction clamped to 1")
            try progressExpect(s.detail == "3,300 / 3,273 chunks", "detail without ETA: \(s.detail)")
        }

        runner.summarizeAndExit()
    }
}

// MARK: - Test harness (file-private to avoid colliding with sibling test files).

private struct ProgressTestFailure: Error { let message: String; init(_ m: String) { message = m } }

private func progressExpect(_ cond: @autoclosure () -> Bool, _ msg: String) throws {
    if !cond() { throw ProgressTestFailure(msg) }
}

private final class ProgressTestRunner {
    private var passed = 0
    private var failed: [(String, String)] = []

    func test(_ name: String, _ body: () throws -> Void) {
        do {
            try body()
            passed += 1
            print("  ok   \(name)")
        } catch let e as ProgressTestFailure {
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
