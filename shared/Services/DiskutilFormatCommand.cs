using System.Collections.Generic;

namespace FreeAiSsd.Shared.Services;

/// <summary>
/// MAC17 sibling of <see cref="DriveFormatCommand"/>. Pure builder that turns a
/// macOS diskutil identifier / label / filesystem into a safe
/// <c>/usr/sbin/diskutil eraseDisk</c> argv array. The Swift mac-prep-app
/// invokes the result via Process with explicit argument arrays — never a
/// shell — so this builder is the parity-pin reviewers (and Windows CI) can
/// inspect to verify the destructive command shape.
///
/// The Windows sibling uses an env-var smuggle for the volume label because
/// PowerShell mixes scripting and argv. diskutil takes the label as a plain
/// argv token, so no env-var indirection is required — which is precisely
/// why the Mac flow is safer to keep in argv form to begin with.
/// </summary>
public static class DiskutilFormatCommand
{
    /// <summary>
    /// exFAT volume-label limit. The Microsoft exFAT spec allows up to 15
    /// UTF-16 code units; we cap at 15 ASCII characters here because diskutil
    /// rejects multi-byte labels on some macOS versions, and the WPF
    /// PrepApp's label sanitizer caps at 32 (NTFS limit) — using 15 on the
    /// Mac side guarantees any label that survives Mac sanitization also
    /// survives a Windows-host cross-platform flow.
    /// </summary>
    public const int MaxLabelLength = 15;

    public const string DefaultFileSystem = "ExFAT";

    /// <summary>
    /// Absolute system path to diskutil. Hardcoded so a malicious
    /// <c>diskutil</c> placed earlier on PATH cannot intercept the
    /// destructive call. Matches the security stance of
    /// <see cref="DriveFormatCommand"/>'s absolute powershell.exe path.
    /// </summary>
    public const string DiskutilPath = "/usr/sbin/diskutil";

    public readonly record struct Built(
        string FileName,
        IReadOnlyList<string> Arguments,
        string DiskIdentifier);

    /// <summary>
    /// Builds the diskutil eraseDisk command. Throws ArgumentException on
    /// invalid input.
    /// </summary>
    /// <param name="diskIdentifier">
    /// diskutil identifier like <c>disk2</c> or <c>disk2s1</c>. Caller is
    /// responsible for refusing the system disk (typically <c>disk0</c> or
    /// <c>disk1</c> on Apple Silicon) — that is a policy decision and
    /// belongs in the candidate-listing path, not in this pure builder.
    /// </param>
    /// <param name="label">Volume label (will be sanitized; empty allowed).</param>
    /// <param name="fileSystem">"ExFAT" only for MAC17 MVP (case-insensitive). APFS deferred.</param>
    public static Built Build(string diskIdentifier, string label, string fileSystem)
    {
        var canonicalIdentifier = ParseDiskIdentifier(diskIdentifier);
        var normalizedFs = NormalizeFileSystem(fileSystem);
        var sanitizedLabel = SanitizeLabel(label);

        // diskutil eraseDisk format name [APM|MBR|GPT] device
        //
        // MBR is the right partition scheme for cross-platform exFAT external
        // drives — GPT can confuse some older Windows configurations, while
        // APM is legacy PowerPC-era. diskutil's exFAT default is MBR but we
        // pin it explicitly to remove ambiguity on the destructive path.
        //
        // diskutil refuses an empty label argument, so substitute a single
        // space if the caller's label sanitized to empty. The volume will
        // still be readable; the user can rename it later.
        var labelArg = sanitizedLabel.Length == 0 ? " " : sanitizedLabel;

        var args = new List<string>
        {
            "eraseDisk",
            normalizedFs,
            labelArg,
            "MBR",
            canonicalIdentifier,
        };

        return new Built(DiskutilPath, args, canonicalIdentifier);
    }

