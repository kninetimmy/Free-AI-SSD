import Foundation

// Test runner for NdjsonFrameBuffer (MAC36b). Compiled as a standalone
// binary by the mac-runner-build CI job and run before Runner.app is
// packaged. Local invocation:
//
//     swiftc mac-runner/Sources/NdjsonFrameBuffer.swift \
//            mac-runner/Tests/NdjsonFrameBufferTests.swift \
//            -parse-as-library -target "arm64-apple-macos11.0" \
//            -o /tmp/ndjson-frame-buffer-tests && /tmp/ndjson-frame-buffer-tests

@main
struct NdjsonFrameBufferTestsMain {
    static func main() {
        let runner = NdjsonTestRunner()

        runner.test("Single complete line in one chunk") {
            var buf = NdjsonFrameBuffer()
            let lines = buf.append(Data("{\"type\":\"token\"}\n".utf8))
            try ndjsonExpect(lines.count == 1, "expected 1 line, got \(lines.count)")
            try ndjsonExpect(String(data: lines[0], encoding: .utf8) == "{\"type\":\"token\"}",
                             "line content mismatch")
            try ndjsonExpect(buf.flush() == nil, "tail should be empty")
        }

        runner.test("Two complete lines in one chunk") {
            var buf = NdjsonFrameBuffer()
            let lines = buf.append(Data("{\"a\":1}\n{\"b\":2}\n".utf8))
            try ndjsonExpect(lines.count == 2, "expected 2 lines, got \(lines.count)")
            try ndjsonExpect(String(data: lines[0], encoding: .utf8) == "{\"a\":1}", "first line")
            try ndjsonExpect(String(data: lines[1], encoding: .utf8) == "{\"b\":2}", "second line")
        }

        runner.test("Line split across two chunks") {
            var buf = NdjsonFrameBuffer()
            let first = buf.append(Data("{\"type\":\"to".utf8))
            try ndjsonExpect(first.isEmpty, "first chunk should yield no complete lines")
            let second = buf.append(Data("ken\",\"token\":\"hi\"}\n".utf8))
            try ndjsonExpect(second.count == 1, "expected 1 line after second chunk")
            try ndjsonExpect(String(data: second[0], encoding: .utf8) ==
                             "{\"type\":\"token\",\"token\":\"hi\"}",
                             "joined line content mismatch")
        }

        runner.test("Trailing partial line stays in tail") {
            var buf = NdjsonFrameBuffer()
            let lines = buf.append(Data("{\"a\":1}\n{\"b\":".utf8))
            try ndjsonExpect(lines.count == 1, "expected 1 complete line")
            try ndjsonExpect(String(data: lines[0], encoding: .utf8) == "{\"a\":1}", "first line")
            // Feed the rest; tail should fuse with the new chunk.
            let more = buf.append(Data("2}\n".utf8))
            try ndjsonExpect(more.count == 1, "expected 1 line after fuse")
            try ndjsonExpect(String(data: more[0], encoding: .utf8) == "{\"b\":2}", "fused line")
        }

        runner.test("Empty chunk is a no-op") {
            var buf = NdjsonFrameBuffer()
            let lines = buf.append(Data())
            try ndjsonExpect(lines.isEmpty, "empty chunk should produce no lines")
            try ndjsonExpect(buf.flush() == nil, "empty buffer should flush nil")
        }

        runner.test("CRLF line ending strips trailing \\r") {
            var buf = NdjsonFrameBuffer()
            let lines = buf.append(Data("{\"a\":1}\r\n".utf8))
            try ndjsonExpect(lines.count == 1, "expected 1 line, got \(lines.count)")
            try ndjsonExpect(String(data: lines[0], encoding: .utf8) == "{\"a\":1}",
                             "CR should be stripped")
        }

        runner.test("flush returns final unterminated tail") {
            var buf = NdjsonFrameBuffer()
            _ = buf.append(Data("{\"complete\":true}\n{\"partial\":".utf8))
            let tail = buf.flush()
            try ndjsonExpect(tail != nil, "tail should be non-nil")
            try ndjsonExpect(String(data: tail!, encoding: .utf8) == "{\"partial\":",
                             "tail content mismatch")
            try ndjsonExpect(buf.flush() == nil, "flush should drain the buffer")
        }

        runner.test("Lone newline yields empty line") {
            var buf = NdjsonFrameBuffer()
            let lines = buf.append(Data("\n".utf8))
            try ndjsonExpect(lines.count == 1, "expected 1 (empty) line for lone \\n")
            try ndjsonExpect(lines[0].isEmpty, "the line should be empty")
        }

        runner.summarizeAndExit()
    }
}

// MARK: - Test harness (file-private to avoid colliding with SsdEncryptionTests).

private struct NdjsonTestFailure: Error { let message: String; init(_ m: String) { message = m } }

private func ndjsonExpect(_ cond: @autoclosure () -> Bool, _ msg: String) throws {
    if !cond() { throw NdjsonTestFailure(msg) }
}

private final class NdjsonTestRunner {
    private var passed = 0
    private var failed: [(String, String)] = []

    func test(_ name: String, _ body: () throws -> Void) {
        do {
            try body()
            passed += 1
            print("  ok   \(name)")
        } catch let e as NdjsonTestFailure {
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
