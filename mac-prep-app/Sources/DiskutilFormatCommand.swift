import Foundation

// MARK: - Swift parity-pin of the C# DiskutilFormatCommand
//
// The Swift sibling of shared/Services/DiskutilFormatCommand.cs. Pure
// builder — turns (diskIdentifier, label, fileSystem) into a safe argv
// array for /usr/sbin/diskutil eraseDisk. The C# tests
// (DiskutilFormatCommandTests.cs) pin the expected argv shape; this
// Swift implementation produces the same shape so a drift on either side
// fails CI before hitting the destructive code path.
//
// Why duplicate the builder in Swift instead of routing through the
// sidecar: per the MAC17 design decision, Swift owns destructive disk
// ops so the macOS authorization prompt comes from a SwiftUI app rather
// than a headless sidecar. The C# version exists to make the argv shape
// reviewable and unit-testable from Windows CI.

enum DiskutilFormatCommandError: Error, LocalizedError {
    case emptyIdentifier
    case malformedIdentifier(String)
    case missingIndex(String)
    case sliceSeparatorMustBeS(String)
    case invalidSliceIndex(String)
    case unsupportedFileSystem(String)

    var errorDescription: String? {
        switch self {
        case .emptyIdentifier:
            return "Disk identifier is empty."
        case .malformedIdentifier(let id):
            return "Disk identifier must start with 'disk' (got '\(id)')."
        case .missingIndex(let id):
            return "Disk identifier missing index (got '\(id)')."
        case .sliceSeparatorMustBeS(let id):
            return "Disk identifier slice separator must be 's' (got '\(id)')."
        case .invalidSliceIndex(let id):
            return "Disk identifier has invalid slice index (got '\(id)')."
        case .unsupportedFileSystem(let fs):
            return "Unsupported file system '\(fs)'. Supported: ExFAT."
        }
    }
}

enum DiskutilFormatCommand {
    static let maxLabelLength = 15
    static let defaultFileSystem = "ExFAT"
    static let diskutilPath = "/usr/sbin/diskutil"

    struct Built: Equatable {
        let fileName: String
        let arguments: [String]
        let diskIdentifier: String
    }

    /// Builds the diskutil eraseDisk command. Throws on invalid input. The
    /// caller is responsible for refusing the system disk (typically
    /// disk0/disk1) — that is a policy decision in the candidate listing,
    /// not in this pure builder.
    static func build(diskIdentifier: String, label: String, fileSystem: String = defaultFileSystem) throws -> Built {
        let canonicalIdentifier = try parseDiskIdentifier(diskIdentifier)
        let normalizedFs = try normalizeFileSystem(fileSystem)
        let sanitizedLabel = sanitizeLabel(label)

        // diskutil refuses an empty label argument, so substitute a single
        // space if the caller's label sanitized to empty. The volume will
        // still be readable; the user can rename it later.
        let labelArg = sanitizedLabel.isEmpty ? " " : sanitizedLabel

        let arguments: [String] = [
            "eraseDisk",
            normalizedFs,
            labelArg,
            "MBR",
            canonicalIdentifier,
        ]

        return Built(fileName: diskutilPath, arguments: arguments, diskIdentifier: canonicalIdentifier)
    }

    static func parseDiskIdentifier(_ identifier: String) throws -> String {
        let trimmedRaw = identifier.trimmingCharacters(in: .whitespaces)
        guard !trimmedRaw.isEmpty else { throw DiskutilFormatCommandError.emptyIdentifier }

        var trimmed = trimmedRaw
        if trimmed.hasPrefix("/dev/") {
            trimmed = String(trimmed.dropFirst("/dev/".count))
        }

        guard trimmed.hasPrefix("disk") else {
            throw DiskutilFormatCommandError.malformedIdentifier(identifier)
        }

        let rest = String(trimmed.dropFirst("disk".count))
        guard !rest.isEmpty else {
            throw DiskutilFormatCommandError.missingIndex(identifier)
        }

        // Parse <digits>[s<digits>]
        var i = rest.startIndex
        while i < rest.endIndex, rest[i].isNumber { i = rest.index(after: i) }
        guard i != rest.startIndex else {
            throw DiskutilFormatCommandError.missingIndex(identifier)
        }

        if i == rest.endIndex { return trimmed }

        guard rest[i] == "s" else {
            throw DiskutilFormatCommandError.sliceSeparatorMustBeS(identifier)
        }

        let sliceStart = rest.index(after: i)
        var j = sliceStart
        while j < rest.endIndex, rest[j].isNumber { j = rest.index(after: j) }
        if j == sliceStart || j != rest.endIndex {
            throw DiskutilFormatCommandError.invalidSliceIndex(identifier)
        }

        return trimmed
    }

    static func normalizeFileSystem(_ fileSystem: String) throws -> String {
        let trimmed = fileSystem.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return defaultFileSystem }

        let upper = trimmed.uppercased()
        switch upper {
        case "EXFAT":
            // diskutil's eraseDisk format token is the literal "ExFAT"
            // (mixed case). Emit canonical casing regardless of input.
            return "ExFAT"
        default:
            // APFS and NTFS are deferred / Windows-only and the C# sibling
            // emits more specific error messages for them. Swift collapses
            // to a single message — the SwiftUI flow gates filesystem
            // choice at the UI layer so we don't expect APFS/NTFS to reach
            // this builder in practice.
            throw DiskutilFormatCommandError.unsupportedFileSystem(fileSystem)
        }
    }

    /// exFAT label sanitization mirrored from the C# sibling: strip control
    /// characters, refuse path separators and metacharacters that would
    /// confuse Windows readers when the SSD travels cross-platform, trim,
    /// cap at 15 chars (exFAT spec).
    static func sanitizeLabel(_ label: String) -> String {
        let trimmed = label.trimmingCharacters(in: .whitespaces)
        if trimmed.isEmpty { return "" }

        let blocked: Set<Character> = ["/", "\\", ":", "*", "?", "\"", "<", ">", "|"]

        var cleaned = ""
        for ch in trimmed {
            if ch.asciiValue != nil, ch.isASCII, ch.asciiValue! < 0x20 || ch.asciiValue! == 0x7f {
                continue // control char
            }
            if !ch.isASCII {
                // Mirror the C# implementation's char.IsControl posture:
                // drop control chars; allow non-ASCII printables. exFAT
                // labels can hold UTF-16, but cross-platform tooling
                // (Windows Explorer) can choke — keep ASCII only.
                continue
            }
            if blocked.contains(ch) { continue }
            cleaned.append(ch)
            if cleaned.count >= maxLabelLength { break }
        }
        return cleaned
    }
}
