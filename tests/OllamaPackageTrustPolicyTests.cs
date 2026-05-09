using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for the Ollama package trust policy — the security gate that validates
/// download URLs (HTTPS + allowlisted host), verifies SHA-256 digests of
/// downloaded packages against vendor-published hashes, and checks the on-SSD
/// execution attestation before allowing Ollama to run.
///
/// MAC38: there is no static URL or SHA pin in this repo anymore. The metadata
/// these tests use is constructed inline so the tests don't depend on whatever
/// version is "current" upstream.
/// </summary>
public sealed class OllamaPackageTrustPolicyTests
{
    private const string SampleWindowsUrl =
        "https://github.com/ollama/ollama/releases/download/v0.20.0/ollama-windows-amd64.zip";
    private const string SampleSha256 =
        "1111111111111111111111111111111111111111111111111111111111111111";

    private static OllamaPackageMetadata SampleMetadata(string? sha = null) =>
        new("v0.20.0", SampleWindowsUrl, sha ?? SampleSha256);

    [Fact]
    public void ValidatePackageSource_RejectsHttp()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource(
            "http://github.com/ollama/ollama/releases/download/v0.5.7/ollama-windows-amd64.zip");

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.UrlSchemeNotHttps, result.Reason);
    }

    [Fact]
    public void ValidatePackageSource_RejectsNonAllowlistedHost()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource(
            "https://example.com/ollama-windows-amd64.zip");

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.UrlHostNotAllowlisted, result.Reason);
    }

    [Fact]
    public void ValidatePackageSource_RejectsMalformedUrl()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource("://not-a-url");

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.UrlMalformed, result.Reason);
    }

    [Fact]
    public void ValidatePackageSource_AcceptsAnyAllowlistedHttpsUrl()
    {
        // MAC38: validator no longer requires the URL to live in a static
        // pinned dictionary. Any HTTPS URL on github.com or
        // objects.githubusercontent.com is accepted; trust comes from the
        // vendor sha256sum.txt verification at staging time.
        var result = OllamaPackageTrustPolicy.ValidatePackageSource(SampleWindowsUrl);

        Assert.True(result.IsTrusted);
    }

    [Fact]
    public void ValidatePackageSource_AcceptsObjectsGithubusercontent()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource(
            "https://objects.githubusercontent.com/release/asset-id");

        Assert.True(result.IsTrusted);
    }

    [Fact]
    public void ValidateDownloadedPackage_PassesOnExactSha256Match()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "abc");
            var sha = OllamaPackageTrustPolicy.ComputeSha256Hex(tempPath);
            var metadata = SampleMetadata(sha);

            var result = OllamaPackageTrustPolicy.ValidateDownloadedPackage(tempPath, metadata);

            Assert.True(result.IsTrusted);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ValidateDownloadedPackage_FailsOnDigestMismatch()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "abc");
            var metadata = SampleMetadata("deadbeef");

            var result = OllamaPackageTrustPolicy.ValidateDownloadedPackage(tempPath, metadata);

            Assert.False(result.IsTrusted);
            Assert.Equal(OllamaPackageTrustFailureReason.DigestMismatch, result.Reason);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ValidateDownloadedPackage_FailsWhenExpectedDigestMissing()
    {
        var tempPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempPath, "abc");
            var metadata = SampleMetadata(string.Empty);

            var result = OllamaPackageTrustPolicy.ValidateDownloadedPackage(tempPath, metadata);

            Assert.False(result.IsTrusted);
            Assert.Equal(OllamaPackageTrustFailureReason.ExpectedDigestMissing, result.Reason);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ValidateExecutionAttestation_BlocksWhenAttestationMissing()
    {
        using var ssd = new TempSsdRoot();

        var result = OllamaPackageTrustPolicy.ValidateExecutionAttestation(ssd.Root);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.AttestationMissing, result.Reason);
    }

    [Fact]
    public void ValidateExecutionAttestation_AllowsExecutionAfterAttestation()
    {
        using var ssd = new TempSsdRoot();
        OllamaPackageTrustPolicy.WriteTrustAttestation(ssd.Root, SampleMetadata());

        var result = OllamaPackageTrustPolicy.ValidateExecutionAttestation(ssd.Root);

        Assert.True(result.IsTrusted);
        Assert.Equal(SampleWindowsUrl, result.Metadata?.Url);
    }

    [Fact]
    public void ValidateExecutionAttestation_RefusesAttestationWithNonAllowlistedUrl()
    {
        using var ssd = new TempSsdRoot();
        var bogus = new OllamaPackageMetadata(
            "v0.0.0",
            "https://evil.example.com/ollama.zip",
            SampleSha256);
        OllamaPackageTrustPolicy.WriteTrustAttestation(ssd.Root, bogus);

        var result = OllamaPackageTrustPolicy.ValidateExecutionAttestation(ssd.Root);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.UrlHostNotAllowlisted, result.Reason);
    }

    [Fact]
    public void ValidateExecutionAttestation_RefusesAttestationWithMalformedSha()
    {
        using var ssd = new TempSsdRoot();
        var bogus = new OllamaPackageMetadata("v0.20.0", SampleWindowsUrl, "not-a-real-sha");
        OllamaPackageTrustPolicy.WriteTrustAttestation(ssd.Root, bogus);

        var result = OllamaPackageTrustPolicy.ValidateExecutionAttestation(ssd.Root);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.AttestationDigestMismatch, result.Reason);
    }
}
