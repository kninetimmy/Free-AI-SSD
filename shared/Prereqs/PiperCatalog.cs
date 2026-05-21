namespace FreeAiSsd.Shared.Prereqs;

/// <summary>
/// Identifies the host platform a Piper binary asset targets. Drives the
/// per-platform asset selection in <see cref="PiperCatalog"/>.
/// </summary>
public enum PiperPlatform
{
    WindowsAmd64,
    MacosAarch64,
    MacosX64,
}

/// <summary>
/// Immutable definition of a Piper binary archive published on a GitHub release.
/// The catalog static-pins the SHA-256 because rhasspy/piper is archived: the
/// tag and assets are frozen, so a pinned hash is the strongest available trust
/// model (no resolver round-trip needed; impossible for the upstream to silently
/// re-publish). When/if we migrate to a maintained binary upstream, the pin can
/// be replaced with the resolver pattern used by <see cref="PrereqResolver"/>.
/// </summary>
/// <param name="Platform">Target platform for this asset.</param>
/// <param name="ArchiveFileName">Asset filename on the GitHub release.</param>
/// <param name="Url">Direct HTTPS download URL (frozen tag, never re-resolved).</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the archive bytes. Verified
/// after download; fail-closed on mismatch.</param>
/// <param name="SizeBytes">Expected size in bytes (defense-in-depth check; the
/// SHA covers integrity but a wildly different size short-circuits the hash).</param>
public sealed record PiperBinaryAsset(
    PiperPlatform Platform,
    string ArchiveFileName,
    string Url,
    string Sha256,
    long SizeBytes);

/// <summary>
/// Immutable definition of a single Piper voice (a .onnx model + its .onnx.json
/// companion config). Voices live in the Hugging Face repo
/// <c>rhasspy/piper-voices</c>; the model file is LFS-tracked so its SHA-256
/// is resolved at staging time from the HF tree API (matches the user's
/// "HF API LFS oid + verify" choice). The companion JSON is a small regular
/// blob — its SHA-256 is static-pinned in this catalog because HF's tree API
/// returns a git blob SHA-1 for non-LFS files, not the content SHA-256.
/// </summary>
/// <param name="Id">Stable catalog id (e.g. "en_US-amy-medium").</param>
/// <param name="DisplayName">Human-readable name for the prep UI.</param>
/// <param name="HfRepo">Hugging Face repo ("rhasspy/piper-voices").</param>
/// <param name="HfPath">Path within the repo to the voice directory (e.g.
/// "en/en_US/amy/medium").</param>
/// <param name="OnnxFileName">Bare filename of the .onnx model.</param>
/// <param name="OnnxJsonFileName">Bare filename of the .onnx.json config.</param>
/// <param name="OnnxJsonSha256">Static SHA-256 of the .onnx.json config bytes.</param>
public sealed record PiperVoice(
    string Id,
    string DisplayName,
    string HfRepo,
    string HfPath,
    string OnnxFileName,
    string OnnxJsonFileName,
    string OnnxJsonSha256);

/// <summary>
/// Static catalog of the Piper neural TTS binary and the default voice bundled
/// by PrepApp.
///
/// Trust model:
///   - Binary: rhasspy/piper release <c>2023.11.14-2</c> is archived. The tag,
///     asset names, and asset bytes are frozen forever. We static-pin SHA-256
///     for each platform asset; downloads are HTTPS-only and fail closed on
///     hash mismatch.
///   - Voice .onnx: SHA-256 is fetched at staging time from
///     <c>https://huggingface.co/api/models/{repo}/tree/{path}</c> (the LFS
///     <c>oid</c> field is the canonical SHA-256). Fail closed if the tree
///     API is unreachable, malformed, or missing the file entry.
///   - Voice .onnx.json: static SHA-256 pin (small, stable companion file;
///     HF's tree API returns a git blob SHA-1 for non-LFS files, not what we
///     need to verify content).
///
/// Future maintenance: if we migrate to a maintained Piper binary (e.g. a
/// future OHF-Voice/piper1-gpl standalone release), replace the static URL +
/// SHA pin with a resolver in <see cref="PiperResolver"/> mirroring the
/// Ollama-style resolver pattern.
/// </summary>
public static class PiperCatalog
{
    /// <summary>Standard filename for the Piper manifest on the SSD.</summary>
    public const string ManifestFileName = "piper-manifest.json";

    /// <summary>Frozen rhasspy/piper release tag the catalog pins to.</summary>
    public const string BinaryReleaseTag = "2023.11.14-2";

