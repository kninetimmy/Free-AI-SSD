import Foundation
import CryptoKit
import CommonCrypto

// MARK: - Constants
//
// These mirror SsdEncryption.cs in the shared/ C# project. The on-disk format
// is fixed by that file; this Swift port must produce byte-compatible output
// and accept Windows-prepped input. Do not invent a new scheme here — if the
// format ever changes, both implementations move together via a new dated
// decision and a versioned scheme name.

enum SsdEncryptionConstants {
    static let schemeName = "aes-256-gcm+pbkdf2-sha256-v1"
    static let stateFileName = "encryption-state.json"
    static let encryptedConfigFileName = "portable-config.encrypted.json"
    static let plaintextConfigFileName = "portable-config.json"
    static let configDirName = "config"

    static let saltBytes = 16
    static let keyBytes = 32
    static let nonceBytes = 12
    static let tagBytes = 16

    /// Default PBKDF2 iteration count. Used only when *creating* a new blob;
    /// unlock always reads the iteration count from the blob's `iterations`
    /// field so older blobs with a different count keep working.
    static let pbkdf2Iterations = 210_000
}

// MARK: - Errors

enum SsdEncryptionError: Error, LocalizedError, Equatable {
    case passwordRequired
    case metadataMissing
    case metadataUnreadable
    case metadataInvalid
    case parametersMissing
    case incorrectPassword
    case decryptedConfigEmpty
    case decryptionFailed
    case saveFailed(String)

    /// Mirrors the Windows error strings byte-for-byte so user-facing docs
    /// apply to both platforms.
    var errorDescription: String? {
        switch self {
        case .passwordRequired:      return "Password is required."
        case .metadataMissing:       return "Encrypted drive metadata is missing."
        case .metadataUnreadable:    return "Encrypted drive metadata is unreadable."
        case .metadataInvalid:       return "Encrypted drive metadata is invalid."
        case .parametersMissing:     return "Encryption parameters are missing."
        case .incorrectPassword:     return "Incorrect password."
        case .decryptedConfigEmpty:  return "Decrypted config is empty."
        case .decryptionFailed:      return "Failed to decrypt portable config."
        case .saveFailed(let why):   return "Failed to save encrypted config: \(why)"
        }
    }
}

// MARK: - UnlockMaterial
//
// Cached PBKDF2 output held in memory while a session is unlocked, so saves
// can re-encrypt without re-deriving. The derived key MUST be zeroed when
// the session locks — call `zeroize()` on app background, app exit, or
// explicit lock action.

final class UnlockMaterial {
    private(set) var derivedKey: Data
    let salt: Data
    let iterations: Int
    let scheme: String

    init(derivedKey: Data, salt: Data, iterations: Int, scheme: String) {
        self.derivedKey = derivedKey
        self.salt = salt
        self.iterations = iterations
        self.scheme = scheme
    }

    /// Overwrites the derived-key buffer with zeros. Call this when locking
    /// the session so the key cannot be recovered from memory snapshots.
    func zeroize() {
        derivedKey.resetBytes(in: 0..<derivedKey.count)
    }

    deinit {
        zeroize()
    }
}

// MARK: - Wire-format Codable shapes
//
// Lowercase property names match C#'s `JsonNamingPolicy.CamelCase` output.

struct EncryptedConfigBlob: Codable {
    var version: Int
    var scheme: String
    var iterations: Int
    var salt: String
    var nonce: String
    var tag: String
    var ciphertext: String
    var createdAtUtc: String
}

struct EncryptionStateFile: Codable {
    var enabled: Bool
    var scheme: String
    var iterations: Int
    var encryptedConfigFile: String
    var updatedAtUtc: String
}

// MARK: - SsdEncryption

enum SsdEncryption {

    // MARK: Public API

