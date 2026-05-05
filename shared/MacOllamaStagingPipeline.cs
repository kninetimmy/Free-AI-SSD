namespace FreeAiSsd.Shared;

/// <summary>
/// Result of verifying a staged macOS Ollama payload before its trust
/// attestation is written. <see cref="Success"/> is false on any failure;
/// <see cref="Failure"/> carries the original validator's reason and message
/// so the caller can surface a precise diagnostic.
/// </summary>
public sealed record MacOllamaStagingResult(bool Success, OllamaPackageTrustValidationResult? Failure)
{
    public static MacOllamaStagingResult Ok() => new(true, null);
    public static MacOllamaStagingResult Fail(OllamaPackageTrustValidationResult failure) => new(false, failure);
}

/// <summary>
/// Coordinated verify-then-attest pipeline for the macOS Ollama staging path.
/// Runs the same security gates as the Windows side (URL allowlist, pinned
/// SHA-256, attestation write) plus the Mac-specific Apple Silicon (arm64)
/// slice check. Used by PrepApp's <c>ArtifactStagingService</c> and by
/// integration-shaped tests so the security policy is identical in both.
/// </summary>
public static class MacOllamaStagingPipeline
{
    /// <summary>
    /// Verifies the Mac Ollama archive at <paramref name="archivePath"/> matches
    /// the pinned SHA-256, that the extracted binary at
    /// <paramref name="extractedBinaryPath"/> contains an arm64 slice, and (on
    /// success) writes the on-SSD trust attestation under
    /// <c>mac/tools/ollama/</c>. On failure no attestation is written and
    /// the caller should refuse to stage.
    /// </summary>
    /// <param name="metadata">Pinned Mac package metadata. Defaults to
    /// <see cref="OllamaPackageTrustPolicy.DefaultMacPackage"/>; tests pass a
    /// substitute so they can exercise the pipeline against a synthetic
    /// archive without re-downloading the 180MB upstream zip.</param>
    public static MacOllamaStagingResult VerifyAndAttest(
        string ssdRoot,
        string archivePath,
        string extractedBinaryPath,
        OllamaPackageMetadata? metadata = null)
    {
        var pinned = metadata ?? OllamaPackageTrustPolicy.DefaultMacPackage;

        var sourceCheck = OllamaPackageTrustPolicy.ValidatePackageSource(pinned.Url);
        if (!sourceCheck.IsTrusted) return MacOllamaStagingResult.Fail(sourceCheck);

        var hashCheck = OllamaPackageTrustPolicy.ValidateDownloadedPackage(archivePath, pinned);
        if (!hashCheck.IsTrusted) return MacOllamaStagingResult.Fail(hashCheck);

        var armCheck = OllamaPackageTrustPolicy.ValidateArm64Slice(extractedBinaryPath, pinned);
        if (!armCheck.IsTrusted) return MacOllamaStagingResult.Fail(armCheck);

        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssdRoot, pinned);
        return MacOllamaStagingResult.Ok();
    }
}
