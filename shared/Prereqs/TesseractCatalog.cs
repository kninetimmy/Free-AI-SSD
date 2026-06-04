namespace FreeAiSsd.Shared.Prereqs;

/// <summary>Host platform a Tesseract bundle targets.</summary>
public enum TesseractPlatform
{
    WindowsAmd64,
    MacosAarch64,
    MacosX64,
}

/// <summary>
/// Immutable definition of a curated, portable Tesseract bundle published as a frozen GitHub
/// release asset (tesseract.exe + runtime DLLs + tessdata). Like <c>PiperCatalog</c> we static-pin
/// the SHA-256: the asset is uploaded once to a frozen tag and never re-published, so a pinned hash
/// is the strongest trust model. Downloads are HTTPS-only and fail closed on hash mismatch.
/// </summary>
/// <param name="Platform">Target platform for this asset.</param>
/// <param name="ArchiveFileName">Asset filename on the GitHub release. The extension
/// (<c>.zip</c> vs <c>.tar.gz</c>) is the source of truth for the archive format —
/// the staging service infers how to extract from it.</param>
/// <param name="Url">Direct HTTPS download URL (frozen tag, never re-resolved).</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the archive bytes. Verified after download.</param>
/// <param name="SizeBytes">Expected size in bytes (defense-in-depth alongside the hash).</param>
/// <param name="TesseractVersion">Upstream Tesseract version this specific asset was curated
/// from. Per-asset because platforms diverge: Windows ships 5.4.0 (UB-Mannheim), macOS ships
/// 5.5.x (Homebrew) — the manifest must record the version actually staged, not a single const.</param>
public sealed record TesseractBinaryAsset(
    TesseractPlatform Platform,
    string ArchiveFileName,
    string Url,
    string Sha256,
    long SizeBytes,
    string TesseractVersion);

/// <summary>
/// Static catalog of the curated portable Tesseract bundle staged for offline PDF-image OCR.
///
/// Trust model: the bundle is assembled from the UB-Mannheim Tesseract 5.4.0.20240606 distribution
/// (tesseract.exe + its runtime DLLs + the <c>eng</c>/<c>osd</c> tessdata and <c>tsv</c> config),
/// zipped, and uploaded once to a frozen release tag on this repo. The SHA-256 and size below are the
/// real values of that artifact; downloads fail closed on mismatch.
/// </summary>
public static class TesseractCatalog
{
    /// <summary>Standard filename for the Tesseract manifest written on the SSD after staging.</summary>
    public const string ManifestFileName = "tesseract-manifest.json";

    /// <summary>Frozen release tag the bundle is published under.</summary>
    public const string BinaryReleaseTag = "ocr-tools-tesseract-5.4.0";

    /// <summary>Upstream Tesseract version the Windows bundle was curated from. Retained as the
    /// default/Windows value; per-asset versions live on <see cref="TesseractBinaryAsset"/> because
    /// macOS ships a different upstream (Homebrew 5.5.x).</summary>
    public const string TesseractVersion = "5.4.0.20240606";

    /// <summary>SPDX-style attribution recorded in the manifest so audits can trace bundled-binary
    /// license posture. Tesseract is invoked as a separate process — no in-process linkage.</summary>
    public const string BinaryLicenseNote =
        "Tesseract (Apache-2.0) bundled with Leptonica (BSD-2-Clause) and its third-party runtime " +
        "libraries under their respective licenses; invoked as a separate process — no in-process linkage.";

    /// <summary>
    /// All Tesseract bundle assets, keyed by platform. The Windows SHA-256 was captured on
    /// 2026-06-04 by assembling the bundle and hashing the zip; the frozen release guarantees the
    /// bytes never change.
    /// </summary>
    public static IReadOnlyList<TesseractBinaryAsset> BinaryAssets { get; } = new[]
    {
        new TesseractBinaryAsset(
            TesseractPlatform.WindowsAmd64,
            "tesseract-5.4.0.20240606-win-x64.zip",
            "https://github.com/kninetimmy/Free-AI-SSD/releases/download/ocr-tools-tesseract-5.4.0/tesseract-5.4.0.20240606-win-x64.zip",
            "c0049e28e216f64668ff1cbcd1178078179e3b127d049da732a6921f90d22ca7",
            62298545L,
            TesseractVersion),
        // macOS arm64 (Apple Silicon): a relocatable bundle hand-assembled from Homebrew
        // Tesseract 5.5.2 via tools/mac-ocr-bundle/build-mac-tesseract-bundle.sh
        // (tesseract + its dylib closure rewritten to @executable_path + eng/osd tessdata + tsv
        // config), uploaded to the same frozen release tag. SHA-256 + size captured 2026-06-04 by
        // hashing the assembled .tar.gz and re-verified against the published asset.
        new TesseractBinaryAsset(
            TesseractPlatform.MacosAarch64,
            "tesseract-5.5.2-osx-arm64.tar.gz",
            "https://github.com/kninetimmy/Free-AI-SSD/releases/download/ocr-tools-tesseract-5.4.0/tesseract-5.5.2-osx-arm64.tar.gz",
            "2744f688aab1ce3c566a26d1d068f57917bdbfa3661518636d4990a2c32532c6",
            9843621L,
            "5.5.2"),
        // macOS x64 (Intel) is not yet covered — building it requires an Intel Mac to assemble a
        // native bundle. DetectCurrentPlatform maps Intel Macs to MacosX64, so GetBinaryAsset
        // throws a clear "no asset" error there (fail-closed, no placeholder hash) until one ships.
    };

    /// <summary>
    /// Returns the bundle asset for the given platform, or throws if the catalog does not cover it
    /// (a missing platform is a real error requiring hash capture, not a silent skip).
    /// </summary>
    public static TesseractBinaryAsset GetBinaryAsset(TesseractPlatform platform)
    {
        foreach (var asset in BinaryAssets)
        {
            if (asset.Platform == platform) return asset;
        }
        throw new InvalidOperationException($"TesseractCatalog has no binary asset for platform {platform}.");
    }

    /// <summary>Full path to the Tesseract manifest under the platform tesseract dir on the SSD.</summary>
    public static string GetManifestPath(string rootPath, TesseractPlatform platform)
        => Path.Combine(rootPath, TesseractLayout.GetDir(platform), ManifestFileName);
}

/// <summary>Maps a <see cref="TesseractPlatform"/> to its on-SSD directory, mirroring PiperLayout.</summary>
public static class TesseractLayout
{
    /// <summary>SSD-relative tesseract directory for the platform.</summary>
    public static string GetDir(TesseractPlatform platform) => platform switch
    {
        TesseractPlatform.WindowsAmd64 => SsdLayout.WindowsTesseract,
        TesseractPlatform.MacosAarch64 => SsdLayout.MacTesseract,
        TesseractPlatform.MacosX64 => SsdLayout.MacTesseract,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown Tesseract platform.")
    };
}
