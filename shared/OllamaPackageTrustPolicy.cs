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
    public static OllamaPackageTrustValidationResult Success(OllamaPackageMetadata? metadata = null, string? actualSha256 = null) =>
        new(true, OllamaPackageTrustFailureReason.None, "Trusted Ollama package.", metadata, actualSha256);

    public static OllamaPackageTrustValidationResult Fail(OllamaPackageTrustFailureReason reason, string message, OllamaPackageMetadata? metadata = null, string? actualSha256 = null) =>
        new(false, reason, message, metadata, actualSha256);
}

/// <summary>
/// Immutable metadata for a verified Ollama package, including its
/// download URL and SHA-256 hash for integrity verification. Sourced
/// dynamically at staging time from the upstream release's vendor-published
/// sha256sum.txt; never hardcoded in this repo.
/// </summary>
public sealed record OllamaPackageMetadata(string Version, string Url, string Sha256);

/// <summary>
/// Persisted attestation record written to the SSD after a trusted package
/// has been downloaded and verified. Used at runtime to gate execution
/// without re-downloading or re-hashing the multi-hundred-MB binary.
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
/// Trust chain (MAC38):
/// <list type="number">
/// <item>URL allowlist: only HTTPS to <c>github.com</c> and
/// <c>objects.githubusercontent.com</c>.</item>
/// <item>Vendor-published SHA-256 (from each Ollama release's
/// <c>sha256sum.txt</c> asset, fetched alongside the binary at staging
/// time) verifies the bytes on disk.</item>
/// <item>An on-SSD attestation file records the URL + SHA-256 of the
/// verified payload. The runtime trust gate accepts the attestation as the
/// source of truth — it doesn't re-verify the binary on every launch
/// (multi-hundred-MB rehash is too slow).</item>
/// </list>
/// Mirrors the trust model already used for the .NET Desktop Runtime
/// (vendor-published <c>releases.json</c> SHA-512 + on-disk staged file).
/// </summary>
public static class OllamaPackageTrustPolicy
{
    /// <summary>
    /// Set of trusted hostnames from which Ollama packages may be downloaded.
    /// Only HTTPS URLs from these hosts pass source validation.
    /// </summary>
    private static readonly HashSet<string> AllowlistedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com"
    };

    public static string TrustAttestationFileName => "ollama-package-trust.json";

    /// <summary>
    /// Validates that a package source URL is well-formed, uses HTTPS, and
    /// comes from an allowlisted host. Returns success with no metadata —
    /// version + hash come from the upstream release's <c>sha256sum.txt</c>
    /// at staging time, not from a static dictionary.
    /// </summary>
    /// <param name="urlText">The download URL to validate.</param>
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

        return OllamaPackageTrustValidationResult.Success();
    }

    /// <summary>
    /// Validates a downloaded package archive by computing its SHA-256 hash
    /// and comparing it against the expected digest carried in
    /// <paramref name="metadata"/>. The expected digest is sourced from the
    /// upstream release's vendor-published <c>sha256sum.txt</c> asset at
    /// staging time, not from a static pin.
    /// </summary>
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
    public static void WriteTrustAttestation(string ssdRoot, OllamaPackageMetadata metadata) =>
        WriteTrustAttestationCore(GetTrustAttestationPath(ssdRoot), metadata);

    /// <summary>
    /// Writes a macOS trust attestation file to the SSD after the Mac Ollama
    /// package has been downloaded, hash-verified, and arm64-validated.
    /// </summary>
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
    /// Validates that the Windows Ollama binary is safe to execute by
    /// checking the on-SSD attestation under <c>windows/tools/ollama/</c>.
    /// The attestation IS the source of truth: it was written only after the
    /// PrepApp staging path verified the bytes against the vendor-published
    /// SHA-256 from the upstream release's <c>sha256sum.txt</c>.
    /// </summary>
    public static OllamaPackageTrustValidationResult ValidateExecutionAttestation(string ssdRoot) =>
        ValidateExecutionAttestationCore(GetTrustAttestationPath(ssdRoot));

    /// <summary>
    /// Validates that the macOS Ollama binary is safe to execute by checking
    /// the on-SSD attestation under <c>mac/tools/ollama/</c>. Shares the
    /// validator core with the Windows variant.
    /// </summary>
    public static OllamaPackageTrustValidationResult ValidateMacExecutionAttestation(string ssdRoot) =>
        ValidateExecutionAttestationCore(GetMacTrustAttestationPath(ssdRoot));

    private static OllamaPackageTrustValidationResult ValidateExecutionAttestationCore(string attestationPath)
    {
        if (!File.Exists(attestationPath))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.AttestationMissing,
                "Trusted package attestation is missing. Re-stage Ollama from PrepApp.");
        }

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
                "Trusted package attestation is invalid. Re-stage Ollama from PrepApp.");
        }

        var sourceCheck = ValidatePackageSource(attestation.Url);
        if (!sourceCheck.IsTrusted)
        {
            // Reuse the source validator's reason so the diagnostic stays
            // precise (e.g. UrlHostNotAllowlisted vs UrlSchemeNotHttps).
            return OllamaPackageTrustValidationResult.Fail(
                sourceCheck.Reason,
                $"Trusted package attestation URL is not allowed: {sourceCheck.Message}");
        }

        if (!IsWellFormedSha256Hex(attestation.Sha256))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.AttestationDigestMismatch,
                "Trusted package attestation digest is not a well-formed SHA-256.");
        }

        var metadata = new OllamaPackageMetadata(attestation.Version, attestation.Url, attestation.Sha256);
        return OllamaPackageTrustValidationResult.Success(metadata);
    }

    private static bool IsWellFormedSha256Hex(string? value)
    {
        if (value is null || value.Length != 64) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex) return false;
        }
        return true;
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

        return OllamaPackageTrustValidationResult.Success(metadata);
    }
}
