using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class OllamaPackageTrustPolicyTests
{
    [Fact]
    public void ValidatePackageSource_RejectsHttp()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource("http://github.com/ollama/ollama/releases/download/v0.5.7/ollama-windows-amd64.zip");

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.UrlSchemeNotHttps, result.Reason);
    }

    [Fact]
    public void ValidatePackageSource_RejectsNonAllowlistedHost()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource("https://example.com/ollama-windows-amd64.zip");

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
    public void ValidatePackageSource_AcceptsAllowlistedHttpsUrl()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource(OllamaPackageTrustPolicy.DefaultWindowsPackage.Url);

        Assert.True(result.IsTrusted);
        Assert.NotNull(result.Metadata);
    }

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