    /// <summary>
    /// Diagnostic-only helper. Mirrors <see cref="DriveFormatCommand.Describe"/>.
    /// </summary>
    public static string Describe(Built built)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("FileName       : ").AppendLine(built.FileName);
        sb.Append("DiskIdentifier : ").AppendLine(built.DiskIdentifier);
        sb.Append("Arguments      : ").AppendLine(string.Join(" ", built.Arguments));
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Validates and canonicalizes a diskutil identifier. Accepts:
    ///   <c>disk2</c>          (whole disk)
    ///   <c>disk2s1</c>        (partition slice)
    ///   <c>/dev/disk2</c>     (device-node form)
    ///   <c>/dev/disk2s1</c>   (device-node + slice)
    /// All forms canonicalize to the bare <c>diskN[sM]</c> form because
    /// diskutil accepts that universally and it produces the cleanest argv.
    /// </summary>
    public static string ParseDiskIdentifier(string diskIdentifier)
    {
        if (string.IsNullOrWhiteSpace(diskIdentifier))
            throw new System.ArgumentException("Disk identifier is empty.", nameof(diskIdentifier));

        var trimmed = diskIdentifier.Trim();
        if (trimmed.StartsWith("/dev/", System.StringComparison.Ordinal))
            trimmed = trimmed.Substring("/dev/".Length);

        if (!trimmed.StartsWith("disk", System.StringComparison.Ordinal))
            throw new System.ArgumentException(
                $"Disk identifier must start with 'disk' (got '{diskIdentifier}').",
                nameof(diskIdentifier));

        var rest = trimmed.Substring("disk".Length);
        if (rest.Length == 0)
            throw new System.ArgumentException(
                $"Disk identifier missing index (got '{diskIdentifier}').",
                nameof(diskIdentifier));

        // Parse <digits>[s<digits>]
        var i = 0;
        while (i < rest.Length && char.IsDigit(rest[i])) i++;
        if (i == 0)
            throw new System.ArgumentException(
                $"Disk identifier must have a numeric index after 'disk' (got '{diskIdentifier}').",
                nameof(diskIdentifier));

        if (i == rest.Length)
            return trimmed;

        if (rest[i] != 's')
            throw new System.ArgumentException(
                $"Disk identifier slice separator must be 's' (got '{diskIdentifier}').",
                nameof(diskIdentifier));

        var sliceStart = i + 1;
        var j = sliceStart;
        while (j < rest.Length && char.IsDigit(rest[j])) j++;
        if (j == sliceStart || j != rest.Length)
            throw new System.ArgumentException(
                $"Disk identifier has invalid slice index (got '{diskIdentifier}').",
                nameof(diskIdentifier));

        return trimmed;
    }

    internal static string NormalizeFileSystem(string fileSystem)
    {
        if (string.IsNullOrWhiteSpace(fileSystem))
            return DefaultFileSystem;

        var upper = fileSystem.Trim().ToUpperInvariant();
        return upper switch
        {
            // diskutil's eraseDisk format token is the literal "ExFAT" (mixed
            // case). Emit canonical casing regardless of how the caller
            // spelled it. Matches the DriveFormatCommand pattern.
            "EXFAT" => "ExFAT",
            // APFS deferred per the 2026-05-05 prep-parity decision and MAC1
            // baseline. NTFS is Windows-only and must not reach the Mac flow.
            "APFS" => throw new System.ArgumentException(
                "APFS is not supported in MAC17 MVP. APFS targets are deferred until a later milestone.",
                nameof(fileSystem)),
            "NTFS" => throw new System.ArgumentException(
                "NTFS is Windows-only. Mac PrepApp must use exFAT for cross-platform or Mac-only targets.",
                nameof(fileSystem)),
            _ => throw new System.ArgumentException(
                $"Unsupported file system '{fileSystem}'. Supported: ExFAT.",
                nameof(fileSystem)),
        };
    }

    /// <summary>
    /// exFAT label sanitization: strip control characters, refuse path
    /// separators and other filesystem metacharacters, trim, cap at
    /// <see cref="MaxLabelLength"/>. Empty input returns empty string —
    /// <see cref="Build"/> substitutes a single space for diskutil's
    /// non-empty-label requirement.
    /// </summary>
    public static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var trimmed = label.Trim();
        var cleaned = new System.Text.StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (char.IsControl(c)) continue;
            // Refuse path separators and other metacharacters that would
            // confuse Windows readers when the SSD travels cross-platform.
            // exFAT itself permits some of these but cross-platform tooling
            // (Windows Explorer, PowerShell Format-Volume) does not.
            if (c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') continue;
            cleaned.Append(c);
            if (cleaned.Length >= MaxLabelLength) break;
        }
        return cleaned.ToString();
    }
}
