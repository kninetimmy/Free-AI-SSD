namespace FreeAiSsd.Shared;

/// <summary>
/// Represents a candidate drive for SSD preparation, including metadata
/// about capacity, filesystem, and a human-readable warning about suitability.
/// </summary>
public sealed record DriveTarget(
    string Name,
    string RootPath,
    string VolumeLabel,
    long FreeBytes,
    long TotalBytes,
    string DriveFormat,
    bool IsReady,
    bool IsRemovable,
    bool IsFixed,
    string Warning);

/// <summary>
/// Scans the system for eligible drives (removable and optionally fixed)
/// that can be used as targets for portable SSD preparation.
/// </summary>
public static class DriveInspector
{
    /// <summary>
    /// Enumerates ready drives on the system, filtering to removable drives by default.
    /// Fixed drives can be included via the <paramref name="includeFixed"/> flag.
    /// Each drive is annotated with filesystem compatibility warnings.
    /// </summary>
    /// <param name="includeFixed">When true, internal/fixed drives are also listed (with extra safety warnings).</param>
    /// <returns>A list of candidate drives with metadata and warnings.</returns>
    public static IReadOnlyList<DriveTarget> GetCandidateDrives(bool includeFixed = false)
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && (d.DriveType == DriveType.Removable || (includeFixed && d.DriveType == DriveType.Fixed)))
            .Select(d => new DriveTarget(
                Name: FormatDriveName(d),
                RootPath: d.RootDirectory.FullName,
                VolumeLabel: d.VolumeLabel,
                FreeBytes: d.AvailableFreeSpace,
                TotalBytes: d.TotalSize,
                DriveFormat: d.DriveFormat,
                IsReady: d.IsReady,
                IsRemovable: d.DriveType == DriveType.Removable,
                IsFixed: d.DriveType == DriveType.Fixed,
                Warning: DriveWarning(d)))
            .ToList();
    }

    /// <summary>
    /// Builds a human-readable display name for a drive, including its letter,
    /// volume label (or "No Label"), and type (Fixed vs Removable).
    /// </summary>
    private static string FormatDriveName(DriveInfo drive)
    {
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "No Label" : drive.VolumeLabel;
        var kind = drive.DriveType == DriveType.Fixed ? "Fixed" : "Removable";
        return $"{drive.Name} ({label}, {kind})";
    }

    /// <summary>
    /// Generates a filesystem suitability warning based on the drive's format.
    /// NTFS is recommended; exFAT/FAT32 may work but with limitations;
    /// other formats are flagged as untested.
    /// </summary>
    private static string DriveWarning(DriveInfo drive)
    {
        if (drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return drive.DriveType == DriveType.Fixed
                ? "Warning: fixed/internal drive selected. Verify target path carefully. Filesystem NTFS is recommended."
                : "Filesystem: NTFS (recommended).";
        }

        if (drive.DriveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase) ||
            drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
        {
            return "Filesystem warning: NTFS is strongly recommended. exFAT/FAT can work but long file names and ACL behavior may be limited.";
        }

        return $"Filesystem warning: {drive.DriveFormat} is untested. NTFS is strongly recommended.";
    }
}
