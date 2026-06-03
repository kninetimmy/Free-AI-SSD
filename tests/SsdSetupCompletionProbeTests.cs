using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreeAiSsd.PrepApp;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for <see cref="SsdSetupCompletionProbe"/> — the cheap, presence-only
/// inspection that drives the PrepApp launch-time "resume setup" prompt (#2).
/// Mirrors the temp-dir fixture style of <c>DriveConfigurationDetectorTests</c>
/// and reuses the content-addressed model layout from <c>ReadinessServiceTests</c>.
/// </summary>
public sealed class SsdSetupCompletionProbeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Inspect_NullOrEmptyRoot_Complete(string? root)
    {
        var result = SsdSetupCompletionProbe.Inspect(root);
        Assert.True(result.IsComplete);
        Assert.Equal(SsdSetupCompletionState.Complete, result.State);
    }

    [Fact]
    public void Inspect_MissingRoot_Complete()
    {
        var missing = Path.Combine(Path.GetTempPath(), "free-ai-ssd-tests", Guid.NewGuid().ToString("N"));
        Assert.True(SsdSetupCompletionProbe.Inspect(missing).IsComplete);
    }

    [Fact]
    public void Inspect_UnconfiguredDrive_Complete_EvenWithModels()
    {
        // Foreign-data guard: no config marker → never our drive → no prompt,
        // even if model manifests happen to be present.
        var root = CreateTempRoot();
        try
        {
            WriteContentAddressedModel(root, "llama3.2:1b", "foreign-model-bytes");

            var result = SsdSetupCompletionProbe.Inspect(root);

            Assert.True(result.IsComplete);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Inspect_ConfigButNoModels_ModelsMissingOrIncomplete()
    {
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);

            var result = SsdSetupCompletionProbe.Inspect(root);

            Assert.Equal(SsdSetupCompletionState.ModelsMissingOrIncomplete, result.State);
            Assert.False(result.IsComplete);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Inspect_ConfigPlusModelWithMissingBlob_ModelsMissingOrIncomplete()
    {
        // Interrupted / partial pull: manifest written but the blob it points at
        // is absent. DiscoverModelsOnDisk sees the model; FindModelBlobForModel
        // returns null → re-pull.
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);
            WriteContentAddressedModel(root, "llama3.2:1b", "model-bytes");
            // Delete the blob to simulate a torn download.
            var blobsDir = Path.Combine(root, SsdLayout.Models, "blobs");
            foreach (var blob in Directory.EnumerateFiles(blobsDir))
            {
                File.Delete(blob);
            }
            StageWindowsRunner(root); // runtime present, but models are broken

            var result = SsdSetupCompletionProbe.Inspect(root);

            Assert.Equal(SsdSetupCompletionState.ModelsMissingOrIncomplete, result.State);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Inspect_ConfigPlusModelButNoRuntime_RuntimeNotStaged()
    {
        // The exact soft-lock case: models pulled + config written, but the user
        // never finished staging the runner (the #1 footer-Continue trap).
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);
            WriteContentAddressedModel(root, "llama3.2:1b", "model-bytes");

            var result = SsdSetupCompletionProbe.Inspect(root);

            Assert.Equal(SsdSetupCompletionState.RuntimeNotStaged, result.State);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Inspect_ConfigPlusModelPlusWindowsRunner_Complete()
    {
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);
            WriteContentAddressedModel(root, "llama3.2:1b", "model-bytes");
            StageWindowsRunner(root);

            Assert.True(SsdSetupCompletionProbe.Inspect(root).IsComplete);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Inspect_ConfigPlusModelPlusMacRunner_Complete()
    {
        // A deliberate macOS-only prep (no Windows runner) must not false-flag.
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);
            WriteContentAddressedModel(root, "llama3.2:1b", "model-bytes");
            Directory.CreateDirectory(Path.Combine(root, SsdLayout.MacRunner));

            Assert.True(SsdSetupCompletionProbe.Inspect(root).IsComplete);
        }
        finally { Cleanup(root); }
    }

    private static void StageWindowsRunner(string root)
    {
        var runnerDir = Path.Combine(root, SsdLayout.WindowsRunner);
        Directory.CreateDirectory(runnerDir);
        File.WriteAllText(Path.Combine(runnerDir, "FreeAiSsd.Runner.exe"), "stub");
    }

    private static void WritePlaintextConfig(string root)
    {
        var configDir = Path.Combine(root, SsdLayout.Config);
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, DriveConfigurationDetector.PlaintextConfigFileName),
            "{}");
    }

    // Mirrors ReadinessServiceTests.WriteContentAddressedModelOnDisk: a real
    // Ollama-shaped manifest + content-addressed blob so DiscoverModelsOnDisk
    // and FindModelBlobForModel both resolve.
    private static void WriteContentAddressedModel(string root, string modelTag, string blobBody)
    {
        var modelsDir = Path.Combine(root, SsdLayout.Models);
        var blobsDir = Path.Combine(modelsDir, "blobs");
        Directory.CreateDirectory(blobsDir);

        var bodyBytes = Encoding.UTF8.GetBytes(blobBody);
        var sha = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(blobsDir, $"sha256-{sha}"), bodyBytes);

        var colon = modelTag.LastIndexOf(':');
        var name = modelTag[..colon];
        var tag = modelTag[(colon + 1)..];
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
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "free-ai-ssd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
