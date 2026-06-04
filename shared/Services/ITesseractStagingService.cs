using FreeAiSsd.Shared.Prereqs;

namespace FreeAiSsd.Shared.Services;

/// <summary>
/// Stages the optional Tesseract OCR engine (binary + tessdata) onto the SSD.
/// Implementations live in prep-core (cross-platform .NET) so both the Windows
/// PrepApp host and (future) the macOS prep sidecar can call the same staging
/// code with the same trust posture. Mirrors <see cref="IPiperStagingService"/>.
/// </summary>
public interface ITesseractStagingService
{
    /// <summary>
    /// Downloads + verifies + extracts the curated Tesseract bundle for
    /// <paramref name="platform"/> into <c>{ssdRoot}/{platformTesseractDir}/</c>,
    /// preserving the bundle's <c>tessdata/</c> subtree. Idempotent (re-running
    /// against a healthy install is a no-op).
    /// </summary>
    Task StageTesseractAsync(
        string ssdRoot,
        TesseractPlatform platform,
        Action<string> onLog,
        CancellationToken ct = default);
}
