using System.Collections.Generic;
using FreeAiSsd.Shared.Helpers;

namespace FreeAiSsd.Shared;

/// <summary>
/// Enumerates the specific reasons why an Ollama package may fail trust validation.
/// Used for programmatic error handling and user-facing diagnostics.
/// </summary>
public enum OllamaPackageTrustFailureReason
{
    None = 0,
    UrlMissing,
    UrlMalformed,
    UrlSchemeNotHttps,
    UrlHostNotAllowlisted,
    MissingPinnedMetadata,
    ExpectedDigestMissing,
    DigestMismatch,
    AttestationMissing,
    AttestationDigestMismatch,
    AttestationUrlMismatch,
    /// <summary>
    /// The downloaded macOS Ollama payload does not contain an arm64 Mach-O
    /// slice. Apple Silicon is the only supported Mac hardware (MAC1).
    /// </summary>
    Arm64SliceMissing,
    /// <summary>
    /// The macOS Ollama binary referenced by trust validation is missing on disk.
    /// </summary>
    BinaryMissing
}

/// <summary>
/// Result of an Ollama package trust validation, indicating whether the package
/// is trusted and providing the failure reason and diagnostic message if not.
/// </summary>
public sealed record OllamaPackageTrustValidationResult(
    bool IsTrusted,
    OllamaPackageTrustFailureReason Reason,
    string Message,
    OllamaPackageMetadata? Metadata = null,
    string? ActualSha256 = null)
{
    public static OllamaPackageTrustValidationResult Success(OllamaPackageMetadata metadata, string? actualSha256 = null) =>
        new(true, OllamaPackageTrustFailureReason.None, "Trusted Ollama package.", metadata, actualSha256);

    public static OllamaPackageTrustValidationResult Fail(OllamaPackageTrustFailureReason reason, string message, OllamaPackageMetadata? metadata = null, string? actualSha256 = null) =>
        new(false, reason, message, metadata, actualSha256);
}

/// <summary>
/// Immutable metadata for a pinned Ollama package version, including its
/// download URL and expected SHA-256 hash for integrity verification.
/// </summary>
public sealed record OllamaPackageMetadata(string Version, string Url, string Sha256);

/// <summary>
/// Persisted attestation record written to the SSD after a trusted package
/// has been downloaded and verified. Used at runtime to gate execution
/// without re-downloading.
/// </summary>
public sealed class OllamaPackageTrustAttestation
{
    public required string Version { get; init; }
    public required string Url { get; init; }
    public required string Sha256 { get; init; }
    public required DateTime VerifiedAtUtc { get; init; }
}

/// <summary>
/// Implements a supply-chain security policy for the bundled Ollama binary.
/// Validates download sources against an allowlist of trusted hosts and pinned
/// SHA-256 digests, and manages attestation files on the SSD to gate execution.
///
/// Trust chain: URL allowlist → pinned metadata → SHA-256 digest verification → attestation file.
/// </summary>
public static class OllamaPackageTrustPolicy
{
    /// <summary>
    /// The default pinned Windows Ollama package with known-good version, URL, and SHA-256 hash.
    /// </summary>
    public static readonly OllamaPackageMetadata DefaultWindowsPackage = new(
        Version: "v0.5.7",
        Url: "https://github.com/ollama/ollama/releases/download/v0.5.7/ollama-windows-amd64.zip",
        Sha256: "11ec2270a5205228fddeaa15c8319a0f0167c0ee7420d19c43714312d4761d2d");

    /// <summary>
    /// The default pinned macOS Ollama package. Pinned to the same upstream
    /// version as <see cref="DefaultWindowsPackage"/> so a single Free-AI-SSD
    /// release ships the same Ollama runtime on both platforms. Universal
    /// (x86_64 + arm64) Mach-O archive; the arm64 slice is the only one the
    /// runtime trust gate accepts (see MAC1 baseline).
    /// </summary>
    public static readonly OllamaPackageMetadata DefaultMacPackage = new(
        Version: "v0.5.7",
        Url: "https://github.com/ollama/ollama/releases/download/v0.5.7/ollama-darwin.zip",
        Sha256: "09ad6bb2edf7cb78619a0932c93c544c362c6ac738c7d5531b3b1b87ac619971");

