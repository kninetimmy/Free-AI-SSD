using System.Text.Json;

namespace FreeAiSsd.Shared;

/// <summary>
/// Manifest tracking all bundled prerequisite installers on the SSD.
/// Records download metadata and SHA-256 hashes for each prerequisite,
/// enabling offline integrity verification before installation.
/// Stored at prereqs/prereqs-manifest.json on the SSD.
/// </summary>
public sealed class PrereqManifest
{
    /// <summary>List of prerequisite entries with their metadata and integrity hashes.</summary>
    public List<PrereqManifestEntry> Prerequisites { get; set; } = new();

    /// <summary>
    /// Loads the manifest from disk. Returns an empty manifest if the file
    /// is missing or corrupt (fail-open for bundle health checks to detect).
    /// </summary>
    public static PrereqManifest Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PrereqManifest();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PrereqManifest>(json, JsonOptions()) ?? new PrereqManifest();
        }
        catch
        {
            return new PrereqManifest();
        }
    }

    /// <summary>
    /// Saves the manifest to disk, creating parent directories if needed.
    /// </summary>
    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonOptions());
        await File.WriteAllTextAsync(path, json);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>
/// Individual entry in the prerequisite manifest, recording the installer's
/// source URL, local filename, SHA-256 hash, size, and installation parameters.
/// </summary>
public sealed class PrereqManifestEntry
{
    /// <summary>Unique identifier matching PrereqCatalog (e.g., "vcredist_x64").</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Human-readable name shown in the install dialog.</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>Filename of the installer on disk (e.g., "vc_redist.x64.exe").</summary>
    public string Filename { get; set; } = string.Empty;
    /// <summary>Original download URL where the installer was obtained.</summary>
    public string SourceUrl { get; set; } = string.Empty;
    /// <summary>UTC timestamp when the installer was downloaded during prep.</summary>
    public DateTime DownloadedAtUtc { get; set; }
    /// <summary>SHA-256 hash of the installer file for integrity verification.</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>File size in bytes.</summary>
    public long SizeBytes { get; set; }
    /// <summary>Command-line arguments for silent/unattended installation.</summary>
    public string SilentArgs { get; set; } = string.Empty;
    /// <summary>Whether the installer requires administrator elevation.</summary>
    public bool RequiresAdmin { get; set; }
    /// <summary>Whether this prerequisite is optional (e.g., .NET Desktop Runtime).</summary>
    public bool IsOptional { get; set; }
}
