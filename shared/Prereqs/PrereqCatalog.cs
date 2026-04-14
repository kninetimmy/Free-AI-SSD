namespace FreeAiSsd.Shared;

/// <summary>
/// Identifies the detection strategy used to check if a prerequisite
/// is already installed on the host system.
/// </summary>
public enum PrereqDetectionType
{
    None,
    /// <summary>Detected via Windows registry keys for VC++ Redistributable.</summary>
    VcRuntimeX64,
    /// <summary>Detected via "dotnet --list-runtimes" output for .NET Desktop Runtime.</summary>
    DotnetDesktop8X64
}

/// <summary>
/// Immutable definition of a prerequisite installer, including its download source,
/// expected filename, silent install arguments, and whether it needs admin elevation.
///
/// Phase-A hardening: <see cref="SourceUrl"/> is retained as a documented default
/// and used by seed/staging paths that do not perform runtime discovery. The
/// actual download path in <see cref="PrepApp.Services.PrereqService"/> and in
/// <c>tools/FreeAiSsd.PrereqFetch</c> resolves the URL through
/// <see cref="PrereqResolver"/> instead so we always grab the latest stable
/// upstream release with its vendor-published hash.
/// </summary>
public sealed record PrereqDefinition(
    string Id,
    string DisplayName,
    string SourceUrl,
    string TargetFileName,
    string SilentArgs,
    bool RequiresAdmin,
    bool IsOptional,
    PrereqDetectionType DetectionType);

/// <summary>
/// Static catalog of required Windows prerequisites for running the portable SSD.
/// Tier 1 includes the Visual C++ Redistributable (required for Ollama) and
/// the .NET 8 Desktop Runtime (required for the Runner WPF app, currently optional).
/// Provides helper methods for manifest path resolution and entry creation.
/// </summary>
public static class PrereqCatalog
{
    /// <summary>Standard filename for the prereqs manifest on the SSD.</summary>
    public const string ManifestFileName = "prereqs-manifest.json";

    /// <summary>Catalog ID for the Visual C++ x64 Redistributable.</summary>
    public const string VcRedistX64Id = "vcredist_x64";
    /// <summary>Catalog ID for the .NET 8 Windows Desktop Runtime (x64).</summary>
    public const string DotnetDesktop8X64Id = "dotnet8_desktop_x64";

    /// <summary>
    /// Tier 1 prerequisite definitions — installers that are bundled on the SSD
    /// and offered for offline installation on the target machine.
    /// </summary>
    public static IReadOnlyList<PrereqDefinition> Tier1 { get; } = new[]
    {
        new PrereqDefinition(
            VcRedistX64Id,
            "Microsoft Visual C++ Redistributable (x64)",
            "https://aka.ms/vs/17/release/vc_redist.x64.exe",
            "vc_redist.x64.exe",
            "/install /quiet /norestart",
            RequiresAdmin: true,
            IsOptional: false,
            DetectionType: PrereqDetectionType.VcRuntimeX64),
        new PrereqDefinition(
            DotnetDesktop8X64Id,
            ".NET 8 Windows Desktop Runtime (x64)",
            "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.18/windowsdesktop-runtime-8.0.18-win-x64.exe",
            "windowsdesktop-runtime-8-win-x64.exe",
            "/install /quiet /norestart",
            RequiresAdmin: true,
            IsOptional: true,
            DetectionType: PrereqDetectionType.DotnetDesktop8X64)
    };

    /// <summary>
    /// Returns the full path to the prereqs manifest file for a given SSD root.
    /// </summary>
    public static string GetManifestPath(string rootPath)
        => Path.Combine(rootPath, SsdLayout.Prereqs, ManifestFileName);

    /// <summary>
    /// Creates a manifest entry from a catalog definition after a successful download.
    /// Captures the SHA-256 hash, file size, and download timestamp.
    /// </summary>
    public static PrereqManifestEntry CreateManifestEntry(PrereqDefinition definition, string sha256, long sizeBytes) => new()
    {
        Id = definition.Id,
        DisplayName = definition.DisplayName,
        Filename = definition.TargetFileName,
        SourceUrl = definition.SourceUrl,
        DownloadedAtUtc = DateTime.UtcNow,
        Sha256 = sha256,
        SizeBytes = sizeBytes,
        SilentArgs = definition.SilentArgs,
        RequiresAdmin = definition.RequiresAdmin,
        IsOptional = definition.IsOptional
    };
}
