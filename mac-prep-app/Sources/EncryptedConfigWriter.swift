import Foundation

// MARK: - Mac PrepApp encrypted-config write path
//
// MAC17: thin wrapper over SsdEncryption (the MAC5 Swift port shared with
// mac-runner). The PrepApp runs at drive-creation time, so it owns the
// *first* encrypted-config write — there's no existing plaintext or
// ciphertext to migrate. A subsequent Mac Runner unlock is what proves
// the write was readable; the cross-language fixture test
// (MacEncryptedConfigCrossLanguageTests + the new swift-prep-encrypted/
// fixture) proves Windows can read it too.
//
// Plaintext invariant from MAC5 carried into MAC17: the in-memory
// PortableConfig dictionary built by the SwiftUI flow never touches disk
// in plaintext form. SsdEncryption.write encrypts in-memory and the
// derived key is zeroized via SsdEncryption's own deinit / explicit lock.

enum EncryptedConfigWriterError: Error, LocalizedError {
    case emptyPassphrase
    case writeFailed(String)

    var errorDescription: String? {
        switch self {
        case .emptyPassphrase:
            return "Encryption passphrase cannot be empty."
        case .writeFailed(let detail):
            return "Encrypted config write failed: \(detail)"
        }
    }
}

/// Minimal PortableConfig payload the MAC17 PrepApp emits for the very
/// first encrypted-config write. Keys mirror shared/PortableConfig.cs's
/// JSON shape (camelCase). The MAC5 cross-language fixture test pins the
/// JSON shape; any drift here fails Windows CI.
struct InitialPortableConfigPayload {
    var ollamaHost: String = "http://127.0.0.1:11434"
    var defaultModel: String?
    var networkModeEnabled: Bool = false
    var networkBindAddress: String = "127.0.0.1"
    var networkPort: Int = 5800
    var networkRequireApiKey: Bool = false
    var networkApiKey: String = ""

    /// Render as the `[String: Any]` dictionary SsdEncryption expects.
    /// Keys must stay camelCase — PortableConfig.SaveAsync uses
    /// JsonNamingPolicy.CamelCase and the cross-language fixture test
    /// pins this shape.
    func asDictionary() -> [String: Any] {
        var dict: [String: Any] = [
            "ollamaHost": ollamaHost,
            "networkModeEnabled": networkModeEnabled,
            "networkBindAddress": networkBindAddress,
            "networkPort": networkPort,
            "networkRequireApiKey": networkRequireApiKey,
            "networkApiKey": networkApiKey,
            "models": [],
        ]
        if let model = defaultModel, !model.isEmpty {
            dict["defaultModel"] = model
        }
        return dict
    }
}

final class EncryptedConfigWriter {
    /// Write the initial encrypted PortableConfig at <ssdRoot>/config/.
    /// Derives a fresh PBKDF2 key from the passphrase, builds an
    /// UnlockMaterial, and invokes SsdEncryption.saveEncryptedConfig
    /// (the two-file atomic commit). On any failure the rollback path
    /// in saveEncryptedConfig restores the prior state — and on a
    /// completely-fresh SSD that's "no encrypted-config exists yet."
    func writeInitialEncryptedConfig(
        ssdRoot: URL,
        payload: InitialPortableConfigPayload,
        passphrase: String
    ) throws {
        let trimmed = passphrase.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { throw EncryptedConfigWriterError.emptyPassphrase }

        do {
            // Generate a fresh 16-byte salt via SecRandomCopyBytes — the
            // OS RNG. SsdEncryption.tryUnlockPortableConfig reads the
            // salt back from the encrypted blob so this only matters at
            // create time.
            var saltBytes = [UInt8](repeating: 0, count: SsdEncryptionConstants.saltBytes)
            let status = SecRandomCopyBytes(kSecRandomDefault, saltBytes.count, &saltBytes)
            guard status == errSecSuccess else {
                throw EncryptedConfigWriterError.writeFailed("SecRandomCopyBytes failed (status=\(status))")
            }
            let salt = Data(saltBytes)

            let derivedKey = try SsdEncryption.pbkdf2Sha256(
                password: passphrase,
                salt: salt,
                iterations: SsdEncryptionConstants.pbkdf2Iterations,
                keyBytes: SsdEncryptionConstants.keyBytes)

            let material = UnlockMaterial(
                derivedKey: derivedKey,
                salt: salt,
                iterations: SsdEncryptionConstants.pbkdf2Iterations,
                scheme: "pbkdf2-sha256")

            // Defense-in-depth: zeroize the derived key the moment the
            // write returns, regardless of success/failure. The
            // UnlockMaterial deinit also zeroizes, but explicit is good
            // here — the prep flow doesn't keep the key for later saves.
            defer { material.zeroize() }

            try SsdEncryption.saveEncryptedConfig(
                ssdRoot: ssdRoot,
                config: payload.asDictionary(),
                material: material)
        } catch let error as SsdEncryptionError {
            throw EncryptedConfigWriterError.writeFailed(error.localizedDescription)
        } catch let error as EncryptedConfigWriterError {
            throw error
        } catch {
            throw EncryptedConfigWriterError.writeFailed(error.localizedDescription)
        }
    }
}