    /// <summary>
    /// Set of trusted hostnames from which Ollama packages may be downloaded.
    /// Only HTTPS URLs from these hosts pass source validation.
    /// </summary>
    private static readonly HashSet<string> AllowlistedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com"
    };

    /// <summary>
    /// Mapping of pinned package URLs to their expected metadata (version + SHA-256).
    /// Only URLs present in this dictionary pass the pinned metadata check.
    /// </summary>
    private static readonly Dictionary<string, OllamaPackageMetadata> PinnedMetadataByUrl = new(StringComparer.Ordinal)
    {
        [DefaultWindowsPackage.Url] = DefaultWindowsPackage,
        [DefaultMacPackage.Url] = DefaultMacPackage
    };

    public static string TrustAttestationFileName => "ollama-package-trust.json";

    /// <summary>
    /// Validates that a package source URL is well-formed, uses HTTPS,
    /// comes from an allowlisted host, and has pinned metadata with a SHA-256 digest.
    /// </summary>
    /// <param name="urlText">The download URL to validate.</param>
    /// <returns>Validation result with trust status and metadata if trusted.</returns>
    public static OllamaPackageTrustValidationResult ValidatePackageSource(string? urlText)
    {
        var normalized = (urlText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.UrlMissing,
                "Ollama package URL is required.");
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.UrlMalformed,
                "Ollama package URL is malformed.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.UrlSchemeNotHttps,
                "Ollama package URL must use https.");
        }

        if (!AllowlistedHosts.Contains(uri.Host))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.UrlHostNotAllowlisted,
                $"Ollama package host '{uri.Host}' is not allowlisted.");
        }

        if (!PinnedMetadataByUrl.TryGetValue(uri.AbsoluteUri, out var metadata))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.MissingPinnedMetadata,
                "No pinned version/digest metadata exists for this Ollama package URL.");
        }

        if (string.IsNullOrWhiteSpace(metadata.Sha256))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.ExpectedDigestMissing,
                "Expected SHA256 metadata is missing for this Ollama package URL.",
                metadata);
        }

        return OllamaPackageTrustValidationResult.Success(metadata);
    }

    /// <summary>
    /// Validates a downloaded package archive by computing its SHA-256 hash
    /// and comparing it against the expected pinned digest.
    /// </summary>
    /// <param name="archivePath">Path to the downloaded archive file.</param>
    /// <param name="metadata">Pinned metadata containing the expected hash.</param>
    /// <returns>Validation result indicating whether the file integrity matches.</returns>
    public static OllamaPackageTrustValidationResult ValidateDownloadedPackage(string archivePath, OllamaPackageMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.Sha256))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.ExpectedDigestMissing,
                "Expected SHA256 metadata is missing for this Ollama package URL.",
                metadata);
        }

        var actualSha = ComputeSha256Hex(archivePath);
        if (!string.Equals(actualSha, metadata.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.DigestMismatch,
                "Ollama package SHA256 verification failed.",
                metadata,
                actualSha);
        }

        return OllamaPackageTrustValidationResult.Success(metadata, actualSha);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a file and returns it as a lowercase hex string.
    /// </summary>
    public static string ComputeSha256Hex(string path) =>
        CryptoUtils.ComputeSha256Hex(path);

    /// <summary>
    /// Returns the full path where the Windows trust attestation JSON file
    /// should be stored on the SSD (under <c>windows/tools/ollama/</c>).
    /// </summary>
    public static string GetTrustAttestationPath(string ssdRoot) =>
        Path.Combine(ssdRoot, SsdLayout.WindowsOllama, TrustAttestationFileName);

    /// <summary>
    /// Returns the full path where the macOS trust attestation JSON file
    /// should be stored on the SSD (under <c>mac/tools/ollama/</c>).
    /// </summary>
    public static string GetMacTrustAttestationPath(string ssdRoot) =>
        Path.Combine(ssdRoot, SsdLayout.MacOllama, TrustAttestationFileName);

    /// <summary>
    /// Writes a Windows trust attestation file to the SSD after the Windows
    /// Ollama package has been downloaded and verified. Mirrors
    /// <see cref="WriteMacTrustAttestation"/> for the Mac side.
    /// </summary>
    /// <param name="ssdRoot">Root path of the portable SSD.</param>
    /// <param name="metadata">Verified package metadata to attest.</param>
    public static void WriteTrustAttestation(string ssdRoot, OllamaPackageMetadata metadata) =>
        WriteTrustAttestationCore(GetTrustAttestationPath(ssdRoot), metadata);

    /// <summary>
    /// Writes a macOS trust attestation file to the SSD after the Mac Ollama
    /// package has been downloaded, hash-verified, and arm64-validated.
    /// </summary>
    /// <param name="ssdRoot">Root path of the portable SSD.</param>
    /// <param name="metadata">Verified package metadata to attest.</param>
    public static void WriteMacTrustAttestation(string ssdRoot, OllamaPackageMetadata metadata) =>
        WriteTrustAttestationCore(GetMacTrustAttestationPath(ssdRoot), metadata);

    private static void WriteTrustAttestationCore(string attestationPath, OllamaPackageMetadata metadata)
    {
        var attestation = new OllamaPackageTrustAttestation
        {
            Version = metadata.Version,
            Url = metadata.Url,
            Sha256 = metadata.Sha256,
            VerifiedAtUtc = DateTime.UtcNow
        };

        Directory.CreateDirectory(Path.GetDirectoryName(attestationPath)!);
        File.WriteAllText(attestationPath, JsonSerializer.Serialize(attestation, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Validates that the Windows Ollama binary is safe to execute by checking
    /// the source URL, attestation presence, and attestation contents against
    /// the Windows trust attestation under <c>windows/tools/ollama/</c>.
    /// </summary>
    /// <param name="ssdRoot">Root path of the portable SSD.</param>
    /// <param name="urlText">The package source URL to validate against.</param>
    /// <returns>Validation result indicating whether execution is permitted.</returns>
    public static OllamaPackageTrustValidationResult ValidateExecutionAttestation(string ssdRoot, string? urlText) =>
        ValidateExecutionAttestationCore(GetTrustAttestationPath(ssdRoot), urlText);

    /// <summary>
    /// Validates that the macOS Ollama binary is safe to execute by checking
    /// the source URL, attestation presence, and attestation contents against
    /// the Mac trust attestation under <c>mac/tools/ollama/</c>. Shares the
    /// same validator core as <see cref="ValidateExecutionAttestation"/>.
    /// </summary>
    /// <param name="ssdRoot">Root path of the portable SSD.</param>
    /// <param name="urlText">The macOS package source URL (typically
    /// <see cref="DefaultMacPackage"/>'s URL).</param>
    public static OllamaPackageTrustValidationResult ValidateMacExecutionAttestation(string ssdRoot, string? urlText) =>
        ValidateExecutionAttestationCore(GetMacTrustAttestationPath(ssdRoot), urlText);

    private static OllamaPackageTrustValidationResult ValidateExecutionAttestationCore(string attestationPath, string? urlText)
    {
        // First validate the source URL itself.
        var sourceValidation = ValidatePackageSource(urlText);
        if (!sourceValidation.IsTrusted || sourceValidation.Metadata is null)
        {
            return sourceValidation;
        }

        // Check that a trust attestation file exists on the SSD.
        if (!File.Exists(attestationPath))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.AttestationMissing,
                "Trusted package attestation is missing. Re-download Ollama from the pinned source.",
                sourceValidation.Metadata);
        }

        // Deserialize and validate the attestation contents.
        OllamaPackageTrustAttestation? attestation;
        try
        {
            attestation = JsonSerializer.Deserialize<OllamaPackageTrustAttestation>(File.ReadAllText(attestationPath));
        }
        catch
        {
            attestation = null;
        }

        if (attestation is null)
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.AttestationMissing,
                "Trusted package attestation is invalid. Re-download Ollama from the pinned source.",
                sourceValidation.Metadata);
        }

        // Verify the attestation URL matches the pinned source.
        if (!string.Equals(attestation.Url, sourceValidation.Metadata.Url, StringComparison.Ordinal))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.AttestationUrlMismatch,
                "Trusted package attestation URL does not match the pinned source. Re-download Ollama.",
                sourceValidation.Metadata);
        }

        // Verify the attestation SHA-256 matches the pinned digest.
        if (!string.Equals(attestation.Sha256, sourceValidation.Metadata.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.AttestationDigestMismatch,
                "Trusted package attestation digest does not match pinned metadata. Re-download Ollama.",
                sourceValidation.Metadata);
        }

        return OllamaPackageTrustValidationResult.Success(sourceValidation.Metadata);
    }

    /// <summary>
    /// Validates that the macOS Ollama binary at <paramref name="binaryPath"/>
    /// contains an arm64 Mach-O slice. Universal payloads pass as long as
    /// arm64 is one of their slices; pure-arm64 payloads also pass; pure
    /// x86_64 payloads fail with <see cref="OllamaPackageTrustFailureReason.Arm64SliceMissing"/>.
    /// </summary>
    public static OllamaPackageTrustValidationResult ValidateArm64Slice(string binaryPath, OllamaPackageMetadata? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.BinaryMissing,
                $"macOS Ollama binary is missing at '{binaryPath}'.",
                metadata);
        }

        if (!MachOArchInspector.ContainsArm64Slice(binaryPath))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.Arm64SliceMissing,
                "macOS Ollama payload missing arm64 slice. Apple Silicon is required for the supported Mac baseline.",
                metadata);
        }

        return OllamaPackageTrustValidationResult.Success(metadata ?? DefaultMacPackage);
    }
}
