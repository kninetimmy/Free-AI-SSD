import Foundation
#if canImport(Darwin)
import Darwin
#endif

// MARK: - macOS-native drive listing + format
//
// MAC17: Swift owns destructive disk operations (per the 2026-05-06 design
// decision). This file wraps `/usr/sbin/diskutil` directly — no shell, no
// PATH-search risk, explicit argv arrays — and surfaces the candidate
// external-drive list to the SwiftUI flow. The argv shape for `eraseDisk`
// is built by DiskutilFormatCommand.swift, which mirrors the C# sibling
// (DiskutilFormatCommand.cs) for cross-language unit testability.

struct DiskCandidate: Identifiable, Hashable {
    let identifier: String        // canonical "diskN" form
    let displayName: String       // e.g. "FREEAI"
    let mediaName: String?        // hardware label from diskutil info
    let totalSizeBytes: Int64
    let removable: Bool
    let mountPoint: URL?

    var id: String { identifier }

    var sizeDisplay: String {
        let gb = Double(totalSizeBytes) / 1_073_741_824.0
        if gb >= 1.0 { return String(format: "%.1f GB", gb) }
        let mb = Double(totalSizeBytes) / 1_048_576.0
        return String(format: "%.0f MB", mb)
    }
}

enum DiskutilDriveServiceError: Error, LocalizedError {
    case diskutilNotFound
    case listingFailed(String)
    case parseFailed(String)
    case formatFailed(exitCode: Int32, output: String)

    var errorDescription: String? {
        switch self {
        case .diskutilNotFound:
            return "/usr/sbin/diskutil not found. macOS install is incomplete or sandboxed."
        case .listingFailed(let detail):
            return "diskutil list failed: \(detail)"
        case .parseFailed(let detail):
            return "diskutil output parse failed: \(detail)"
        case .formatFailed(let code, let output):
            let trimmed = output.trimmingCharacters(in: .whitespacesAndNewlines)
            return "diskutil eraseDisk exit \(code).\n\(trimmed.isEmpty ? "(no output)" : trimmed)"
        }
    }
}

final class DiskutilDriveService {
    /// List candidate external disks. Filters out internal/system disks
    /// (typically disk0/disk1 on Apple Silicon) by inspecting the
    /// `Internal` flag from `diskutil info`. The candidate list is what the
    /// SwiftUI drive-selection step renders.
    func listExternalCandidates() throws -> [DiskCandidate] {
        // `diskutil list -plist external` returns a list of disks the OS
        // considers external. We feed each disk's identifier into
        // `diskutil info -plist <id>` to get the size + whether it's
        // ejectable (defense in depth — `external` already filters by the
        // most important criterion).
        let listOutput = try runDiskutil(["list", "-plist", "external"])
        guard let plist = try? PropertyListSerialization.propertyList(
            from: listOutput, options: [], format: nil) as? [String: Any]
        else {
            throw DiskutilDriveServiceError.parseFailed("list root is not a dictionary")
        }

        guard let allDisksAndPartitions = plist["AllDisksAndPartitions"] as? [[String: Any]] else {
            return []
        }

        var candidates: [DiskCandidate] = []
        for entry in allDisksAndPartitions {
            guard let identifier = entry["DeviceIdentifier"] as? String else { continue }
            // Skip disk0/disk1 unconditionally as a belt-and-braces guard;
            // they should not appear under "external" but the cost of an
            // accidental destructive op there is catastrophic.
            if identifier == "disk0" || identifier == "disk1" { continue }

            let info = try? infoForDisk(identifier)
            let isInternal = (info?["Internal"] as? Bool) ?? false
            if isInternal { continue }

            let size = (info?["TotalSize"] as? Int64) ?? (info?["Size"] as? Int64) ?? 0
            let mediaName = info?["MediaName"] as? String
            let mountPath = info?["MountPoint"] as? String
            let mountUrl = (mountPath?.isEmpty == false) ? URL(fileURLWithPath: mountPath!) : nil
            let displayName = (info?["VolumeName"] as? String)
                ?? (info?["IORegistryEntryName"] as? String)
                ?? mediaName
                ?? identifier
            let removable = (info?["Ejectable"] as? Bool) ?? true

            candidates.append(DiskCandidate(
                identifier: identifier,
                displayName: displayName,
                mediaName: mediaName,
                totalSizeBytes: size,
                removable: removable,
                mountPoint: mountUrl
            ))
        }
        return candidates
    }