    /// True when the state file explicitly marks encryption enabled.
    /// Returns false if the state file is missing or unreadable.
    static func isEncryptionEnabled(ssdRoot: URL) -> Bool {
        let statePath = stateFileURL(ssdRoot: ssdRoot)
        guard FileManager.default.fileExists(atPath: statePath.path) else { return false }
        guard let data = try? Data(contentsOf: statePath),
              let state = try? JSONDecoder().decode(EncryptionStateFile.self, from: data) else {
            return false
        }
        return state.enabled
    }

    /// Fail-closed write-guard helper, mirroring SsdEncryption.IsEffectivelyEncryptedForWriteGuard.
    /// Treats the drive as encrypted unless state explicitly says disabled AND no encrypted
    /// blob exists. This prevents a missing/corrupt state file from green-lighting plaintext writes.
    static func isEffectivelyEncryptedForWriteGuard(ssdRoot: URL) -> Bool {
        let statePath = stateFileURL(ssdRoot: ssdRoot)
        let blobPath  = encryptedBlobURL(ssdRoot: ssdRoot)
        let hasBlob   = FileManager.default.fileExists(atPath: blobPath.path)

        guard FileManager.default.fileExists(atPath: statePath.path) else {
            // No state file: encrypted artifacts alone trigger protection.
            return hasBlob
        }

        guard let data = try? Data(contentsOf: statePath) else {
            return true // unreadable → fail closed
        }
        guard let state = try? JSONDecoder().decode(EncryptionStateFile.self, from: data) else {
            return true // malformed → fail closed
        }
        if state.enabled { return true }
        if hasBlob { return true }  // disabled but blob present → inconsistent → fail closed
        return false
    }

    /// Decrypts the portable config using the provided password and returns
    /// the parsed JSON object as `[String: Any]` (so unknown fields survive a
    /// later save) plus the cached UnlockMaterial. Mirrors the C#
    /// TryUnlockPortableConfigWithMaterial method.
    static func tryUnlockPortableConfig(
        ssdRoot: URL,
        password: String
    ) -> Result<(config: [String: Any], material: UnlockMaterial), SsdEncryptionError> {
        guard !password.isEmpty else { return .failure(.passwordRequired) }

        let statePath = stateFileURL(ssdRoot: ssdRoot)
        let blobPath  = encryptedBlobURL(ssdRoot: ssdRoot)
        guard FileManager.default.fileExists(atPath: statePath.path),
              FileManager.default.fileExists(atPath: blobPath.path) else {
            return .failure(.metadataMissing)
        }

        let stateData: Data
        let blobData: Data
        do {
            stateData = try Data(contentsOf: statePath)
            blobData  = try Data(contentsOf: blobPath)
        } catch {
            return .failure(.metadataUnreadable)
        }

        let state: EncryptionStateFile
        let blob:  EncryptedConfigBlob
        do {
            state = try JSONDecoder().decode(EncryptionStateFile.self, from: stateData)
            blob  = try JSONDecoder().decode(EncryptedConfigBlob.self, from: blobData)
        } catch {
            return .failure(.metadataUnreadable)
        }

        guard state.enabled else { return .failure(.metadataInvalid) }
        guard blob.iterations > 0, !blob.salt.isEmpty else {
            return .failure(.parametersMissing)
        }

        guard let salt       = Data(base64Encoded: blob.salt),
              let nonceData  = Data(base64Encoded: blob.nonce),
              let tag        = Data(base64Encoded: blob.tag),
              let ciphertext = Data(base64Encoded: blob.ciphertext) else {
            return .failure(.decryptionFailed)
        }

        let derivedKey: Data
        do {
            derivedKey = try pbkdf2Sha256(password: password, salt: salt, iterations: blob.iterations,
                                          keyBytes: SsdEncryptionConstants.keyBytes)
        } catch {
            return .failure(.decryptionFailed)
        }

        // AES-GCM open. Authentication failure ≡ wrong password OR tampered
        // ciphertext — we surface this as .incorrectPassword to match Windows.
        let plaintext: Data
        do {
            let nonce = try AES.GCM.Nonce(data: nonceData)
            let sealed = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
            plaintext = try AES.GCM.open(sealed, using: SymmetricKey(data: derivedKey))
        } catch {
            return .failure(.incorrectPassword)
        }

        guard !plaintext.isEmpty,
              let parsed = try? JSONSerialization.jsonObject(with: plaintext) as? [String: Any] else {
            return .failure(.decryptedConfigEmpty)
        }

        let material = UnlockMaterial(
            derivedKey: derivedKey,
            salt: salt,
            iterations: blob.iterations,
            scheme: blob.scheme.isEmpty ? SsdEncryptionConstants.schemeName : blob.scheme
        )
        return .success((parsed, material))
    }

