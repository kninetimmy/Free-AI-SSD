using System.Management;
using System.Runtime.InteropServices;

namespace FreeAiSsd.Shared;

public sealed record GpuInfo(
    string Name,
    string Vendor,
    string? DriverVersion,
    bool IsDiscreteLikely,
    bool IsIntegratedLikely);

public sealed record SystemCompatibilitySnapshot(
    string CpuArchitecture,
    string OsVersion,
    IReadOnlyList<GpuInfo> Gpus)
{
    public string BestGpuSummary => Gpus.FirstOrDefault() is { } gpu
        ? $"{gpu.Name} ({gpu.Vendor})"
        : "Unknown GPU";
}

public static class SystemCompatibilityDetector
{
    public static SystemCompatibilitySnapshot Detect()
    {
        var gpus = DetectGpus();
        return new SystemCompatibilitySnapshot(
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.OSDescription,
            gpus);
    }

    public static IReadOnlyList<GpuInfo> DetectGpus()
    {
        try
        {
            var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController");
            var gpus = new List<GpuInfo>();
            foreach (var result in searcher.Get().Cast<ManagementObject>())
            {
                var name = ReadProperty(result, "Name") ?? "Unknown GPU";
                var driverVersion = ReadProperty(result, "DriverVersion");
                var adapterCompatibility = ReadProperty(result, "AdapterCompatibility") ?? string.Empty;
                var pnpDeviceId = ReadProperty(result, "PNPDeviceID") ?? string.Empty;

                var vendor = InferVendor(adapterCompatibility, pnpDeviceId, name);
                var isIntegrated = LooksIntegrated(name, pnpDeviceId);
                var isDiscrete = !isIntegrated;

                gpus.Add(new GpuInfo(name, vendor, driverVersion, isDiscrete, isIntegrated));
            }

            if (gpus.Count > 0)
            {
                return gpus;
            }
        }
        catch
        {
            // fall through to Unknown GPU
        }

        return new[] { new GpuInfo("Unknown GPU", "Unknown", null, false, false) };
    }

    private static string InferVendor(string adapterCompatibility, string pnpDeviceId, string name)
    {
        var source = string.Join(' ', adapterCompatibility, pnpDeviceId, name).ToUpperInvariant();
        if (source.Contains("VEN_10DE") || source.Contains("NVIDIA")) return "NVIDIA";
        if (source.Contains("VEN_1002") || source.Contains("VEN_1022") || source.Contains("AMD") || source.Contains("RADEON")) return "AMD";
        if (source.Contains("VEN_8086") || source.Contains("INTEL")) return "Intel";
        return "Unknown";
    }

    private static bool LooksIntegrated(string name, string pnpDeviceId)
    {
        var text = (name + " " + pnpDeviceId).ToUpperInvariant();
        return text.Contains("INTEL")
            || text.Contains("UHD")
            || text.Contains("IRIS")
            || text.Contains("INTEGRATED")
            || text.Contains("APU");
    }

    private static string? ReadProperty(ManagementObject obj, string propertyName)
    {
        return obj.Properties[propertyName]?.Value?.ToString()?.Trim();
    }
}
