using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Mac side of the Ollama trust policy. Mirrors
/// <see cref="OllamaPackageTrustPolicyTests"/> but exercises the Mac
/// attestation path under <c>mac/tools/ollama/</c>, the Mac default
/// package, and the Apple Silicon (arm64) slice gate.
/// </summary>
public sealed class MacOllamaTrustPolicyTests
{
    /// <summary>
    /// The Mac default URL must round-trip through ValidatePackageSource so
    /// callers can use <see cref="OllamaPackageTrustPolicy.DefaultMacPackage"/>
    /// without having to register the URL separately.
    /// </summary>
    [Fact]
    public void ValidatePackageSource_AcceptsPinnedMacUrl()
    {
        var result = OllamaPackageTrustPolicy.ValidatePackageSource(OllamaPackageTrustPolicy.DefaultMacPackage.Url);

        Assert.True(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustPolicy.DefaultMacPackage.Sha256, result.Metadata?.Sha256);
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

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(
            ssd.Root, OllamaPackageTrustPolicy.DefaultMacPackage.Url);

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

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(
            ssd.Root, OllamaPackageTrustPolicy.DefaultMacPackage.Url);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.AttestationMissing, result.Reason);
    }

    [Fact]
    public void ValidateMacExecutionAttestation_BlocksOnUrlMismatch()
    {
        using var ssd = new TempSsdRoot();
        // Write a Mac attestation that claims the Windows URL — the validator
        // must refuse this because it's pinning to DefaultMacPackage.
        var bogus = OllamaPackageTrustPolicy.DefaultWindowsPackage with { Sha256 = OllamaPackageTrustPolicy.DefaultMacPackage.Sha256 };
        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssd.Root, bogus);

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(
            ssd.Root, OllamaPackageTrustPolicy.DefaultMacPackage.Url);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.AttestationUrlMismatch, result.Reason);
    }

    [Fact]
    public void ValidateMacExecutionAttestation_BlocksOnDigestMismatch()
    {
        using var ssd = new TempSsdRoot();
        var tampered = OllamaPackageTrustPolicy.DefaultMacPackage with { Sha256 = new string('0', 64) };
        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssd.Root, tampered);

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(
            ssd.Root, OllamaPackageTrustPolicy.DefaultMacPackage.Url);

        Assert.False(result.IsTrusted);
        Assert.Equal(OllamaPackageTrustFailureReason.AttestationDigestMismatch, result.Reason);
    }

    [Fact]
    public void ValidateMacExecutionAttestation_AllowsAfterAttestationWrite()
    {
        using var ssd = new TempSsdRoot();
        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssd.Root, OllamaPackageTrustPolicy.DefaultMacPackage);

        var result = OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(
            ssd.Root, OllamaPackageTrustPolicy.DefaultMacPackage.Url);

        Assert.True(result.IsTrusted);
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
        OllamaPackageTrustPolicy.WriteTrustAttestation(ssd.Root, OllamaPackageTrustPolicy.DefaultWindowsPackage);
        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssd.Root, OllamaPackageTrustPolicy.DefaultMacPackage);

        var winPath = OllamaPackageTrustPolicy.GetTrustAttestationPath(ssd.Root);
        var macPath = OllamaPackageTrustPolicy.GetMacTrustAttestationPath(ssd.Root);

        Assert.NotEqual(winPath, macPath);
        Assert.True(File.Exists(winPath));
        Assert.True(File.Exists(macPath));
        Assert.True(OllamaPackageTrustPolicy.ValidateExecutionAttestation(ssd.Root, OllamaPackageTrustPolicy.DefaultWindowsPackage.Url).IsTrusted);
        Assert.True(OllamaPackageTrustPolicy.ValidateMacExecutionAttestation(ssd.Root, OllamaPackageTrustPolicy.DefaultMacPackage.Url).IsTrusted);
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
