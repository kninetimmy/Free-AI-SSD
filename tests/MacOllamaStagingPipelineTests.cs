using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Integration-shaped tests for the verify-then-attest pipeline used by
/// PrepApp's macOS Ollama staging path. Synthesizes a "staged" SSD layout
/// in temp directories and checks that the pipeline refuses bad archives
/// (hash mismatch) and bad binaries (no arm64 slice) without writing the
/// trust attestation, and writes the attestation only on the happy path.
/// </summary>
public sealed class MacOllamaStagingPipelineTests
{
    [Fact]
    public void VerifyAndAttest_HappyPath_WritesAttestation()
    {
        using var ssd = new TempSsdRoot();
        var (archivePath, archiveSha) = WriteArchive(ssd.Root, "archive.zip");
        var binaryPath = WriteBinary(ssd.Root, "ollama", MachOFixtures.SingleMachO64(x86_64: false));
        var metadata = OllamaPackageTrustPolicy.DefaultMacPackage with { Sha256 = archiveSha };

        var result = MacOllamaStagingPipeline.VerifyAndAttest(ssd.Root, archivePath, binaryPath, metadata);

        Assert.True(result.Success);
        var attestationPath = OllamaPackageTrustPolicy.GetMacTrustAttestationPath(ssd.Root);
        Assert.True(File.Exists(attestationPath));
    }

    [Fact]
    public void VerifyAndAttest_RefusesArchiveWithWrongSha()
    {
        using var ssd = new TempSsdRoot();
        var (archivePath, _) = WriteArchive(ssd.Root, "archive.zip");
        var binaryPath = WriteBinary(ssd.Root, "ollama", MachOFixtures.SingleMachO64(x86_64: false));
        // Force a mismatch by claiming a different expected SHA.
        var metadata = OllamaPackageTrustPolicy.DefaultMacPackage with { Sha256 = new string('a', 64) };

        var result = MacOllamaStagingPipeline.VerifyAndAttest(ssd.Root, archivePath, binaryPath, metadata);

        Assert.False(result.Success);
        Assert.Equal(OllamaPackageTrustFailureReason.DigestMismatch, result.Failure!.Reason);
        Assert.False(File.Exists(OllamaPackageTrustPolicy.GetMacTrustAttestationPath(ssd.Root)));
    }

    [Fact]
    public void VerifyAndAttest_RefusesBinaryWithoutArm64Slice()
    {
        using var ssd = new TempSsdRoot();
        var (archivePath, archiveSha) = WriteArchive(ssd.Root, "archive.zip");
        var binaryPath = WriteBinary(ssd.Root, "ollama", MachOFixtures.SingleMachO64(x86_64: true));
        var metadata = OllamaPackageTrustPolicy.DefaultMacPackage with { Sha256 = archiveSha };

        var result = MacOllamaStagingPipeline.VerifyAndAttest(ssd.Root, archivePath, binaryPath, metadata);

        Assert.False(result.Success);
        Assert.Equal(OllamaPackageTrustFailureReason.Arm64SliceMissing, result.Failure!.Reason);
        Assert.False(File.Exists(OllamaPackageTrustPolicy.GetMacTrustAttestationPath(ssd.Root)));
    }

    [Fact]
    public void VerifyAndAttest_RefusesMissingBinary()
    {
        using var ssd = new TempSsdRoot();
        var (archivePath, archiveSha) = WriteArchive(ssd.Root, "archive.zip");
        var binaryPath = Path.Combine(ssd.Root, "does-not-exist", "ollama");
        var metadata = OllamaPackageTrustPolicy.DefaultMacPackage with { Sha256 = archiveSha };

        var result = MacOllamaStagingPipeline.VerifyAndAttest(ssd.Root, archivePath, binaryPath, metadata);

        Assert.False(result.Success);
        Assert.Equal(OllamaPackageTrustFailureReason.BinaryMissing, result.Failure!.Reason);
        Assert.False(File.Exists(OllamaPackageTrustPolicy.GetMacTrustAttestationPath(ssd.Root)));
    }

    private static (string Path, string Sha256) WriteArchive(string root, string fileName)
    {
        // Pseudorandom content keeps each test isolated and avoids accidental
        // SHA collisions with the real DefaultMacPackage hash.
        var content = Guid.NewGuid().ToByteArray();
        var path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, content);
        return (path, FreeAiSsd.Shared.Helpers.CryptoUtils.ComputeSha256Hex(path));
    }

    private static string WriteBinary(string root, string fileName, byte[] content)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }
}
