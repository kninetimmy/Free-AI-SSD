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
    static func main() async {
        // Subcommand: write a canonical Mac PrepApp encrypted-config
        // fixture for the cross-language test. Invoked once (or on
        // deliberate format change) to (re)generate
        // tests/Fixtures/MacEncryptedConfig/swift-prep-encrypted/.
        // Mirrors the MAC5 mac-runner Tests' write-fixture subcommand.
        let args = CommandLine.arguments
        if args.count >= 3, args[1] == "write-prep-fixture" {
            let outDir = URL(fileURLWithPath: args[2])
            do {
                try writeMac17PrepFixture(outDir: outDir)
                print("Wrote MAC17 prep fixture to \(outDir.path)")
                exit(0)
            } catch {
                print("MAC17 prep fixture write failed: \(error)")
                exit(1)
            }
        }

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

        // MARK: PrepHostController cancel-path tests (MAC17a Issue #1)
        //
        // Pin the bug fix: on timeout the pending continuation slot must
        // be freed and the command name re-usable. Pre-fix, the slot
        // leaked and a re-send under the same name would either trap on
        // double-resume or silently overwrite the leaked continuation.
        // Drives the internal `awaitCommandResult` seam directly so we
        // don't need a real spawned sidecar.

        runner.test("Issue #1: send timeout frees pending result slot") {
            let controller = PrepHostController()
            var caught: Error?
            do {
                _ = try await controller.awaitCommandResult(
                    commandName: "test-cmd",
                    timeout: 0.05,
                    dispatch: {})
            } catch {
                caught = error
            }
            try expect(caught != nil, "expected timeout/cancel to throw")
            try expect(controller.pendingResultsCountForTest == 0,
                       "pending slot leaked: count=\(controller.pendingResultsCountForTest)")
        }

        runner.test("Issue #1: same command name is re-usable after timeout") {
            let controller = PrepHostController()

            // First call: time out, slot must clear.
            _ = try? await controller.awaitCommandResult(
                commandName: "cmd-x",
                timeout: 0.05,
                dispatch: {})
            try expect(controller.pendingResultsCountForTest == 0,
                       "first-call slot leaked")

            // Second call with the same command name must not crash on
            // re-registration. Pre-fix, the slot still held the stale
            // continuation and the next stdout `result:` line would
            // try to resume it, trapping on double-resume in real use.
            var caught: Error?
            do {
                _ = try await controller.awaitCommandResult(
                    commandName: "cmd-x",
                    timeout: 0.05,
                    dispatch: {})
            } catch {
                caught = error
            }
            try expect(caught != nil, "second-call expected timeout/cancel")
            try expect(controller.pendingResultsCountForTest == 0,
                       "second-call slot leaked")
        }

        await runner.run()
    }
}

// MARK: - Fixture writer (cross-language proof)
//
// Generates the swift-prep-encrypted fixture under
// tests/Fixtures/MacEncryptedConfig/swift-prep-encrypted/ via the same
// EncryptedConfigWriter the SwiftUI flow uses. The fixture is then
// consumed by the C# MacEncryptedConfigCrossLanguageTests to prove
// MAC17 PrepApp's first-write payload roundtrips through the Windows
// Runner. This complements (does not replace) the MAC5 csharp-encrypted/
// fixture — MAC5 pins the *blob* format, MAC17 pins the *initial-write
// plaintext shape* (InitialPortableConfigPayload).

// Constants must match the C# test's expected values.
private let mac17FixturePassword       = "mac17-prep-cross-lang-fixture-pw"
private let mac17FixtureOllamaPort     = 13577
private let mac17FixtureNetworkPort    = 41555
private let mac17FixtureNetworkBind    = "127.0.0.1"
private let mac17FixturePreferredCompute = "cpu"

func writeMac17PrepFixture(outDir: URL) throws {
    let fm = FileManager.default
    // Preserve README.md (and any sibling docs) — only clear the
    // config/ subdirectory that SsdEncryption.saveEncryptedConfig
    // writes into. This way `regenerate the fixture` doesn't trash
    // the README that explains what password unlocks it.
    let configDir = outDir.appendingPathComponent("config")
    if fm.fileExists(atPath: configDir.path) {
        try fm.removeItem(at: configDir)
    }
    try fm.createDirectory(at: outDir, withIntermediateDirectories: true)

    // Use a non-default ollamaPort (13577) so the C# unlock test can
    // distinguish "fixture decoded successfully" from "fixture decoded
    // empty and PortableConfig defaults filled in 11434." Other fields
    // stay at their canonical defaults.
    let payload = InitialPortableConfigPayload(
        ollamaPort: mac17FixtureOllamaPort,
        networkModeEnabled: false,
        networkBindAddress: mac17FixtureNetworkBind,
        networkPort: mac17FixtureNetworkPort,
        networkRequireApiKey: true,
        networkApiKey: "",
        preferredCompute: mac17FixturePreferredCompute
    )

    let writer = EncryptedConfigWriter()
    try writer.writeInitialEncryptedConfig(
        ssdRoot: outDir, payload: payload, passphrase: mac17FixturePassword)
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
        let body: () async throws -> Void
    }

    private var cases: [Case] = []

    func test(_ name: String, _ body: @escaping () async throws -> Void) {
        cases.append(Case(name: name, body: body))
    }

    func run() async {
        var failures = 0
        for c in cases {
            do {
                try await c.body()
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
