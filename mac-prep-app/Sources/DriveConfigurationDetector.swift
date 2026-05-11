import Foundation

// MARK: - State enum
//
// Mirrors shared/DriveConfigurationDetector.cs. The C# implementation is
// the canonical source — this Swift port must produce byte-identical
// snapshots for the same on-disk inputs. Detection is pure file-presence
// (no decrypt), safe to call on any candidate drive.

enum DriveConfigurationState: String, Equatable {
    /// No Free-AI-SSD config marker on disk. Drive may still contain
    /// foreign data (e.g. a user's own Ollama install) — deliberately
    /// not enough to claim it as ours.
    case unconfigured

    /// Our config marker is present, but no model manifests have been
    /// written yet. Realistic case: user formatted and finalized but
    /// bailed before pulling a starter model.
    case configuredEmpty

    /// Our config marker is present AND at least one model manifest
    /// exists on disk.
    case fullyConfigured
}

// MARK: - Snapshot

/// Snapshot of an SSD's configuration state from PrepApp's perspective.
/// Fields derive from file-presence probes only — no config contents are
/// read or decrypted.
struct DriveConfigurationSnapshot: Equatable {
    let state: DriveConfigurationState
    let hasOurConfig: Bool
    let isConfigEncrypted: Bool
    let hasModels: Bool
    let modelManifestCount: Int

    /// Empty snapshot returned for nil, missing, or unconfigured drives.
    static let empty = DriveConfigurationSnapshot(
        state: .unconfigured,
        hasOurConfig: false,
        isConfigEncrypted: false,
        hasModels: false,
        modelManifestCount: 0)
}

// MARK: - Detector

enum DriveConfigurationDetector {
    /// Cap on manifest enumeration. Counts inform UI copy ("3 models on
    /// this drive") but aren't load-bearing — exceeding the cap simply
    /// reports the cap, which is still enough to know `hasModels` is true.
    /// Mirrors `ManifestEnumerationCap` in shared/DriveConfigurationDetector.cs.
    static let manifestEnumerationCap = 256

    /// Inspects `ssdRoot` and returns a snapshot of its configuration
    /// state. Safe to call with nil or a missing path — each maps to
    /// `DriveConfigurationSnapshot.empty`.
    static func detect(_ ssdRoot: URL?) -> DriveConfigurationSnapshot {
        guard let ssdRoot else { return .empty }

        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: ssdRoot.path, isDirectory: &isDir),
              isDir.boolValue else {
            return .empty
        }

        let configDir = ssdRoot.appendingPathComponent(
            SsdEncryptionConstants.configDirName, isDirectory: true)
        let plaintextPath = configDir.appendingPathComponent(
            SsdEncryptionConstants.plaintextConfigFileName)
        let encryptedPath = configDir.appendingPathComponent(
            SsdEncryptionConstants.encryptedConfigFileName)

        let hasPlaintext = fm.fileExists(atPath: plaintextPath.path)
        let hasEncrypted = fm.fileExists(atPath: encryptedPath.path)
        let hasOurConfig = hasPlaintext || hasEncrypted
        let isConfigEncrypted = hasEncrypted && !hasPlaintext

        guard hasOurConfig else {
            // Foreign-data guard: never claim a drive as ours based on
            // model manifests alone. Marker is our config file.
            return .empty
        }

        let manifestsRoot = ssdRoot
            .appendingPathComponent("models", isDirectory: true)
            .appendingPathComponent("manifests", isDirectory: true)
        let manifestCount = countManifests(at: manifestsRoot)
        let hasModels = manifestCount > 0

        return DriveConfigurationSnapshot(
            state: hasModels ? .fullyConfigured : .configuredEmpty,
            hasOurConfig: true,
            isConfigEncrypted: isConfigEncrypted,
            hasModels: hasModels,
            modelManifestCount: manifestCount)
    }

    private static func countManifests(at manifestsRoot: URL) -> Int {
        let fm = FileManager.default
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: manifestsRoot.path, isDirectory: &isDir),
              isDir.boolValue,
              let enumerator = fm.enumerator(
                at: manifestsRoot,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles]) else {
            return 0
        }

        var count = 0
        for case let url as URL in enumerator {
            // Only count regular files (matches the C# Directory.EnumerateFiles
            // semantics — directories are skipped automatically).
            let values = try? url.resourceValues(forKeys: [.isRegularFileKey])
            guard values?.isRegularFile == true else { continue }
            count += 1
            if count >= manifestEnumerationCap { break }
        }
        return count
    }
}
