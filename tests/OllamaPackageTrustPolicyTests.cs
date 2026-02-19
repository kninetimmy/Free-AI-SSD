using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for the Ollama package trust policy — the security gate that validates
/// download URLs, verifies SHA-256 digests of downloaded packages, and checks
/// execution attestation before allowing Ollama to run.
///
/// Covers: URL validation (scheme, host allowlist, malformed), digest verification
/// (match, mismatch, missing), and execution attestation (present/absent).
/// </summary>
public sealed class OllamaPackageTrustPolicyTests
{
    /// <summary>Rejects non-HTTPS URLs to prevent insecure downloads.</summary>
    [Fact]
    public void ValidatePackageSource_RejectsHttp()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource("http://github.com/ollama/ollama/releases/download/v0.5.7/ollama-windows-amd64.zip");

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.UrlSchemeNotHttps, result.Reason);
    }

    /// <summary>Rejects HTTPS URLs from non-allowlisted hosts (only github.com allowed).</summary>
    [Fact]
    public void ValidatePackageSource_RejectsNonAllowlistedHost()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource("https://example.com/ollama-windows-amd64.zip");

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.UrlHostNotAllowlisted, result.Reason);
    }

    /// <summary>Rejects completely malformed URLs that can't be parsed.</summary>
    [Fact]
    public void ValidatePackageSource_RejectsMalformedUrl()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource("://not-a-url");

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.UrlMalformed, result.Reason);
    }

    /// <summary>Accepts the default allowlisted HTTPS URL for the Ollama Windows package.</summary>
    [Fact]
    public void ValidatePackageSource_AcceptsAllowlistedHttpsUrl()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource(OllamaPackageTrustPolicy.DefaultWindowsPackage.Url);

        Assert.True(result.IsTrusted);
        Assert.NotNull(result.Metadata);
    }

    /// <summary>
    /// Verifies that a file with a matching SHA-256 hash passes digest validation.
    /// </summary>
    [Fact]
    public void ValidateDownloadedPackage_PassesOnExactSha256Match()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "abc");
            var sha = OllamaPackageTrustPolicy.ComputeSha256Hex(tempPath);
            var metadata = new OllamaPackageMetadata("v-test", "https://github.com/ollama/ollama/releases/download/v-test/ollama-windows-amd64.zip", sha);

            var result = OllamaPackageTrustPolicy.ValidateDownloadedPackage(tempPath, metadata);

            Assert.True(result.IsTrusted);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Verifies that a mismatched SHA-256 hash is detected and reported.
    /// Prevents execution of tampered or corrupted downloads.
    /// </summary>
    [Fact]
    public void ValidateDownloadedPackage_FailsOnDigestMismatch()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "abc");
            var metadata = new OllamaPackageMetadata("v-test", "https://github.com/ollama/ollama/releases/download/v-test/ollama-windows-amd64.zip", "deadbeef");

            var result = OllamaPackageTrustPolicy.ValidateDownloadedPackage(tempPath, metadata);

            Assert.False(result.IsTrusted);
            Assert.Equal(OllamaPackageTrustFailureReason.DigestMismatch, result.Reason);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Verifies that an empty expected digest blocks execution.
    /// This catches configuration errors where the hash wasn't recorded.
    /// </summary>
    [Fact]
    public void ValidateDownloadedPackage_FailsWhenExpectedDigestMissing()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "abc");
            var metadata = new OllamaPackageMetadata("v-test", "https://github.com/ollama/ollama/releases/download/v-test/ollama-windows-amd64.zip", string.Empty);

            var result = OllamaPackageTrustPolicy.ValidateDownloadedPackage(tempPath, metadata);

            Assert.False(result.IsTrusted);
            Assert.Equal(OllamaPackageTrustFailureReason.ExpectedDigestMissing, result.Reason);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Verifies that execution is blocked when no trust attestation file exists.
    /// The Runner must see an attestation written by PrepApp before it will run Ollama.
    /// </summary>
    [Fact]
    public void ValidateExecutionAttestation_BlocksWhenAttestationMissing()
    {
        var ssdRoot = Path.Combine(Path.GetTempPath(), $"freeaissd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ssdRoot);

        try
        {
            var result = OllamaPackageTrustPolicy.ValidateExecutionAttestation(ssdRoot, OllamaPackageTrustPolicy.DefaultWindowsPackage.Url);

            Assert.False(result.IsTrusted);
            Assert.Equal(OllamaPackageTrustFailureReason.AttestationMissing, result.Reason);
        }
        finally
        {
            Directory.Delete(ssdRoot, recursive: true);
        }
    }

    /// <summary>
    /// Verifies the full trust chain: write attestation → validate → execution allowed.
    /// </summary>
    [Fact]
    public void ValidateExecutionAttestation_AllowsExecutionAfterAttestation()
    {
        var ssdRoot = Path.Combine(Path.GetTempPath(), $"freeaissd-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ssdRoot);

        try
        {
            OllamaPackageTrustPolicy.WriteTrustAttestation(ssdRoot, OllamaPackageTrustPolicy.DefaultWindowsPackage);

            var result = OllamaPackageTrustPolicy.ValidateExecutionAttestation(ssdRoot, OllamaPackageTrustPolicy.DefaultWindowsPackage.Url);

            Assert.True(result.IsTrusted);
        }
        finally
        {
            Directory.Delete(ssdRoot, recursive: true);
        }
    }
}