    /// <summary>SPDX-style attribution recorded in the manifest so audits can
    /// trace bundled-binary license posture without re-deriving it.</summary>
    public const string BinaryLicenseNote =
        "Piper (MIT) bundled with onnxruntime (MIT) and espeak-ng (GPL-3.0); " +
        "invoked as a separate process — no in-process linkage.";

    /// <summary>
    /// All Piper binary archive assets in the pinned release, keyed by platform.
    /// Hashes captured on 2026-05-20 by downloading each asset and computing
    /// SHA-256; the archived release guarantees these bytes never change.
    /// </summary>
    public static IReadOnlyList<PiperBinaryAsset> BinaryAssets { get; } = new[]
    {
        new PiperBinaryAsset(
            PiperPlatform.WindowsAmd64,
            "piper_windows_amd64.zip",
            "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip",
            "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea",
            22477236L),
        new PiperBinaryAsset(
            PiperPlatform.MacosAarch64,
            "piper_macos_aarch64.tar.gz",
            "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_macos_aarch64.tar.gz",
            "6b1eb03b3735946cb35216e063e7eebcc33a6bbf5dd96ec0217959bf1cdcb0cc",
            19146957L),
        new PiperBinaryAsset(
            PiperPlatform.MacosX64,
            "piper_macos_x64.tar.gz",
            "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_macos_x64.tar.gz",
            "ced85c0a3df13945b1e623b878a48fdc2854d5c485b4b67f62857cf551deaf8b",
            19146927L),
    };

    /// <summary>
    /// Default English voice shipped by PrepApp when the user opts in. Mid-quality
    /// female voice (~60 MB .onnx), CPU-friendly. Voice files live at
    /// <c>https://huggingface.co/rhasspy/piper-voices/resolve/main/{HfPath}/{file}</c>.
    /// </summary>
    public static PiperVoice DefaultVoice { get; } = new(
        Id: "en_US-amy-medium",
        DisplayName: "Amy (US English, medium)",
        HfRepo: "rhasspy/piper-voices",
        HfPath: "en/en_US/amy/medium",
        OnnxFileName: "en_US-amy-medium.onnx",
        OnnxJsonFileName: "en_US-amy-medium.onnx.json",
        OnnxJsonSha256: "95a23eb4d42909d38df73bb9ac7f45f597dbfcde2d1bf9526fdeaf5466977d77");

    /// <summary>
    /// All catalog voices currently offered for staging. Single entry today;
    /// kept as a list so the UI and resolver are list-shaped from day one
    /// (add new voices here without restructuring callers).
    /// </summary>
    public static IReadOnlyList<PiperVoice> Voices { get; } = new[] { DefaultVoice };

    /// <summary>
    /// Returns the binary asset for the given platform, or throws if the
    /// catalog does not cover it (the platform list is fixed and adding a
    /// new platform requires hash capture, so a missing platform is a real
    /// error rather than a silent skip).
    /// </summary>
    public static PiperBinaryAsset GetBinaryAsset(PiperPlatform platform)
    {
        foreach (var asset in BinaryAssets)
        {
            if (asset.Platform == platform) return asset;
        }
        throw new InvalidOperationException(
            $"PiperCatalog has no binary asset for platform {platform}.");
    }

    /// <summary>
    /// Returns the full path to the Piper manifest file under the platform
    /// piper directory on the SSD (e.g. windows/tools/piper/piper-manifest.json).
    /// </summary>
    public static string GetManifestPath(string rootPath, PiperPlatform platform)
        => Path.Combine(rootPath, PiperLayout.GetPiperDir(platform), ManifestFileName);
}

/// <summary>
/// Helper that maps a <see cref="PiperPlatform"/> to its on-SSD directory paths.
/// Kept distinct from <see cref="SsdLayout"/> so PiperCatalog (which lives in
/// the Prereqs subnamespace) does not have to leak knowledge of every platform
/// constant in the layout class.
/// </summary>
public static class PiperLayout
{
    /// <summary>SSD-relative piper directory for the platform.</summary>
    public static string GetPiperDir(PiperPlatform platform) => platform switch
    {
        PiperPlatform.WindowsAmd64 => SsdLayout.WindowsPiper,
        PiperPlatform.MacosAarch64 => SsdLayout.MacPiper,
        PiperPlatform.MacosX64 => SsdLayout.MacPiper,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown Piper platform.")
    };

    /// <summary>SSD-relative piper voices directory for the platform.</summary>
    public static string GetVoicesDir(PiperPlatform platform) => platform switch
    {
        PiperPlatform.WindowsAmd64 => SsdLayout.WindowsPiperVoices,
        PiperPlatform.MacosAarch64 => SsdLayout.MacPiperVoices,
        PiperPlatform.MacosX64 => SsdLayout.MacPiperVoices,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown Piper platform.")
    };
}
