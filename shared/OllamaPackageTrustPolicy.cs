using System.Collections.Generic;
using System.Security.Cryptography;

namespace FreeAiSsd.Shared;

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
    AttestationUrlMismatch
}

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

public sealed record OllamaPackageMetadata(string Version, string Url, string Sha256);

public sealed class OllamaPackageTrustAttestation
{
    public required string Version { get; init; }
    public required string Url { get; init; }
    public required string Sha256 { get; init; }
    public required DateTime VerifiedAtUtc { get; init; }
}

public static class OllamaPackageTrustPolicy
{
    public static readonly OllamaPackageMetadata DefaultWindowsPackage = new(
        Version: "v0.5.7",
        Url: "https://github.com/ollama/ollama/releases/download/v0.5.7/ollama-windows-amd64.zip",
        Sha256: "11ec2270a5205228fddeaa15c8319a0f0167c0ee7420d19c43714312d4761d2d");

    private static readonly HashSet<string> AllowlistedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com"
    };

    private static readonly Dictionary<string, OllamaPackageMetadata> PinnedMetadataByUrl = new(StringComparer.Ordinal)
    {
        [DefaultWindowsPackage.Url] = DefaultWindowsPackage
    };

    public static string TrustAttestationFileName => "ollama-package-trust.json";

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

    public static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string GetTrustAttestationPath(string ssdRoot) =>
        Path.Combine(ssdRoot, SsdLayout.Ollama, TrustAttestationFileName);

    public static void WriteTrustAttestation(string ssdRoot, OllamaPackageMetadata metadata)
    {
        var attestation = new OllamaPackageTrustAttestation
        {
            Version = metadata.Version,
            Url = metadata.Url,
            Sha256 = metadata.Sha256,
            VerifiedAtUtc = DateTime.UtcNow
        };

        var attestationPath = GetTrustAttestationPath(ssdRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(attestationPath)!);
        File.WriteAllText(attestationPath, JsonSerializer.Serialize(attestation, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static OllamaPackageTrustValidationResult ValidateExecutionAttestation(string ssdRoot, string? urlText)
    {
        var sourceValidation = ValidatePackageSource(urlText);
        if (!sourceValidation.IsTrusted || sourceValidation.Metadata is null)
        {
            return sourceValidation;
        }

        var attestationPath = GetTrustAttestationPath(ssdRoot);
        if (!File.Exists(attestationPath))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.AttestationMissing,
                "Trusted package attestation is missing. Re-download Ollama from the pinned source.",
                sourceValidation.Metadata);
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
                "Trusted package attestation is invalid. Re-download Ollama from the pinned source.",
                sourceValidation.Metadata);
        }

        if (!string.Equals(attestation.Url, sourceValidation.Metadata.Url, StringComparison.Ordinal))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.AttestationUrlMismatch,
                "Trusted package attestation URL does not match the pinned source. Re-download Ollama.",
                sourceValidation.Metadata);
        }

        if (!string.Equals(attestation.Sha256, sourceValidation.Metadata.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return OllamaPackageTrustValidationResult.Fail(
                OllamaPackageTrustFailureReason.AttestationDigestMismatch,
                "Trusted package attestation digest does not match pinned metadata. Re-download Ollama.",
                sourceValidation.Metadata);
        }

        return OllamaPackageTrustValidationResult.Success(sourceValidation.Metadata);
    }
}
