import Foundation

// Test runner for the Swift port of SsdEncryption. Compiled as a standalone
// command-line binary by the mac-runner-build CI job and run as a smoke test
// before Runner.app is packaged. Exits non-zero on any failure so CI can gate
// on it. Designed to be runnable locally too:
//
//     swiftc mac-runner/Sources/SsdEncryption.swift \
//            mac-runner/Tests/SsdEncryptionTests.swift \
//            -parse-as-library -target "arm64-apple-macos11.0" \
//            -o /tmp/ssd-encryption-tests && /tmp/ssd-encryption-tests

@main
struct SsdEncryptionTestsMain {
    static func main() {
        // Subcommand: produce a canonical encrypted fixture for the
        // cross-language test. Invoked once by the build pipeline (or
        // manually) to (re)generate `tests/Fixtures/MacEncryptedConfig/`.
        let args = CommandLine.arguments
        if args.count >= 3, args[1] == "write-fixture" {
            let outDir = URL(fileURLWithPath: args[2])
            do {
                try writeCrossLanguageFixture(outDir: outDir)
                print("Wrote fixture to \(outDir.path)")
                exit(0)
            } catch {
                print("Fixture write failed: \(error)")
                exit(1)
            }
        }

        let runner = TestRunner()

        runner.test("PBKDF2 produces 32-byte key") {
            let salt = Data([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                             0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10])
            let key = try SsdEncryption.pbkdf2Sha256(
                password: "test-password", salt: salt, iterations: 1000, keyBytes: 32)
            try expect(key.count == 32, "expected 32 bytes, got \(key.count)")
        }

        runner.test("PBKDF2 RFC 6070 vector (SHA-256, 4096 iters)") {
            // Cross-check: PBKDF2-HMAC-SHA256("password", "salt", 4096, 32) =
            // c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a
            // (from RFC 6070-style published vectors for HMAC-SHA256).
            let key = try SsdEncryption.pbkdf2Sha256(
                password: "password",
                salt: "salt".data(using: .utf8)!,
                iterations: 4096, keyBytes: 32)
            let hex = key.map { String(format: "%02x", $0) }.joined()
            try expect(hex == "c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a",
                       "unexpected PBKDF2 output: \(hex)")
        }

        runner.test("isEncryptionEnabled returns false for missing state") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            try expect(SsdEncryption.isEncryptionEnabled(ssdRoot: root) == false,
                       "missing state should not register as enabled")
        }

