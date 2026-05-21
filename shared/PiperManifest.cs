using System.Text.Json;

namespace FreeAiSsd.Shared;

/// <summary>
/// Manifest tracking the Piper neural TTS binary and voice files staged on
/// the SSD under the per-platform piper directory. Records the source URL,
/// SHA-256, and observed-at timestamp for each file so a future audit (or
/// the runtime's verification step) can confirm integrity without re-derivation.
/// Stored at <c>{piperDir}/piper-manifest.json</c>.
/// </summary>
public sealed class PiperManifest
{
    /// <summary>The Piper binary archive that was extracted into the piper dir.</summary>
    public PiperBinaryManifestEntry? Binary { get; set; }

    /// <summary>Voices currently staged under {piperDir}/voices/.</summary>
    public List<PiperVoiceManifestEntry> Voices { get; set; } = new();

    /// <summary>
    /// Loads the manifest from disk. Returns an empty manifest if the file
    /// is missing or corrupt; staging will then re-download.
    /// </summary>
    public static PiperManifest Load(string path)
    {
        if (!File.Exists(path)) return new PiperManifest();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PiperManifest>(json, JsonOptions()) ?? new PiperManifest();
        }
        catch
        {
            return new PiperManifest();
        }
    }

    /// <summary>Saves the manifest to disk, creating parent dirs if needed.</summary>
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
/// Manifest entry for the Piper binary archive (one per SSD/platform).
/// Records the source release tag + URL + SHA-256 so the post-extract trust
/// posture can be reconstructed without re-downloading.
/// </summary>
public sealed class PiperBinaryManifestEntry
{
    /// <summary>Stable platform identifier matching <c>PiperPlatform</c>.</summary>
    public string Platform { get; set; } = string.Empty;
    /// <summary>Upstream release tag (e.g. "2023.11.14-2").</summary>
    public string ReleaseTag { get; set; } = string.Empty;
    /// <summary>Filename of the downloaded archive.</summary>
    public string ArchiveFileName { get; set; } = string.Empty;
    /// <summary>HTTPS URL the archive was downloaded from.</summary>
    public string SourceUrl { get; set; } = string.Empty;
    /// <summary>SHA-256 verified against the catalog pin.</summary>
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>Archive size in bytes (defense-in-depth).</summary>
    public long SizeBytes { get; set; }
    /// <summary>UTC timestamp when staging completed.</summary>
    public DateTime InstalledAtUtc { get; set; }
    /// <summary>Free-form license attribution recorded for audit.</summary>
    public string LicenseNote { get; set; } = string.Empty;
}

/// <summary>
/// Manifest entry for a single Piper voice (.onnx + .onnx.json pair).
/// </summary>
public sealed class PiperVoiceManifestEntry
{
    /// <summary>Catalog id (e.g. "en_US-amy-medium").</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>HF resolve URL of the .onnx file.</summary>
    public string OnnxUrl { get; set; } = string.Empty;
    /// <summary>SHA-256 verified after download.</summary>
    public string OnnxSha256 { get; set; } = string.Empty;
    /// <summary>.onnx size in bytes.</summary>
    public long OnnxSizeBytes { get; set; }
    /// <summary>HF resolve URL of the .onnx.json companion config.</summary>
    public string OnnxJsonUrl { get; set; } = string.Empty;
    /// <summary>SHA-256 verified against the catalog pin.</summary>
    public string OnnxJsonSha256 { get; set; } = string.Empty;
    /// <summary>.onnx.json size in bytes.</summary>
    public long OnnxJsonSizeBytes { get; set; }
    /// <summary>UTC timestamp when staging completed.</summary>
    public DateTime InstalledAtUtc { get; set; }
    /// <summary>Free-form trust attribution recorded for audit.</summary>
    public string TrustNote { get; set; } = string.Empty;
}
