using System.Text.Json;

namespace FreeAiSsd.Shared;

/// <summary>
/// Manifest tracking the Tesseract OCR bundle staged on the SSD under the
/// per-platform tesseract directory. Records the source release tag, URL,
/// SHA-256, size, and observed-at timestamp so a future audit (or the
/// runtime's verification step) can confirm integrity without re-derivation.
/// Stored at <c>{tesseractDir}/tesseract-manifest.json</c>. Mirrors
/// <see cref="PiperManifest"/> but is binary-only — Tesseract ships its
/// language data (tessdata) inside the same frozen bundle, so there is no
/// separate per-voice/per-model download to track.
/// </summary>
public sealed class TesseractManifest
{
    /// <summary>The Tesseract bundle archive that was extracted into the tesseract dir.</summary>
    public TesseractBinaryManifestEntry? Binary { get; set; }

    /// <summary>
    /// Loads the manifest from disk. Returns an empty manifest if the file
    /// is missing or corrupt; staging will then re-download.
    /// </summary>
    public static TesseractManifest Load(string path)
    {
        if (!File.Exists(path)) return new TesseractManifest();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TesseractManifest>(json, JsonOptions()) ?? new TesseractManifest();
        }
        catch
        {
            return new TesseractManifest();
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
/// Manifest entry for the Tesseract bundle archive (one per SSD/platform).
/// Records the source release tag + URL + SHA-256 so the post-extract trust
/// posture can be reconstructed without re-downloading.
/// </summary>
public sealed class TesseractBinaryManifestEntry
{
    /// <summary>Stable platform identifier matching <c>TesseractPlatform</c>.</summary>
    public string Platform { get; set; } = string.Empty;
    /// <summary>Frozen release tag the bundle was published under.</summary>
    public string ReleaseTag { get; set; } = string.Empty;
    /// <summary>Upstream Tesseract version the bundle was curated from.</summary>
    public string TesseractVersion { get; set; } = string.Empty;
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