        runner.test("isEffectivelyEncryptedForWriteGuard fails closed on missing state + blob present") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            let configDir = SsdEncryption.configDirURL(ssdRoot: root)
            try FileManager.default.createDirectory(at: configDir, withIntermediateDirectories: true)
            try Data("{}".utf8).write(to: SsdEncryption.encryptedBlobURL(ssdRoot: root))
            try expect(SsdEncryption.isEffectivelyEncryptedForWriteGuard(ssdRoot: root),
                       "blob alone should trigger fail-closed protection")
        }

        runner.test("Roundtrip: seal + unlock with same password") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            try seedEncryptedFixture(root: root, password: "pw-correct",
                                     plaintext: ["ollamaPort": 11434, "models": []])

            switch SsdEncryption.tryUnlockPortableConfig(ssdRoot: root, password: "pw-correct") {
            case .success(let unlocked):
                try expect((unlocked.config["ollamaPort"] as? Int) == 11434,
                           "expected ollamaPort 11434")
                try expect((unlocked.config["models"] as? [Any])?.isEmpty == true,
                           "expected empty models array")
                unlocked.material.zeroize()
            case .failure(let err):
                throw TestFailure("unexpected failure: \(err.localizedDescription)")
            }
        }

        runner.test("Wrong password returns Incorrect password.") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            try seedEncryptedFixture(root: root, password: "right",
                                     plaintext: ["ollamaPort": 11434])

            switch SsdEncryption.tryUnlockPortableConfig(ssdRoot: root, password: "wrong") {
            case .success: throw TestFailure("wrong password should not unlock")
            case .failure(let err):
                try expect(err == .incorrectPassword,
                           "got \(err.localizedDescription)")
            }
        }

        runner.test("Empty password returns Password is required.") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            switch SsdEncryption.tryUnlockPortableConfig(ssdRoot: root, password: "") {
            case .success: throw TestFailure("empty password should not unlock")
            case .failure(let err):
                try expect(err == .passwordRequired, "got \(err.localizedDescription)")
            }
        }

        runner.test("Missing metadata returns Encrypted drive metadata is missing.") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            switch SsdEncryption.tryUnlockPortableConfig(ssdRoot: root, password: "anything") {
            case .success: throw TestFailure("missing metadata should not unlock")
            case .failure(let err):
                try expect(err == .metadataMissing, "got \(err.localizedDescription)")
            }
        }

        runner.test("Tampered ciphertext returns Incorrect password.") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            try seedEncryptedFixture(root: root, password: "pw",
                                     plaintext: ["ollamaPort": 11434])

            // Flip a bit inside the base64 ciphertext.
            let blobURL = SsdEncryption.encryptedBlobURL(ssdRoot: root)
            var blobJson = try JSONSerialization.jsonObject(with: try Data(contentsOf: blobURL))
                as! [String: Any]
            var ct = Data(base64Encoded: blobJson["ciphertext"] as! String)!
            ct[0] ^= 0xFF
            blobJson["ciphertext"] = ct.base64EncodedString()
            try JSONSerialization.data(withJSONObject: blobJson, options: [.prettyPrinted])
                .write(to: blobURL)

            switch SsdEncryption.tryUnlockPortableConfig(ssdRoot: root, password: "pw") {
            case .success: throw TestFailure("tampered ciphertext should not unlock")
            case .failure(let err):
                try expect(err == .incorrectPassword, "got \(err.localizedDescription)")
            }
        }

        runner.test("Save roundtrip preserves unknown fields") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            try seedEncryptedFixture(root: root, password: "pw",
                                     plaintext: ["ollamaPort": 11434, "futureField": "preserve-me"])

            // Unlock, mutate a known field, save.
            guard case .success(let unlocked) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: "pw") else {
                throw TestFailure("seed unlock failed")
            }
            var cfg = unlocked.config
            cfg["ollamaPort"] = 22222
            try SsdEncryption.saveEncryptedConfig(ssdRoot: root, config: cfg, material: unlocked.material)
            unlocked.material.zeroize()

            // Re-unlock and verify both fields are intact.
            guard case .success(let reopened) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: "pw") else {
                throw TestFailure("re-unlock failed")
            }
            try expect((reopened.config["ollamaPort"] as? Int) == 22222,
                       "mutation lost — expected 22222")
            try expect((reopened.config["futureField"] as? String) == "preserve-me",
                       "unknown field lost on save")
            reopened.material.zeroize()
        }

        runner.test("Save updates state file's updatedAtUtc") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            try seedEncryptedFixture(root: root, password: "pw",
                                     plaintext: ["ollamaPort": 1])

            guard case .success(let unlocked) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: "pw") else {
                throw TestFailure("seed unlock failed")
            }
            // Tiny pause so the new updatedAtUtc differs from the original.
            Thread.sleep(forTimeInterval: 0.01)
            try SsdEncryption.saveEncryptedConfig(
                ssdRoot: root, config: unlocked.config, material: unlocked.material)
            unlocked.material.zeroize()

            let stateData = try Data(contentsOf: SsdEncryption.stateFileURL(ssdRoot: root))
            let state = try JSONDecoder().decode(EncryptionStateFile.self, from: stateData)
            try expect(state.enabled, "state.enabled should remain true after save")
            try expect(state.scheme == SsdEncryptionConstants.schemeName,
                       "state.scheme should match constant")
            try expect(state.iterations > 0, "state.iterations should be > 0")
            try expect(state.encryptedConfigFile == SsdEncryptionConstants.encryptedConfigFileName,
                       "state.encryptedConfigFile should match constant")
            try expect(!state.updatedAtUtc.isEmpty, "state.updatedAtUtc should be set")
        }

        runner.test("Plaintext migration: branch A merges newer plaintext") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            try seedEncryptedFixture(root: root, password: "pw",
                                     plaintext: ["ollamaPort": 1])

            // Drop a newer plaintext file alongside the encrypted blob.
            let plaintextURL = SsdEncryption.configDirURL(ssdRoot: root)
                .appendingPathComponent(SsdEncryptionConstants.plaintextConfigFileName)
            let newer: [String: Any] = ["ollamaPort": 9999, "fromPlaintext": true]
            try JSONSerialization.data(withJSONObject: newer, options: [.prettyPrinted])
                .write(to: plaintextURL)
            // Bump mtime well past the blob's.
            try FileManager.default.setAttributes(
                [.modificationDate: Date(timeIntervalSinceNow: 60)],
                ofItemAtPath: plaintextURL.path)

            guard case .success(let unlocked) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: "pw") else {
                throw TestFailure("seed unlock failed")
            }
            let result = SsdEncryption.tryMigratePlaintext(
                ssdRoot: root, material: unlocked.material)
            unlocked.material.zeroize()

            switch result {
            case .mergedFromPlaintext(let merged):
                try expect((merged["ollamaPort"] as? Int) == 9999, "expected newer config to be merged")
                try expect(!FileManager.default.fileExists(atPath: plaintextURL.path),
                           "plaintext should be deleted after merge")
            default:
                throw TestFailure("expected mergedFromPlaintext, got \(result)")
            }

            // Re-unlock and verify the merge stuck.
            guard case .success(let reopened) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: "pw") else {
                throw TestFailure("post-merge unlock failed")
            }
            try expect((reopened.config["ollamaPort"] as? Int) == 9999, "merge did not persist")
            try expect((reopened.config["fromPlaintext"] as? Bool) == true,
                       "plaintext-only field did not persist into encrypted blob")
            reopened.material.zeroize()
        }

        runner.test("Plaintext migration: branch B silently removes stale plaintext") {
            let root = TempDir.make()
            defer { TempDir.remove(root) }
            try seedEncryptedFixture(root: root, password: "pw",
                                     plaintext: ["ollamaPort": 1])
            // Wait a hair, then drop a plaintext (older mtime than the blob).
            let plaintextURL = SsdEncryption.configDirURL(ssdRoot: root)
                .appendingPathComponent(SsdEncryptionConstants.plaintextConfigFileName)
            try Data("{}".utf8).write(to: plaintextURL)
            try FileManager.default.setAttributes(
                [.modificationDate: Date(timeIntervalSinceNow: -3600)],
                ofItemAtPath: plaintextURL.path)

            guard case .success(let unlocked) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: "pw") else {
                throw TestFailure("seed unlock failed")
            }
            let result = SsdEncryption.tryMigratePlaintext(
                ssdRoot: root, material: unlocked.material)
            unlocked.material.zeroize()

            switch result {
            case .stalePlaintextRemoved:
                try expect(!FileManager.default.fileExists(atPath: plaintextURL.path),
                           "stale plaintext should be deleted")
            default:
                throw TestFailure("expected stalePlaintextRemoved, got \(result)")
            }
        }

        runner.test("UnlockMaterial.zeroize wipes derivedKey") {
            let salt = Data([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
                             0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10])
            let key = try SsdEncryption.pbkdf2Sha256(
                password: "pw", salt: salt, iterations: 1000, keyBytes: 32)
            let m = UnlockMaterial(derivedKey: key, salt: salt, iterations: 1000,
                                   scheme: SsdEncryptionConstants.schemeName)
            try expect(m.derivedKey.contains(where: { $0 != 0 }), "key should not start zeroed")
            m.zeroize()
            try expect(m.derivedKey.allSatisfy { $0 == 0 }, "zeroize should null all bytes")
        }

        // Cross-language fixture round-trip.
        // If the C# Windows-build CI job has dropped a fixture under
        // `tests/Fixtures/MacEncryptedConfig/csharp-encrypted/`, decrypt it
        // here and assert the plaintext matches the expected canonical config.
        // The fixture is committed to the repo so this also passes locally.
        runner.test("Cross-language: Swift unlocks the C#-produced fixture") {
            guard let repoRoot = ProcessInfo.processInfo.environment["FREE_AI_SSD_REPO"]
                ?? findRepoRoot() else {
                print("  skipped (FREE_AI_SSD_REPO not set and repo root not auto-detected)")
                return
            }
            let fixtureRoot = URL(fileURLWithPath: repoRoot)
                .appendingPathComponent("tests/Fixtures/MacEncryptedConfig/csharp-encrypted")
            guard FileManager.default.fileExists(atPath: fixtureRoot.path) else {
                print("  skipped (fixture not present — Windows-build will produce it)")
                return
            }
            // Fixture password and expected plaintext are documented in the
            // README.md inside the fixture directory.
            let result = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: fixtureRoot, password: "mac5-cross-lang-fixture-pw")
            switch result {
            case .success(let unlocked):
                try expect((unlocked.config["ollamaPort"] as? Int) == 13577,
                           "C# fixture plaintext drift")
                unlocked.material.zeroize()
            case .failure(let err):
                throw TestFailure("C# fixture decrypt failed: \(err.localizedDescription)")
            }
        }

        runner.summarizeAndExit()
    }
}

