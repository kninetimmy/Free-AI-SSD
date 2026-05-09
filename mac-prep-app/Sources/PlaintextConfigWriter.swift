import Foundation

// MARK: - Mac PrepApp plaintext-config write path
//
// MAC30: encryption became opt-in (default OFF) on both PrepApps. When the
// user keeps the toggle off, the PrepApp must produce a readable
// portable-config.json on the SSD instead of the encrypted blob the
// EncryptedConfigWriter would emit. This writer is the Swift sibling of
// EncryptedConfigWriter and writes byte-compatible JSON that
// shared/PortableConfig.cs deserializes (camelCase keys via JsonNamingPolicy).
//
// Plaintext invariant (narrowed at MAC30): "no API key written in plaintext."
// The InitialPortableConfigPayload generates a random networkApiKey for the
// encrypted path because the encrypted blob is the only readable surface for
// it. On the plaintext path we explicitly clear the API key field before
// writing — Network Mode + RequireApiKey is incompatible with plaintext
// config and is enforced server-side by the
// `NetworkModeEncryptionRequiredMessage` guard in shared/PortableConfig.cs.
// If the user later wants Network Mode they must enable encryption first.

enum PlaintextConfigWriterError: Error, LocalizedError {
    case writeFailed(String)

    var errorDescription: String? {
        switch self {
        case .writeFailed(let detail):
            return "Plaintext config write failed: \(detail)"
        }
    }
}

// `@unchecked Sendable`: stateless. Marked so PrepViewModel can capture
// the writer in `Task.detached` for parity with EncryptedConfigWriter
// (the encrypted path hops off @MainActor for PBKDF2; plaintext doesn't
// strictly need it but matching shape keeps the call site uniform).
final class PlaintextConfigWriter: @unchecked Sendable {
    /// Write the initial plaintext PortableConfig at <ssdRoot>/config/portable-config.json.
    /// Strips `networkApiKey` from the payload before serialization so a
    /// LAN secret never lands on disk in cleartext.
    func writeInitialPlaintextConfig(
        ssdRoot: URL,
        payload: InitialPortableConfigPayload
    ) throws {
        do {
            var dict = payload.asDictionary()
            // MAC30 plaintext invariant: clear the API key before writing.
            // PortableConfig defaults networkRequireApiKey=true, but the
            // companion `networkModeEnabled` defaults to false, so a user
            // who never enables Network Mode never sees the encryption gate.
            dict["networkApiKey"] = ""

            let configDir = ssdRoot
                .appendingPathComponent(SsdEncryptionConstants.configDirName, isDirectory: true)
            try FileManager.default.createDirectory(
                at: configDir, withIntermediateDirectories: true)

            let configURL = configDir.appendingPathComponent(
                SsdEncryptionConstants.plaintextConfigFileName)

            // pretty-printed + sortedKeys so a human reading the file sees a
            // stable, diffable layout. PortableConfig's JsonSerializerOptions
            // accepts either; matches what `mac-runner` writes via its
            // own .prettyPrinted save path in main.swift:537.
            let data = try JSONSerialization.data(
                withJSONObject: dict,
                options: [.prettyPrinted, .sortedKeys])
            try data.write(to: configURL, options: .atomic)
        } catch let error as PlaintextConfigWriterError {
            throw error
        } catch {
            throw PlaintextConfigWriterError.writeFailed(error.localizedDescription)
        }
    }
}
