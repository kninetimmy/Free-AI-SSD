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
    string Warning);

public static class DriveInspector
{
    public static IReadOnlyList<DriveTarget> GetCandidateDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && (d.DriveType == DriveType.Removable || d.DriveType == DriveType.Fixed))
            .Select(d => new DriveTarget(
                Name: $"{d.Name} ({d.VolumeLabel})",
                RootPath: d.RootDirectory.FullName,
                VolumeLabel: d.VolumeLabel,
                FreeBytes: d.AvailableFreeSpace,
                TotalBytes: d.TotalSize,
                DriveFormat: d.DriveFormat,
                IsReady: d.IsReady,
                IsRemovable: d.DriveType == DriveType.Removable,
                Warning: DriveWarning(d.DriveFormat)))
            .ToList();
    }

    private static string DriveWarning(string driveFormat)
    {
        if (driveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (driveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase) ||
            driveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
        {
            return "NTFS recommended. exFAT/FAT can work but long file names and ACL behavior may be limited.";
        }

        return $"Filesystem {driveFormat} is untested.";
    }
}
