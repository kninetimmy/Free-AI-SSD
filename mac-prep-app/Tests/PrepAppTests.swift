import Foundation

// MAC17 mac-prep-app test runner. Mirrors mac-runner/Tests/SsdEncryptionTests.swift
// pattern: a standalone CLI binary compiled with swiftc that exits non-zero on
// any failed assertion. Run from CI via:
//
//     swiftc \
//         mac-prep-app/Sources/PrepFlowStep.swift \
//         mac-prep-app/Sources/DiskutilFormatCommand.swift \
//         mac-prep-app/Tests/PrepAppTests.swift \
//         -parse-as-library -target "arm64-apple-macos11.0" \
//         -o /tmp/mac-prep-tests && /tmp/mac-prep-tests
//
// We intentionally limit the test surface to the *pure*, non-UI types
// (DiskutilFormatCommand + PrepFlowStep). DiskutilDriveService, PrepHostController,
// and PrepViewModel are exercised end-to-end on the published binary in CI,
// where a real macOS environment is available — pure-builder tests here are
// the parity-pin against the C# DiskutilFormatCommandTests and would fail
// fast on any drift.

@main
struct PrepAppTestsMain {
    static func main() {
        let runner = TestRunner()

        // MARK: DiskutilFormatCommand parity-pin tests

        runner.test("Build emits expected argv shape") {
            let built = try DiskutilFormatCommand.build(
                diskIdentifier: "disk2", label: "FREEAI", fileSystem: "ExFAT")
            try expect(built.fileName == "/usr/sbin/diskutil",
                       "fileName: \(built.fileName)")
            try expect(built.arguments == ["eraseDisk", "ExFAT", "FREEAI", "MBR", "disk2"],
                       "arguments: \(built.arguments)")
        }

        runner.test("Build accepts case variants and emits canonical ExFAT") {
            for variant in ["ExFAT", "EXFAT", "exfat", "exFAT"] {
                let built = try DiskutilFormatCommand.build(
                    diskIdentifier: "disk2", label: "FREEAI", fileSystem: variant)
                try expect(built.arguments[1] == "ExFAT",
                           "variant=\(variant) emitted \(built.arguments[1])")
            }
        }

        runner.test("Build pins MBR partition scheme") {
            let built = try DiskutilFormatCommand.build(
                diskIdentifier: "disk2", label: "FREEAI", fileSystem: "ExFAT")
            try expect(built.arguments.contains("MBR"), "missing MBR in \(built.arguments)")
            try expect(!built.arguments.contains("GPT"), "unexpected GPT")
            try expect(!built.arguments.contains("APM"), "unexpected APM")
        }

        runner.test("ParseDiskIdentifier accepts common forms") {
            let cases: [(String, String)] = [
                ("disk2", "disk2"),
                ("disk20", "disk20"),
                ("disk2s1", "disk2s1"),
                ("/dev/disk2", "disk2"),
                ("/dev/disk2s1", "disk2s1"),
                ("  disk3  ", "disk3"),
            ]
            for (input, expected) in cases {
                let parsed = try DiskutilFormatCommand.parseDiskIdentifier(input)
                try expect(parsed == expected,
                           "input='\(input)' → '\(parsed)', expected '\(expected)'")
            }
        }

        runner.test("ParseDiskIdentifier rejects malformed forms") {
            for bad in ["", "  ", "disk", "disks2", "disk2s", "disk2sX", "disk2x1", "/dev/sda", "hd0"] {
                var threw = false
                do { _ = try DiskutilFormatCommand.parseDiskIdentifier(bad) }
                catch { threw = true }
                try expect(threw, "expected throw for '\(bad)'")
            }
        }

        runner.test("Build rejects APFS / NTFS / unsupported filesystems") {
            for bad in ["APFS", "NTFS", "FAT32", "HFS+", "MSDOS"] {
                var threw = false
                do {
                    _ = try DiskutilFormatCommand.build(
                        diskIdentifier: "disk2", label: "FREEAI", fileSystem: bad)
                } catch { threw = true }
                try expect(threw, "expected throw for fs='\(bad)'")
            }
        }

        runner.test("SanitizeLabel removes metacharacters and caps length") {
            try expect(DiskutilFormatCommand.sanitizeLabel("FREEAI") == "FREEAI")
            try expect(DiskutilFormatCommand.sanitizeLabel("  FREEAI  ") == "FREEAI")
            try expect(DiskutilFormatCommand.sanitizeLabel("a/b\\c:d*e?f\"g<h>i|j") == "abcdefghij",
                       "got \(DiskutilFormatCommand.sanitizeLabel("a/b\\c:d*e?f\"g<h>i|j"))")
            let oversized = String(repeating: "X", count: 32)
            try expect(DiskutilFormatCommand.sanitizeLabel(oversized).count == 15)
        }

        runner.test("Build substitutes single space when label sanitizes to empty") {
            let built = try DiskutilFormatCommand.build(
                diskIdentifier: "disk2", label: "///\\\\:::", fileSystem: "ExFAT")
            try expect(built.arguments[2] == " ", "label arg was '\(built.arguments[2])'")
        }

        // MARK: PrepFlowStep equality / case-coverage smoke

        runner.test("PrepFlowStep equality") {
            try expect(PrepFlowStep.welcome == .welcome)
            try expect(PrepFlowStep.failed(message: "x") == .failed(message: "x"))
            try expect(PrepFlowStep.failed(message: "a") != .failed(message: "b"))
        }

        runner.run()
    }
}

// MARK: - Test runner harness (mirrors mac-runner/Tests/SsdEncryptionTests.swift)

struct ExpectationFailure: Error {
    let message: String
}

func expect(_ condition: @autoclosure () -> Bool, _ message: String = "") throws {
    if !condition() {
        throw ExpectationFailure(message: message.isEmpty ? "expectation failed" : message)
    }
}

final class TestRunner {
    private struct Case {
        let name: String
        let body: () throws -> Void
    }

    private var cases: [Case] = []

    func test(_ name: String, _ body: @escaping () throws -> Void) {
        cases.append(Case(name: name, body: body))
    }

    func run() {
        var failures = 0
        for c in cases {
            do {
                try c.body()
                print("ok      \(c.name)")
            } catch let f as ExpectationFailure {
                failures += 1
                print("FAIL    \(c.name) — \(f.message)")
            } catch {
                failures += 1
                print("ERROR   \(c.name) — \(error.localizedDescription)")
            }
        }
        print("\n\(cases.count - failures)/\(cases.count) passed.")
        if failures > 0 { exit(1) }
    }
}