    /// Re-encrypts `config` with a fresh nonce under the cached `material`'s
    /// key and commits the encrypted blob + state file via a two-step atomic
    /// rename. Mirrors `SsdEncryption.SaveEncryptedConfigAsync` including the
    /// rollback-on-state-failure path.
    static func saveEncryptedConfig(
        ssdRoot: URL,
        config: [String: Any],
        material: UnlockMaterial
    ) throws {
        guard material.derivedKey.count == SsdEncryptionConstants.keyBytes else {
            throw SsdEncryptionError.saveFailed("UnlockMaterial is missing a valid 256-bit key.")
        }

        let configDir = configDirURL(ssdRoot: ssdRoot)
        try FileManager.default.createDirectory(at: configDir, withIntermediateDirectories: true)

        let blobPath  = encryptedBlobURL(ssdRoot: ssdRoot)
        let statePath = stateFileURL(ssdRoot: ssdRoot)
        let blobTmp   = blobPath.appendingPathExtension("tmp")
        let stateTmp  = statePath.appendingPathExtension("tmp")
        let blobBak   = blobPath.appendingPathExtension("bak")
        let stateBak  = statePath.appendingPathExtension("bak")

        // Serialize plaintext as canonical JSON. PortableConfig deserialization
        // is tolerant of formatting; .sortedKeys gives us deterministic output
        // for tests, .prettyPrinted matches the C# `WriteIndented = true` shape.
        let plaintext: Data
        do {
            plaintext = try JSONSerialization.data(
                withJSONObject: config,
                options: [.prettyPrinted, .sortedKeys])
        } catch {
            throw SsdEncryptionError.saveFailed("plaintext serialization failed: \(error.localizedDescription)")
        }

        // Fresh nonce per call. AES-GCM nonce reuse with the same key is catastrophic.
        var nonceBytes = [UInt8](repeating: 0, count: SsdEncryptionConstants.nonceBytes)
        let status = SecRandomCopyBytes(kSecRandomDefault, nonceBytes.count, &nonceBytes)
        guard status == errSecSuccess else {
            throw SsdEncryptionError.saveFailed("SecRandomCopyBytes failed (status=\(status))")
        }

        let sealed: AES.GCM.SealedBox
        do {
            let nonce = try AES.GCM.Nonce(data: Data(nonceBytes))
            sealed = try AES.GCM.seal(plaintext,
                                       using: SymmetricKey(data: material.derivedKey),
                                       nonce: nonce)
        } catch {
            throw SsdEncryptionError.saveFailed("AES-GCM seal failed: \(error.localizedDescription)")
        }

        let nowIso = Self.iso8601Formatter.string(from: Date())

        let blob = EncryptedConfigBlob(
            version: 1,
            scheme: material.scheme,
            iterations: material.iterations,
            salt: material.salt.base64EncodedString(),
            nonce: Data(nonceBytes).base64EncodedString(),
            tag: sealed.tag.base64EncodedString(),
            ciphertext: sealed.ciphertext.base64EncodedString(),
            createdAtUtc: nowIso
        )
        let state = EncryptionStateFile(
            enabled: true,
            scheme: material.scheme,
            iterations: material.iterations,
            encryptedConfigFile: SsdEncryptionConstants.encryptedConfigFileName,
            updatedAtUtc: nowIso
        )

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]

