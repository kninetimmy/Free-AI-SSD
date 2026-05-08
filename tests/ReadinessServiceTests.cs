using System.Security.Cryptography;
using FreeAiSsd.PrepApp;
using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Tests;

/// <summary>
/// Pins the four readiness states MAC29 has to handle correctly so the v1.3.9
/// Mac field-test scenario (encrypted config + model on disk → all-green)
/// stops false-flagging Fail.
/// </summary>
public sealed class ReadinessServiceTests
{
    [Fact]
    public async Task ReadinessChecks_EncryptedConfigPlusModelOnDisk_AllGreen()
    {
        // The v1.3.9 Mac field-test scenario: encrypted blob exists, the Mac
        // sidecar pulled a starter model so manifests + content-addressed blobs
        // exist on disk, but the encrypted config has no Models[] entry yet
        // (the sidecar zeroizes the passphrase before pulls run, so it can't
        // re-open the encrypted blob to write back an Installed entry). Both
        // checks should pass against the encrypted blob + disk truth.
        using var tempRoot = new TempRoot();
        WriteEncryptedConfigStub(tempRoot.Path);
        var (modelName, _) = WriteContentAddressedModelOnDisk(tempRoot.Path, "llama3.2:1b", "fake-model-bytes-for-readiness-test");

        var service = new ReadinessService(new ModelService());
        var checks = await service.RunReadinessChecksAsync(tempRoot.Path, _ => { }, CancellationToken.None);

        var configCheck = checks.Single(c => c.Check == "Config.json valid");
        var modelCheck = checks.Single(c => c.Check == "≥1 installed model");
        Assert.True(configCheck.Passed,
            $"Config.json valid should pass when encrypted blob is present (was: {configCheck.Result}).");
        Assert.True(modelCheck.Passed,
            $"≥1 installed model should pass via disk-truth self-consistency (was: {modelCheck.Result}).");
        Assert.Contains(modelName, ModelOperations.DiscoverModelsOnDisk(Path.Combine(tempRoot.Path, SsdLayout.Models)));
    }

    [Fact]
    public async Task ReadinessChecks_EncryptedConfigButNoModelsOnDisk_OnlyModelCheckFails()
    {
        using var tempRoot = new TempRoot();
        WriteEncryptedConfigStub(tempRoot.Path);
        Directory.CreateDirectory(Path.Combine(tempRoot.Path, SsdLayout.Models));

        var service = new ReadinessService(new ModelService());
        var checks = await service.RunReadinessChecksAsync(tempRoot.Path, _ => { }, CancellationToken.None);

        Assert.True(checks.Single(c => c.Check == "Config.json valid").Passed);
        var modelCheck = checks.Single(c => c.Check == "≥1 installed model");
        Assert.False(modelCheck.Passed);
        Assert.Contains("No models found on disk", modelCheck.Result);
    }

    [Fact]
    public async Task ReadinessChecks_PlaintextConfigWithPinnedHash_VerifiesAgainstPin()
    {
        // The Windows path: plaintext config with a pinned Sha256 should still
        // verify against the pin, not the filename self-consistency. This is
        // the strict integrity guarantee MAC29 must preserve for Windows.
        using var tempRoot = new TempRoot();
        var (modelName, contentSha) = WriteContentAddressedModelOnDisk(tempRoot.Path, "llama3.2:1b", "fake-windows-model-bytes");

        var config = new PortableConfig
        {
            Models = new List<ModelConfigEntry>
            {
                new() { Name = modelName, Status = ModelInstallStatus.Installed, Sha256 = contentSha }
            }
        };
        var configPath = Path.Combine(tempRoot.Path, config.ConfigRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await config.SaveAsync(configPath);

        var service = new ReadinessService(new ModelService());
        var checks = await service.RunReadinessChecksAsync(tempRoot.Path, _ => { }, CancellationToken.None);

        Assert.True(checks.Single(c => c.Check == "Config.json valid").Passed);
        Assert.True(checks.Single(c => c.Check == "≥1 installed model").Passed);
    }

    [Fact]
    public async Task ReadinessChecks_NoConfigAtAll_BothChecksFail()
    {
        using var tempRoot = new TempRoot();
        // No config file, no encrypted blob, no models on disk.
        Directory.CreateDirectory(Path.Combine(tempRoot.Path, SsdLayout.Models));

        var service = new ReadinessService(new ModelService());
        var checks = await service.RunReadinessChecksAsync(tempRoot.Path, _ => { }, CancellationToken.None);

        Assert.False(checks.Single(c => c.Check == "Config.json valid").Passed);
        Assert.False(checks.Single(c => c.Check == "≥1 installed model").Passed);
    }

    private static void WriteEncryptedConfigStub(string root)
    {
        // Hand-rolled minimal encrypted blob. The readiness check parses the
        // JSON and validates the `scheme` field — it does NOT decrypt — so any
        // shape with the right scheme passes. Real encrypted blobs are produced
        // by SsdEncryption.EnableConfigEncryptionAsync; the cross-language
        // round-trip is pinned by MacEncryptedConfigCrossLanguageTests.
        var configDir = Path.Combine(root, SsdLayout.Config);
        Directory.CreateDirectory(configDir);
        var blob = new
        {
            version = 1,
            scheme = SsdEncryption.SchemeName,
            iterations = 210_000,
            salt = Convert.ToBase64String(new byte[16]),
            nonce = Convert.ToBase64String(new byte[12]),
            tag = Convert.ToBase64String(new byte[16]),
            ciphertext = Convert.ToBase64String(new byte[1]),
            createdAtUtc = DateTime.UtcNow
        };
        File.WriteAllText(
            Path.Combine(configDir, SsdEncryption.EncryptedConfigFileName),
            JsonSerializer.Serialize(blob));
    }

    private static (string ModelName, string ContentSha) WriteContentAddressedModelOnDisk(string root, string modelTag, string blobBody)
    {
        var modelsDir = Path.Combine(root, SsdLayout.Models);
        var blobsDir = Path.Combine(modelsDir, "blobs");
        Directory.CreateDirectory(blobsDir);

        // Compute the SHA-256 of the blob body and use it as the filename so
        // ReadinessService's self-consistency check (filename digest matches
        // file content) passes — exactly the way Ollama lays out blobs.
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(blobBody);
        var sha = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        var blobName = $"sha256-{sha}";
        File.WriteAllBytes(Path.Combine(blobsDir, blobName), bodyBytes);

        // Manifest tree: manifests/registry.ollama.ai/library/<name>/<tag>
        // Layer digest must match the blob filename so FindModelBlobForModel
        // resolves to the same file the verifier hashes.
        var (name, tag) = SplitModelTag(modelTag);
        var manifestDir = Path.Combine(modelsDir, "manifests", "registry.ollama.ai", "library", name);
        Directory.CreateDirectory(manifestDir);
        var manifest = new
        {
            schemaVersion = 2,
            config = new { mediaType = "application/vnd.ollama.image.config.v1+json", digest = "sha256:0000", size = 1 },
            layers = new[]
            {
                new { mediaType = "application/vnd.ollama.image.layer.model", digest = $"sha256:{sha}", size = (long)bodyBytes.Length }
            }
        };
        File.WriteAllText(Path.Combine(manifestDir, tag), JsonSerializer.Serialize(manifest));

        return (modelTag, sha);
    }

    private static (string Name, string Tag) SplitModelTag(string modelTag)
    {
        var colon = modelTag.LastIndexOf(':');
        return (modelTag[..colon], modelTag[(colon + 1)..]);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; }

        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"freeaissd-readiness-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