    /// Run `diskutil eraseDisk` with the argv built by DiskutilFormatCommand.
    /// Streams stdout/stderr to the supplied callback. Throws on non-zero
    /// exit. Caller must have already confirmed the destructive intent via
    /// NSAlert — this method does NOT prompt.
    func format(
        diskIdentifier: String,
        label: String,
        fileSystem: String = DiskutilFormatCommand.defaultFileSystem,
        onOutput: @escaping (String) -> Void
    ) throws {
        let built = try DiskutilFormatCommand.build(
            diskIdentifier: diskIdentifier, label: label, fileSystem: fileSystem)

        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: built.fileName)
        proc.arguments = built.arguments

        let stdout = Pipe()
        let stderr = Pipe()
        proc.standardOutput = stdout
        proc.standardError = stderr

        var captured = ""
        let captureLock = NSLock()
        func readPipe(_ pipe: Pipe) {
            pipe.fileHandleForReading.readabilityHandler = { handle in
                let data = handle.availableData
                if data.isEmpty { return }
                if let s = String(data: data, encoding: .utf8) {
                    captureLock.lock()
                    captured.append(s)
                    captureLock.unlock()
                    s.split(whereSeparator: { $0 == "\n" || $0 == "\r" })
                        .forEach { onOutput(String($0)) }
                }
            }
        }
        readPipe(stdout)
        readPipe(stderr)

        try proc.run()
        proc.waitUntilExit()

        // Drain remaining buffers — readabilityHandler can miss the final
        // chunk if the child exits before the handler runs again.
        stdout.fileHandleForReading.readabilityHandler = nil
        stderr.fileHandleForReading.readabilityHandler = nil
        if let tailOut = try? stdout.fileHandleForReading.readToEnd(),
           let s = String(data: tailOut, encoding: .utf8), !s.isEmpty
        {
            captureLock.lock(); captured.append(s); captureLock.unlock()
            s.split(whereSeparator: { $0 == "\n" || $0 == "\r" })
                .forEach { onOutput(String($0)) }
        }
        if let tailErr = try? stderr.fileHandleForReading.readToEnd(),
           let s = String(data: tailErr, encoding: .utf8), !s.isEmpty
        {
            captureLock.lock(); captured.append(s); captureLock.unlock()
            s.split(whereSeparator: { $0 == "\n" || $0 == "\r" })
                .forEach { onOutput(String($0)) }
        }

        if proc.terminationStatus != 0 {
            throw DiskutilDriveServiceError.formatFailed(
                exitCode: proc.terminationStatus, output: captured)
        }
    }

    // MARK: - Private

    private func infoForDisk(_ identifier: String) throws -> [String: Any] {
        let data = try runDiskutil(["info", "-plist", identifier])
        guard let plist = try PropertyListSerialization.propertyList(
            from: data, options: [], format: nil) as? [String: Any]
        else {
            throw DiskutilDriveServiceError.parseFailed("info root is not a dictionary")
        }
        return plist
    }

    /// Runs `/usr/sbin/diskutil` with explicit argv. Refuses if the binary
    /// is missing (sandboxed environments, broken installs). Read-only —
    /// for destructive `eraseDisk` see `format(...)`.
    private func runDiskutil(_ arguments: [String]) throws -> Data {
        let url = URL(fileURLWithPath: DiskutilFormatCommand.diskutilPath)
        if !FileManager.default.fileExists(atPath: url.path) {
            throw DiskutilDriveServiceError.diskutilNotFound
        }

        let proc = Process()
        proc.executableURL = url
        proc.arguments = arguments

        let stdout = Pipe()
        let stderr = Pipe()
        proc.standardOutput = stdout
        proc.standardError = stderr

        try proc.run()
        proc.waitUntilExit()

        let data = stdout.fileHandleForReading.readDataToEndOfFile()
        let errData = stderr.fileHandleForReading.readDataToEndOfFile()

        if proc.terminationStatus != 0 {
            let errStr = String(data: errData, encoding: .utf8) ?? "(no stderr)"
            throw DiskutilDriveServiceError.listingFailed("exit \(proc.terminationStatus): \(errStr)")
        }
        return data
    }
}
