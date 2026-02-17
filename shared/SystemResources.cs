using System.Management;

namespace FreeAiSsd.Shared;

public static class SystemResources
{
    public static int? GetTotalSystemRamGb()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var item in searcher.Get().OfType<ManagementObject>())
            {
                var raw = item["TotalPhysicalMemory"]?.ToString();
                if (ulong.TryParse(raw, out var bytes) && bytes > 0)
                {
                    return ToGb(bytes);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    public static int? GetGpuVramGb()
    {
        try
        {
            ulong maxBytes = 0;
            using var searcher = new ManagementObjectSearcher("SELECT AdapterRAM FROM Win32_VideoController");
            foreach (var item in searcher.Get().OfType<ManagementObject>())
            {
                if (ulong.TryParse(item["AdapterRAM"]?.ToString(), out var bytes) && bytes > maxBytes)
                {
                    maxBytes = bytes;
                }
            }

            return maxBytes > 0 ? ToGb(maxBytes) : null;
        }
        catch
        {
            return null;
        }
    }

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

    private static int ToGb(ulong bytes)
    {
        var gb = bytes / (1024d * 1024d * 1024d);
        return Math.Max(1, (int)Math.Round(gb, MidpointRounding.AwayFromZero));
    }
}