        let blobJson:  Data
        let stateJson: Data
        do {
            blobJson  = try encoder.encode(blob)
            stateJson = try encoder.encode(state)
        } catch {
            throw SsdEncryptionError.saveFailed("JSON encode failed: \(error.localizedDescription)")
        }

        // Stage tmp files before touching destinations.
        do {
            try blobJson.write(to: blobTmp,  options: [.atomic])
            try stateJson.write(to: stateTmp, options: [.atomic])
        } catch {
            // Best-effort cleanup of either tmp that might have been written.
            try? FileManager.default.removeItem(at: blobTmp)
            try? FileManager.default.removeItem(at: stateTmp)
            throw SsdEncryptionError.saveFailed("tmp write failed: \(error.localizedDescription)")
        }

        // Clean stale backups from a prior crashed save.
        try? FileManager.default.removeItem(at: blobBak)
        try? FileManager.default.removeItem(at: stateBak)

        let blobExisted  = FileManager.default.fileExists(atPath: blobPath.path)
        let stateExisted = FileManager.default.fileExists(atPath: statePath.path)

        // Commit blob first. replaceItemAt gives us atomic-rename-with-backup
        // for an existing destination; for a fresh destination we just move.
        do {
            if blobExisted {
                _ = try FileManager.default.replaceItemAt(
                    blobPath, withItemAt: blobTmp,
                    backupItemName: blobBak.lastPathComponent)
            } else {
                try FileManager.default.moveItem(at: blobTmp, to: blobPath)
            }
        } catch {
            try? FileManager.default.removeItem(at: blobTmp)
            try? FileManager.default.removeItem(at: stateTmp)
            throw SsdEncryptionError.saveFailed("blob commit failed: \(error.localizedDescription)")
        }

        // Commit state. If this fails, restore the blob from .bak so blob+state
        // can never disagree on iterations/scheme — the whole point of the
        // two-file atomic commit.
        do {
            if stateExisted {
                _ = try FileManager.default.replaceItemAt(
                    statePath, withItemAt: stateTmp,
                    backupItemName: stateBak.lastPathComponent)
            } else {
                try FileManager.default.moveItem(at: stateTmp, to: statePath)
            }
        } catch {
            // Roll back the blob rename.
            do {
                if blobExisted, FileManager.default.fileExists(atPath: blobBak.path) {
                    _ = try FileManager.default.replaceItemAt(
                        blobPath, withItemAt: blobBak,
                        backupItemName: nil)
                } else if !blobExisted {
                    try? FileManager.default.removeItem(at: blobPath)
                }
            } catch {
                // Best-effort rollback; fall through to surface the original
                // state-rename error.
            }
            try? FileManager.default.removeItem(at: stateTmp)
            throw SsdEncryptionError.saveFailed("state commit failed: \(error.localizedDescription)")
        }