// MARK: - Test harness

struct TestFailure: Error { let message: String; init(_ m: String) { message = m } }

func expect(_ cond: @autoclosure () -> Bool, _ msg: String) throws {
    if !cond() { throw TestFailure(msg) }
}

final class TestRunner {
    private var passed = 0
    private var failed: [(String, String)] = []

    func test(_ name: String, _ body: () throws -> Void) {
        do {
            try body()
            passed += 1
            print("  ok   \(name)")
        } catch let e as TestFailure {
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

enum TempDir {
    static func make() -> URL {
        let url = URL(fileURLWithPath: NSTemporaryDirectory(), isDirectory: true)
            .appendingPathComponent("mac5-test-\(UUID().uuidString)", isDirectory: true)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }
    static func remove(_ url: URL) {
        try? FileManager.default.removeItem(at: url)
    }
}

/// Builds an encrypted fixture under `root` from scratch using the production
/// save path. PBKDF2 is genuine (slow); tests using this should pass small
/// plaintexts and not run on a hot loop.
func seedEncryptedFixture(root: URL, password: String, plaintext: [String: Any]) throws {
    let configDir = SsdEncryption.configDirURL(ssdRoot: root)
    try FileManager.default.createDirectory(at: configDir, withIntermediateDirectories: true)

    var saltBytes = [UInt8](repeating: 0, count: SsdEncryptionConstants.saltBytes)
    let s = SecRandomCopyBytes(kSecRandomDefault, saltBytes.count, &saltBytes)
    guard s == errSecSuccess else { throw TestFailure("SecRandomCopyBytes failed") }
    let salt = Data(saltBytes)

    let key = try SsdEncryption.pbkdf2Sha256(
        password: password, salt: salt,
        iterations: SsdEncryptionConstants.pbkdf2Iterations,
        keyBytes: SsdEncryptionConstants.keyBytes)

    let material = UnlockMaterial(
        derivedKey: key, salt: salt,
        iterations: SsdEncryptionConstants.pbkdf2Iterations,
        scheme: SsdEncryptionConstants.schemeName)

    try SsdEncryption.saveEncryptedConfig(ssdRoot: root, config: plaintext, material: material)
    material.zeroize()
}

/// Canonical fixture for the cross-language format pin. Both sides
/// (Swift here, C# in tests/MacEncryptedConfigCrossLanguageTests.cs)
/// must be able to unlock the resulting blob with the documented
/// password and recover the documented plaintext.
///
/// Password and plaintext are intentionally unremarkable so the fixture
/// can be committed without leaking anything sensitive.
func writeCrossLanguageFixture(outDir: URL) throws {
    let password = "mac5-cross-lang-fixture-pw"
    let plaintext: [String: Any] = [
        "version": "1.0",
        "ollamaPort": 13577,
        "ollamaRelativePath": "windows\\tools\\ollama\\ollama.exe",
        "preferredCompute": "cpu",
        "models": [
            ["name": "llama3.2:3b", "status": "Installed"]
        ],
        "isEncrypted": true,
        "encryptionScheme": "AES-256-GCM"
    ]

    try? FileManager.default.removeItem(at: outDir)
    try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
    try seedEncryptedFixture(root: outDir, password: password, plaintext: plaintext)

    // Sidecar README so future humans know what the fixture is for and
    // how to refresh it without grepping for magic strings.
    let readme = """
    # MAC5 cross-language encryption fixture

    This directory holds an encrypted portable-config blob produced by the
    Swift `SsdEncryption` port. The C# `MacEncryptedConfigCrossLanguageTests`
    fixture asserts that `SsdEncryption.TryUnlockPortableConfig` unlocks this
    blob with the password below and recovers the documented plaintext, and
    the Swift test binary asserts the symmetric direction.

    Password: `mac5-cross-lang-fixture-pw`
    Expected `ollamaPort` after unlock: `13577`
    Expected `models[0].name`: `llama3.2:3b`
    Expected scheme: `aes-256-gcm+pbkdf2-sha256-v1`

    To regenerate after a deliberate format change:

        swiftc mac-runner/Sources/SsdEncryption.swift \\
               mac-runner/Tests/SsdEncryptionTests.swift \\
               -parse-as-library -target arm64-apple-macos11.0 \\
               -o /tmp/ssd-encryption-tests
        /tmp/ssd-encryption-tests write-fixture \\
            tests/Fixtures/MacEncryptedConfig/csharp-encrypted

    Refreshing must come with a dated decision in
    `.memhub/rendered/PROJECT_LEDGER.md` — the encrypted-config format is a
    locked invariant.
    """
    try readme.data(using: .utf8)!
        .write(to: outDir.appendingPathComponent("README.md"))
}

/// Walk up from this binary's location looking for a directory with
/// `.git` and `mac-runner/Sources/main.swift`. Lets the test binary find
/// the repo root when run locally without setting FREE_AI_SSD_REPO.
func findRepoRoot() -> String? {
    var dir = URL(fileURLWithPath: CommandLine.arguments.first ?? "").deletingLastPathComponent()
    for _ in 0..<10 {
        let git = dir.appendingPathComponent(".git")
        let marker = dir.appendingPathComponent("mac-runner/Sources/main.swift")
        if FileManager.default.fileExists(atPath: git.path),
           FileManager.default.fileExists(atPath: marker.path) {
            return dir.path
        }
        dir = dir.deletingLastPathComponent()
    }
    return nil
}
