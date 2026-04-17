using System.Management;

namespace FreeAiSsd.Shared;

/// <summary>
/// Queries system hardware resources (RAM, VRAM, disk space) for model sizing
/// compatibility checks. Uses WMI on Windows; returns null on other platforms.
/// All values are silently null on failure to avoid crashing non-Windows or
/// restricted environments.
/// </summary>
public static class SystemResources
{
    /// <summary>
    /// Returns the total physical RAM in GB (rounded), or null if unavailable.
    /// Uses WMI query against Win32_ComputerSystem.TotalPhysicalMemory.
    /// </summary>
    public static int? GetTotalSystemRamGb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            using var collection = searcher.Get();
            foreach (ManagementObject item in collection)
            {
                using (item)
                {
                    var raw = item["TotalPhysicalMemory"]?.ToString();
                    if (ulong.TryParse(raw, out var bytes) && bytes > 0)
                    {
                        return ToGb(bytes);
                    }
                }
            }
        }
        catch
        {
            // WMI unavailable (non-Windows or restricted) → return null.
        }

        return null;
    }

    /// <summary>
    /// Returns the largest GPU's VRAM in GB (rounded), or null if unavailable.
    /// Checks all video controllers and returns the maximum AdapterRAM value.
    /// Note: Win32_VideoController.AdapterRAM is capped at 4 GB (32-bit field),
    /// so high-end GPUs may report inaccurate values.
    /// </summary>
    public static int? GetGpuVramGb()
    {
        try
        {
            ulong maxBytes = 0;
            using var searcher = new ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController");
            using var collection = searcher.Get();
            foreach (ManagementObject item in collection)
            {
                using (item)
                {
                    if (ulong.TryParse(item["AdapterRAM"]?.ToString(), out var bytes) && bytes > maxBytes)
                    {
                        maxBytes = bytes;
                    }
                }
            }

            return maxBytes > 0 ? ToGb(maxBytes) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the free disk space in GB (rounded) for the drive containing the given path,
    /// or null if the path is invalid or the drive is not ready.
    /// </summary>
    /// <param name="pathRoot">Any path on the target drive (e.g., SSD root).</param>
    public static int? GetFreeDiskSpaceGb(string pathRoot)
    {
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            return null;
        }

        try
        {
            var root = Path.GetPathRoot(pathRoot) ?? pathRoot;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return null;
            }

            return ToGb((ulong)drive.AvailableFreeSpace);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts bytes to gigabytes, rounding to the nearest integer
    /// with a minimum of 1 GB to avoid displaying "0 GB".
    /// </summary>
    private static int ToGb(ulong bytes)
    {
        var gb = bytes / (1024d * 1024d * 1024d);
        return Math.Max(1, (int)Math.Round(gb, MidpointRounding.AwayFromZero));
    }
}
