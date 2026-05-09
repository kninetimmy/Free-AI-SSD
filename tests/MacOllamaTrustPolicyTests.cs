using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Mac side of the Ollama trust policy. Mirrors
/// <see cref="OllamaPackageTrustPolicyTests"/> but exercises the Mac
/// attestation path under <c>mac/tools/ollama/</c> and the Apple Silicon
/// (arm64) slice gate.
///
/// MAC38: no static pin — metadata constructed inline.
/// </summary>
public sealed class MacOllamaTrustPolicyTests
{
    private const string SampleMacUrl =
        "https://github.com/ollama/ollama/releases/download/v0.20.0/Ollama-darwin.zip";
    private const string SampleWindowsUrl =
        "https://github.com/ollama/ollama/releases/download/v0.20.0/ollama-windows-amd64.zip";
    private const string SampleSha256 =
        "1111111111111111111111111111111111111111111111111111111111111111";

    private static OllamaPackageMetadata SampleMacMetadata(string? sha = null) =>
        new("v0.20.0", SampleMacUrl, sha ?? SampleSha256);

    private static OllamaPackageMetadata SampleWindowsMetadata(string? sha = null) =>
        new("v0.20.0", SampleWindowsUrl, sha ?? SampleSha256);

    [Fact]
    public void ValidatePackageSource_AcceptsMacUrl()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource(SampleMacUrl);

        Assert.True(result.IsTrusted);
    }

    [Fact]
    public void GetMacTrustAttestationPath_LivesUnderMacOllama()
    {
        using var ssd = new TempSsdRoot();
        var path = OllamaPackageTrustPolicy.GetMacTrustAttestationPath(ssd.Root);
        var expectedSuffix = Path.Combine(SsdLayout.MacOllama, "ollama-package-trust.json");
        Assert.EndsWith(expectedSuffix, path);
    }

    [Fact]
    public void ValidateMacExecutionAttestation_BlocksWhenAttestationMissing()
    {
        using var ssd = new TempSsdRoot();

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(ssd.Root);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.AttestationMissing, result.Reason);
    }

    [Fact]
    public void ValidateMacExecutionAttestation_BlocksWhenAttestationMalformed()
    {
        using var ssd = new TempSsdRoot();
        var path = OllamaPackageTrustPolicy.GetMacTrustAttestationPath(ssd.Root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not valid json");

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(ssd.Root);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.AttestationMissing, result.Reason);
    }

    [Fact]
    public void ValidateMacExecutionAttestation_BlocksOnNonAllowlistedAttestationUrl()
    {
        using var ssd = new TempSsdRoot();
        var bogus = new OllamaPackageMetadata(
            "v0.20.0",
            "https://evil.example.com/ollama-darwin.zip",
            SampleSha256);
        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssd.Root, bogus);

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(ssd.Root);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.UrlHostNotAllowlisted, result.Reason);
    }

    [Fact]
    public void ValidateMacExecutionAttestation_BlocksOnMalformedDigest()
    {
        using var ssd = new TempSsdRoot();
        var tampered = new OllamaPackageMetadata("v0.20.0", SampleMacUrl, "not-a-sha-256");
        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssd.Root, tampered);

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(ssd.Root);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.AttestationDigestMismatch, result.Reason);
    }

    [Fact]
    public void ValidateMacExecutionAttestation_AllowsAfterAttestationWrite()
    {
        using var ssd = new TempSsdRoot();
        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssd.Root, SampleMacMetadata());

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(ssd.Root);

        Assert.True(result.IsTrusted);
        Assert.Equal(SampleMacUrl, result.Metadata?.Url);
    }

    /// <summary>
    /// Writing the Mac attestation must not stomp on the Windows attestation
    /// (and vice versa) — they live in separate paths so a single SSD can
    /// hold both runtimes.
    /// </summary>
    [Fact]
    public void Mac_And_Windows_Attestations_Are_Independent()
    {
        using var ssd = new TempSsdRoot();
        OllamaPackageTrustPolicy.WriteTrustAttestation(ssd.Root, SampleWindowsMetadata());
        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssd.Root, SampleMacMetadata());

        var winPath = OllamaPackageTrustPolicy.GetTrustAttestationPath(ssd.Root);
        var macPath = OllamaPackageTrustPolicy.GetMacTrustAttestationPath(ssd.Root);

        Assert.NotEqual(winPath, macPath);
        Assert.True(File.Exists(winPath));
        Assert.True(File.Exists(macPath));
        Assert.True(OllamaPackageTrustPolicy.ValidateExecutionAttestation(ssd.Root).IsTrusted);
        Assert.True(OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(ssd.Root).IsTrusted);
    }

    [Fact]
    public void ValidateArm64Slice_RejectsPureX86_64Payload()
    {
        using var temp = new TempBinary(MachOFixtures.SingleMachO64(x86_64: true));

        var result = OllamaPackageTrustPolicy.ValidateArm64Slice(temp.Path);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.Arm64SliceMissing, result.Reason);
    }

    [Fact]
    public void ValidateArm64Slice_AcceptsUniversalWithArm64Slice()
    {
        using var temp = new TempBinary(MachOFixtures.FatUniversalArm64AndX86());

        var result = OllamaPackageTrustPolicy.ValidateArm64Slice(temp.Path);

        Assert.True(result.IsTrusted);
    }

    [Fact]
    public void ValidateArm64Slice_AcceptsPureArm64Payload()
    {
        using var temp = new TempBinary(MachOFixtures.SingleMachO64(x86_64: false));

        var result = OllamaPackageTrustPolicy.ValidateArm64Slice(temp.Path);

        Assert.True(result.IsTrusted);
    }

    [Fact]
    public void ValidateArm64Slice_RejectsMissingBinary()
    {
        var path = Path.Combine(Path.GetTempPath(), $"freeaissd-not-a-real-file-{Guid.NewGuid():N}");

        var result = OllamaPackageTrustPolicy.ValidateArm64Slice(path);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.BinaryMissing, result.Reason);
    }
}

/// <summary>
/// Disposable temporary SSD-shaped directory. Tests use this to avoid leaking
/// scratch directories under TEMP when assertions throw.
/// </summary>
internal sealed class TempSsdRoot : IDisposable
{
    public string Root { get; }

    public TempSsdRoot()
    {
        Root = Path.Combine(Path.GetTempPath(), $"freeaissd-mac-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

internal sealed class TempBinary : IDisposable
{
    public string Path { get; }

    public TempBinary(byte[] content)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"freeaissd-bin-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(Path, content);
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { }
    }
}
