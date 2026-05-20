using System.Management;
using System.Runtime.Versioning;

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
    /// Enumerates ready drives on the system. Includes removable drives and
    /// USB-connected drives that Windows misclassifies as Fixed (common for
    /// USB SSDs). True internal fixed drives are only included when
    /// <paramref name="includeFixed"/> is true.
    /// </summary>
    /// <param name="includeFixed">When true, internal/fixed drives are also listed (with extra safety warnings).</param>
    /// <returns>A list of candidate drives with metadata and warnings.</returns>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<DriveTarget> GetCandidateDrives(bool includeFixed = false)
    {
        var usbRoots = GetUsbConnectedRootPaths();
        var osRoot = GetOsDriveRoot();
        var selfRoot = GetSelfDriveRoot();

        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && IsEligible(d, usbRoots, osRoot, selfRoot, includeFixed))
            .Select(d =>
            {
                bool usbAttributed = d.DriveType == DriveType.Fixed && usbRoots.Contains(d.RootDirectory.FullName);
                // Task #47: a Fixed drive that isn't the OS drive and isn't the
                // drive PrepApp launched from is almost always an external
                // enclosure whose USB attribution slipped past both WMI paths
                // (common with Mac-prepped exFAT drives and Thunderbolt/UAS
                // enclosures). Surface them as removable so the user can
                // actually pick them; the warning string flags the uncertainty.
                bool unattributedExternal = d.DriveType == DriveType.Fixed
                    && !usbAttributed
                    && !IsExcludedRoot(d.RootDirectory.FullName, osRoot, selfRoot);
                bool isRemovable = d.DriveType == DriveType.Removable || usbAttributed || unattributedExternal;
                bool isFixed = d.DriveType == DriveType.Fixed && !usbAttributed && !unattributedExternal;
                return new DriveTarget(
                    Name: FormatDriveName(d, isRemovable),
                    RootPath: d.RootDirectory.FullName,
                    VolumeLabel: d.VolumeLabel,
                    FreeBytes: d.AvailableFreeSpace,
                    TotalBytes: d.TotalSize,
                    DriveFormat: d.DriveFormat,
                    IsReady: d.IsReady,
                    IsRemovable: isRemovable,
                    IsFixed: isFixed,
                    Warning: DriveWarning(d, isFixed, unattributedExternal));
            })
            .ToList();
    }

    private static bool IsEligible(DriveInfo d, HashSet<string> usbRoots, string? osRoot, string? selfRoot, bool includeFixed)
    {
        if (d.DriveType == DriveType.Removable) return true;
        if (d.DriveType == DriveType.Fixed)
        {
            // USB SSDs that Windows marks Fixed are always eligible
            if (usbRoots.Contains(d.RootDirectory.FullName)) return true;
            // Task #47: surface Fixed drives that are neither the OS drive nor
            // PrepApp's host drive — external enclosures often slip past USB
            // attribution. The OS/self exclusion is the safety rail; the
            // user still hits ConfirmErase before any format runs.
            if (!IsExcludedRoot(d.RootDirectory.FullName, osRoot, selfRoot)) return true;
            // Final escape hatch for the OS/self drives — only when the user
            // explicitly opts in via the "Show all drives" toggle.
            if (includeFixed) return true;
        }
        return false;
    }

    internal static bool IsExcludedRoot(string root, string? osRoot, string? selfRoot)
    {
        if (string.IsNullOrEmpty(root)) return false;
        if (!string.IsNullOrEmpty(osRoot) && string.Equals(root, osRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(selfRoot) && string.Equals(root, selfRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static string? GetOsDriveRoot()
    {
        try { return Path.GetPathRoot(Environment.SystemDirectory); }
        catch { return null; }
    }

    private static string? GetSelfDriveRoot()
    {
        try { return Path.GetPathRoot(AppContext.BaseDirectory); }
        catch { return null; }
    }

    /// <summary>
    /// Uses WMI to build the set of drive root paths that are physically
    /// connected via USB, regardless of how Windows classifies their DriveType.
    /// Returns an empty set if WMI is unavailable.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static HashSet<string> GetUsbConnectedRootPaths()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Primary: ROOT\Microsoft\Windows\Storage MSFT_PhysicalDisk with bus
        // types that imply an external enclosure — 7 (USB), 8 (RAID), 17 (NVMe
        // over external M.2 enclosures), 20 (Thunderbolt/File-Backed Virtual).
        // Catches USB NVMe/SATA enclosures that report InterfaceType='SCSI'
        // via UAS in Win32_DiskDrive, plus Thunderbolt SSDs that present as
        // non-USB to the legacy query below. The Fixed-drive fallback in
        // IsEligible is the safety net for enclosures that still slip through.
        try
        {
            using var pdSearcher = new ManagementObjectSearcher(
                @"ROOT\Microsoft\Windows\Storage",
                "SELECT UniqueId FROM MSFT_PhysicalDisk WHERE BusType = 7 OR BusType = 8 OR BusType = 17 OR BusType = 20");
            using var pdCollection = pdSearcher.Get();
            foreach (ManagementObject pd in pdCollection)
            {
                using (pd)
                {
                    var uniqueId = pd["UniqueId"]?.ToString();
                    if (string.IsNullOrEmpty(uniqueId)) continue;

                    // MSFT_PhysicalDisk.DeviceID != MSFT_Disk.Number; join through MSFT_Disk
                    // so that MSFT_Partition.DiskNumber resolves correctly on UAS/NVMe enclosures.
                    var escapedUniqueId = uniqueId.Replace("\\", "\\\\").Replace("'", "\\'");
                    using var diskSearcher = new ManagementObjectSearcher(
                        @"ROOT\Microsoft\Windows\Storage",
                        $"SELECT Number FROM MSFT_Disk WHERE UniqueId = '{escapedUniqueId}'");
                    using var diskCollection = diskSearcher.Get();
                    foreach (ManagementObject disk in diskCollection)
                    {
                        using (disk)
                        {
                            if (!uint.TryParse(disk["Number"]?.ToString(), out var diskNumber)) continue;

                            using var partSearcher = new ManagementObjectSearcher(
                                @"ROOT\Microsoft\Windows\Storage",
                                $"SELECT DriveLetter FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");
                            using var partCollection = partSearcher.Get();
                            foreach (ManagementObject part in partCollection)
                            {
                                using (part)
                                {
                                    var letterObj = part["DriveLetter"];
                                    if (letterObj == null) continue;
                                    char letter = Convert.ToChar(letterObj);
                                    if (letter != '\0')
                                        roots.Add(letter + ":\\");
                                }
                            }
                        }
                    }
                }
            }

            if (roots.Count > 0) return roots;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DriveInspector] MSFT_PhysicalDisk query failed: {ex.Message}");
        }

        // Fallback: Win32_DiskDrive ASSOCIATORS chain. Works on most systems but misses
        // USB SSDs behind UAS adapters (they report InterfaceType='SCSI', not 'USB').
        try
        {
            using var diskSearcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_DiskDrive WHERE InterfaceType = 'USB'");
            using var diskCollection = diskSearcher.Get();
            foreach (ManagementObject disk in diskCollection)
            {
                using (disk)
                {
                    var diskId = disk["DeviceID"]?.ToString();
                    if (diskId == null) continue;

                    // WMI ASSOCIATORS queries require backslashes doubled
                    var escapedDiskId = diskId.Replace("\\", "\\\\");

                    using var partSearcher = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escapedDiskId}'}} " +
                        "WHERE AssocClass=Win32_DiskDriveToDiskPartition");
                    using var partCollection = partSearcher.Get();
                    foreach (ManagementObject partition in partCollection)
                    {
                        using (partition)
                        {
                            var partId = partition["DeviceID"]?.ToString();
                            if (partId == null) continue;

                            var escapedPartId = partId.Replace("\\", "\\\\");
                            using var logicalSearcher = new ManagementObjectSearcher(
                                $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{escapedPartId}'}} " +
                                "WHERE AssocClass=Win32_LogicalDiskToPartition");
                            using var logicalCollection = logicalSearcher.Get();
                            foreach (ManagementObject logical in logicalCollection)
                            {
                                using (logical)
                                {
                                    var name = logical["Name"]?.ToString();
                                    if (name != null)
                                        roots.Add(name + "\\");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[DriveInspector] Win32_DiskDrive query failed: {ex.Message}");
        }

        return roots;
    }

    private static string FormatDriveName(DriveInfo drive, bool isRemovable)
    {
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "No Label" : drive.VolumeLabel;
        var kind = isRemovable ? "Removable" : "Fixed";
        return $"{drive.Name} ({label}, {kind})";
    }

    private static string DriveWarning(DriveInfo drive, bool isInternalFixed, bool isUnattributedExternal)
    {
        // Task #47: drives that escaped USB attribution but aren't the OS/self
        // drive get a "verify this is correct" prefix — the safety-rail
        // warning. The filesystem note (if any) is appended after.
        var prefix = isUnattributedExternal
            ? "External enclosure detected via fallback path — verify this is the correct drive before continuing. "
            : isInternalFixed
                ? "Warning: fixed/internal drive selected. Verify target path carefully. "
                : string.Empty;

        if (drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
        {
            if (prefix.Length > 0) return prefix + "Filesystem NTFS is recommended.";
            return string.Empty; // NTFS removable: nothing to warn about
        }

        if (drive.DriveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase) ||
            drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
        {
            // exFAT/FAT is the common Mac-formatted state for #47. PrepApp
            // will reformat to NTFS (or back to exFAT for cross-platform prep)
            // during the Format step, so frame this as informational rather
            // than alarming.
            return prefix +
                $"Filesystem: {drive.DriveFormat}. PrepApp will reformat this drive when you continue to the Format step.";
        }

        return prefix + $"Filesystem warning: {drive.DriveFormat} is untested. NTFS is strongly recommended.";
    }
}
