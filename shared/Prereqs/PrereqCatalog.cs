namespace FreeAiSsd.Shared;

public enum PrereqDetectionType
{
    None,
    VcRuntimeX64,
    DotnetDesktop8X64
}

public sealed record PrereqDefinition(
    string Id,
    string DisplayName,
    string SourceUrl,
    string TargetFileName,
    string SilentArgs,
    bool RequiresAdmin,
    bool IsOptional,
    PrereqDetectionType DetectionType);

public static class PrereqCatalog
{
    public const string ManifestFileName = "prereqs-manifest.json";

    public const string VcRedistX64Id = "vcredist_x64";
    public const string DotnetDesktop8X64Id = "dotnet8_desktop_x64";

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

    public static string GetManifestPath(string rootPath)
        => Path.Combine(rootPath, SsdLayout.Prereqs, ManifestFileName);

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