        // Both replaces succeeded — clean up backups.
        try? FileManager.default.removeItem(at: blobBak)
        try? FileManager.default.removeItem(at: stateBak)
    }

    // MARK: Plaintext migration

    enum PlaintextMigrationResult {
        case noPlaintext
        case mergedFromPlaintext(config: [String: Any])
        case stalePlaintextRemoved
        case migrationFailed(reason: String)
    }

    /// Mirrors `TryMigratePlaintextAsync`. Call immediately after a successful
    /// unlock so the drive never accumulates plaintext secrets.
    @discardableResult
    static func tryMigratePlaintext(
        ssdRoot: URL,
        material: UnlockMaterial,
        log: ((String) -> Void)? = nil
    ) -> PlaintextMigrationResult {
        let plaintextPath = configDirURL(ssdRoot: ssdRoot)
            .appendingPathComponent(SsdEncryptionConstants.plaintextConfigFileName)
        let blobPath = encryptedBlobURL(ssdRoot: ssdRoot)

        guard FileManager.default.fileExists(atPath: plaintextPath.path) else {
            return .noPlaintext
        }

        let plaintextMtime = (try? FileManager.default.attributesOfItem(atPath: plaintextPath.path)[.modificationDate] as? Date) ?? .distantPast
        let blobMtime: Date = {
            guard FileManager.default.fileExists(atPath: blobPath.path),
                  let m = try? FileManager.default.attributesOfItem(atPath: blobPath.path)[.modificationDate] as? Date else {
                return .distantPast
            }
            return m
        }()

        if plaintextMtime > blobMtime {
            // Branch A: plaintext is newer — merge into encrypted, delete plaintext.
            do {
                let data = try Data(contentsOf: plaintextPath)
                guard let parsed = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                    log?("[Migration] Plaintext config found but is not a JSON object — skipping migration, plaintext preserved.")
                    return .migrationFailed(reason: "plaintext config is not a JSON object")
                }
                try saveEncryptedConfig(ssdRoot: ssdRoot, config: parsed, material: material)
                try? FileManager.default.removeItem(at: plaintextPath)
                log?("[Migration] Plaintext config was newer — merged into encrypted blob, plaintext deleted.")
                return .mergedFromPlaintext(config: parsed)
            } catch {
                log?("[Migration] Failed to absorb plaintext into encrypted blob: \(error.localizedDescription). Plaintext preserved.")
                return .migrationFailed(reason: error.localizedDescription)
            }
        } else {
            // Branch B: encrypted is authoritative — discard stale plaintext.
            try? FileManager.default.removeItem(at: plaintextPath)
            log?("[Migration] Stale plaintext removed — encrypted is authoritative.")
            return .stalePlaintextRemoved
        }
    }

    // MARK: Path helpers

    static func configDirURL(ssdRoot: URL) -> URL {
        ssdRoot.appendingPathComponent(SsdEncryptionConstants.configDirName, isDirectory: true)
    }

    static func encryptedBlobURL(ssdRoot: URL) -> URL {
        configDirURL(ssdRoot: ssdRoot)
            .appendingPathComponent(SsdEncryptionConstants.encryptedConfigFileName)
    }

    static func stateFileURL(ssdRoot: URL) -> URL {
        configDirURL(ssdRoot: ssdRoot)
            .appendingPathComponent(SsdEncryptionConstants.stateFileName)
    }

    // MARK: Crypto helpers

    /// PBKDF2-HMAC-SHA256 via CommonCrypto. The macOS SDK ships CommonCrypto
    /// with a module map, so a single-file swiftc build can `import CommonCrypto`
    /// without a bridging header.
    static func pbkdf2Sha256(password: String, salt: Data, iterations: Int, keyBytes: Int) throws -> Data {
        guard let passwordData = password.data(using: .utf8) else {
            throw SsdEncryptionError.decryptionFailed
        }
        var derived = Data(count: keyBytes)
        let saltBytes = [UInt8](salt)
        let pwdBytes  = [UInt8](passwordData)

        let status: Int32 = derived.withUnsafeMutableBytes { keyBuf in
            CCKeyDerivationPBKDF(
                CCPBKDFAlgorithm(kCCPBKDF2),
                pwdBytes, pwdBytes.count,
                saltBytes, saltBytes.count,
                CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256),
                UInt32(iterations),
                keyBuf.bindMemory(to: UInt8.self).baseAddress, keyBytes
            )
        }
        guard status == kCCSuccess else {
            throw SsdEncryptionError.decryptionFailed
        }
        return derived
    }

    // MARK: Misc

    /// ISO-8601 with fractional seconds — close enough to C#'s
    /// `DateTime.UtcNow` round-trip for the purposes of metadata fields.
    private static let iso8601Formatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        f.timeZone = TimeZone(secondsFromGMT: 0)
        return f
    }()
}
