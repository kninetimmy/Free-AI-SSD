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
/// first encrypted-config write. Field shape mirrors
/// shared/PortableConfig.cs — only fields PortableConfig actually has.
/// The MAC17 cross-language fixture test pins this shape; any drift
/// here fails Windows CI.
///
/// Defaults match PortableConfig's defaults so a Windows Runner unlock
/// of the freshly-prepped SSD reads the same values it would after
/// constructing a default PortableConfig in C#.
struct InitialPortableConfigPayload {
    var ollamaPort: Int = 11434
    var networkModeEnabled: Bool = false
    var networkBindAddress: String = "127.0.0.1"
    var networkPort: Int = 41555
    var networkRequireApiKey: Bool = true
    /// MAC34: a fresh 32-byte random hex key generated at first-write.
    /// Pre-MAC34 this defaulted to `""`, which made the LAN API path
    /// fail-closed with `503 API key is required by configuration but
    /// not set on host` the moment a user enabled Network Mode — there
    /// was no UI to set the key and `""` triggers `RunnerLocalApiService`'s
    /// fail-closed guard. Generating one at prep time means the key is
    /// always non-empty for LAN exposure; loopback chat skips the gate
    /// regardless via the MAC34 loopback bypass in
    /// `RunnerLocalApiService`.
    var networkApiKey: String = Self.generateRandomApiKey()
    var preferredCompute: String = "cpu"
    /// C27 Stage 3: optional Hugging Face access token. Defaults to nil
    /// (anonymous mode). When the user types a token into the PrepApp
    /// SecureField at prep time, this field carries it into the
    /// `huggingFaceToken` JSON key on the initial config write. Rides
    /// the AES-256-GCM seal when encryption is on; plaintext otherwise.
    var huggingFaceToken: String? = nil

    /// Opt-in PDF-image OCR at ingest. Maps to PortableConfig.OcrEnabled
    /// (default false in C#). Emitted only when true so the JSON stays minimal
    /// and an un-opted-in drive takes the C# default on deserialization.
    var ocrEnabled: Bool = false

    /// Render as the `[String: Any]` dictionary SsdEncryption expects.
    /// Keys must stay camelCase — PortableConfig.SaveAsync uses
    /// JsonNamingPolicy.CamelCase. PortableConfig deserialization is
    /// permissive, so missing fields take their C# defaults.
    func asDictionary() -> [String: Any] {
        var dict: [String: Any] = [
            "ollamaPort": ollamaPort,
            "networkModeEnabled": networkModeEnabled,
            "networkBindAddress": networkBindAddress,
            "networkPort": networkPort,
            "networkRequireApiKey": networkRequireApiKey,
            "networkApiKey": networkApiKey,
            "preferredCompute": preferredCompute,
            "models": [],
        ]
        // C27 Stage 3: emit huggingFaceToken only when set so empty input
        // leaves the field absent in JSON; PortableConfig.HuggingFaceToken
        // defaults to null on deserialization.
        if let token = huggingFaceToken, !token.isEmpty {
            dict["huggingFaceToken"] = token
        }
        // Emit ocrEnabled only when opted in; absence ⇒ C# PortableConfig default (false).
        if ocrEnabled {
            dict["ocrEnabled"] = true
        }
        return dict
    }

    /// MAC34: 32 bytes from `SecRandomCopyBytes`, hex-encoded. Falls back
    /// to a UUID-derived hex if the OS RNG ever fails — that is a defense
    /// posture, not a real fallback path; on Apple Silicon SecRandomCopyBytes
    /// has no documented failure mode.
    static func generateRandomApiKey() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        if status == errSecSuccess {
            return bytes.map { String(format: "%02x", $0) }.joined()
        }
        let fallback = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        return fallback + fallback
    }
}

// `@unchecked Sendable`: stateless (no instance vars). Marked so
// PrepViewModel can capture the writer in `Task.detached` for
// MAC17a #3 — hopping the PBKDF2 + AES-GCM seal off `@MainActor`
// so the SwiftUI spinner ticks during encryption setup.
final class EncryptedConfigWriter: @unchecked Sendable {
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

            // The scheme MUST be SsdEncryptionConstants.schemeName so the
            // C# unlock path accepts the blob (asserted via the
            // MacEncryptedConfigCrossLanguageTests cross-language fixture
            // test). Hardcoding a different value here would silently
            // ship blobs that Windows Runner refuses.
            let material = UnlockMaterial(
                derivedKey: derivedKey,
                salt: salt,
                iterations: SsdEncryptionConstants.pbkdf2Iterations,
                scheme: SsdEncryptionConstants.schemeName)

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
