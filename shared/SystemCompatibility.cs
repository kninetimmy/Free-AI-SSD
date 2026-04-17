using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FreeAiSsd.Shared;

/// <summary>
/// Information about a single GPU detected on the system, including
/// vendor classification and discrete/integrated heuristics.
/// </summary>
public sealed record GpuInfo(
    string Name,
    string Vendor,
    string? DriverVersion,
    /// <summary>True if the GPU appears to be a dedicated/discrete GPU (NVIDIA, AMD).</summary>
    bool IsDiscreteLikely,
    /// <summary>True if the GPU appears to be integrated (Intel UHD/Iris, AMD APU).</summary>
    bool IsIntegratedLikely);

/// <summary>
/// Snapshot of the system's hardware compatibility profile, including CPU architecture,
/// OS version, and detected GPUs. Used to display compatibility info in the Runner UI
/// and to inform the user about potential model performance issues.
/// </summary>
public sealed record SystemCompatibilitySnapshot(
    string CpuArchitecture,
    string OsVersion,
    IReadOnlyList<GpuInfo> Gpus)
{
    /// <summary>
    /// Returns a human-readable summary of the best available GPU,
    /// or "Unknown GPU" if none were detected.
    /// </summary>
    public string BestGpuSummary => Gpus.FirstOrDefault() is { } gpu
        ? $"{gpu.Name} ({gpu.Vendor})"
        : "Unknown GPU";
}

/// <summary>
/// Detects system hardware capabilities (CPU, OS, GPUs) for compatibility reporting.
/// Uses WMI queries on Windows; returns "Unknown" placeholders on other platforms.
/// </summary>
public static class SystemCompatibilityDetector
{
    /// <summary>
    /// Captures a snapshot of the current system's hardware profile.
    /// </summary>
    public static SystemCompatibilitySnapshot Detect()
    {
        var gpus = DetectGpus();
        return new SystemCompatibilitySnapshot(
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.OSDescription,
            gpus);
    }

    /// <summary>
    /// Detects GPUs on the system. On Windows, uses WMI queries against
    /// Win32_VideoController. On other platforms, returns an "Unknown GPU" placeholder.
    /// </summary>
    public static IReadOnlyList<GpuInfo> DetectGpus()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new[] { new GpuInfo("Unknown GPU", "Unknown", null, false, false) };
        }

        return DetectGpusWindows();
    }

    /// <summary>
    /// Windows-specific GPU detection using WMI. Queries Win32_VideoController
    /// for each GPU's name, driver version, adapter compatibility, and PNP device ID.
    /// Infers vendor (NVIDIA/AMD/Intel) and discrete vs integrated classification
    /// from PCI vendor IDs and name heuristics.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<GpuInfo> DetectGpusWindows()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterCompatibility, PNPDeviceID FROM Win32_VideoController");
            var gpus = new List<GpuInfo>();
            using var collection = searcher.Get();
            foreach (ManagementObject result in collection)
            {
                using (result)
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
            }

            if (gpus.Count > 0)
            {
                return gpus;
            }
        }
        catch
        {
            // WMI query failure → fall through to Unknown GPU.
        }

        return new[] { new GpuInfo("Unknown GPU", "Unknown", null, false, false) };
    }

    /// <summary>
    /// Infers the GPU vendor from PCI vendor IDs and name keywords.
    /// PCI vendor IDs: NVIDIA=10DE, AMD=1002/1022, Intel=8086.
    /// </summary>
    private static string InferVendor(string adapterCompatibility, string pnpDeviceId, string name)
    {
        var source = string.Join(' ', adapterCompatibility, pnpDeviceId, name).ToUpperInvariant();
        if (source.Contains("VEN_10DE") || source.Contains("NVIDIA")) return "NVIDIA";
        if (source.Contains("VEN_1002") || source.Contains("VEN_1022") || source.Contains("AMD") || source.Contains("RADEON")) return "AMD";
        if (source.Contains("VEN_8086") || source.Contains("INTEL")) return "Intel";
        return "Unknown";
    }

    /// <summary>
    /// Heuristic to detect integrated GPUs based on name keywords.
    /// Intel UHD/Iris, AMD APUs, and anything labeled "INTEGRATED" are classified as integrated.
    /// </summary>
    private static bool LooksIntegrated(string name, string pnpDeviceId)
    {
        var text = (name + " " + pnpDeviceId).ToUpperInvariant();
        return text.Contains("INTEL")
            || text.Contains("UHD")
            || text.Contains("IRIS")
            || text.Contains("INTEGRATED")
            || text.Contains("APU");
    }

    /// <summary>
    /// Safely reads a WMI property value as a trimmed string, returning null if unavailable.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? ReadProperty(ManagementObject obj, string propertyName)
    {
        return obj.Properties[propertyName]?.Value?.ToString()?.Trim();
    }
}
