namespace FreeAiSsd.Shared;

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

public static class DriveInspector
{
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

    private static string FormatDriveName(DriveInfo drive)
    {
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "No Label" : drive.VolumeLabel;
        var kind = drive.DriveType == DriveType.Fixed ? "Fixed" : "Removable";
        return $"{drive.Name} ({label}, {kind})";
    }

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
