using FreeAiSsd.Shared.Prereqs;

namespace FreeAiSsd.Shared.Services;

/// <summary>
/// Stages the optional Piper neural TTS engine (binary + voice files) onto
/// the SSD. Implementations live in prep-core (cross-platform .NET) so both
/// the Windows PrepApp host and the macOS mac-prep-host sidecar can call the
/// same staging code with the same trust posture.
/// </summary>
public interface IPiperStagingService
{
    /// <summary>
    /// Downloads + verifies + extracts Piper for <paramref name="platform"/>
    /// into <c>{ssdRoot}/{platformPiperDir}/</c>, then downloads + verifies
    /// the catalog default voice into <c>voices/</c>. Idempotent (re-running
    /// against a healthy install is a no-op).
    /// </summary>
    Task StagePiperAsync(
        string ssdRoot,
        PiperPlatform platform,
        Action<string> onLog,
        CancellationToken ct = default);
}
