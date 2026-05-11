using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for <see cref="DriveConfigurationDetector"/> — the C6 detector that
/// decides whether a candidate SSD is Unconfigured / ConfiguredEmpty /
/// FullyConfigured based purely on file-presence probes. Each test creates an
/// isolated temp directory and cleans up in finally blocks.
/// </summary>
public sealed class DriveConfigurationDetectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Detect_NullOrEmptyOrWhitespaceRoot_ReturnsEmptySnapshot(string? root)
    {
        var snapshot = DriveConfigurationDetector.Detect(root);

        Assert.Same(DriveConfigurationDetector.Empty, snapshot);
    }

    [Fact]
    public void Detect_NonexistentRoot_ReturnsEmptySnapshot()
    {
        var missing = Path.Combine(Path.GetTempPath(), "free-ai-ssd-tests", Guid.NewGuid().ToString("N"));

        var snapshot = DriveConfigurationDetector.Detect(missing);

        Assert.Same(DriveConfigurationDetector.Empty, snapshot);
    }

    [Fact]
    public void Detect_EmptyDirectory_ReturnsUnconfiguredEmptySnapshot()
    {
        var root = CreateTempRoot();
        try
        {
            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.Unconfigured, snapshot.State);
            Assert.False(snapshot.HasOurConfig);
            Assert.False(snapshot.IsConfigEncrypted);
            Assert.False(snapshot.HasModels);
            Assert.Equal(0, snapshot.ModelManifestCount);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_OnlyPlaintextConfig_ReturnsConfiguredEmpty_AndIsNotEncrypted()
    {
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.ConfiguredEmpty, snapshot.State);
            Assert.True(snapshot.HasOurConfig);
            Assert.False(snapshot.IsConfigEncrypted);
            Assert.False(snapshot.HasModels);
            Assert.Equal(0, snapshot.ModelManifestCount);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_OnlyEncryptedConfig_ReturnsConfiguredEmpty_AndIsEncrypted()
    {
        var root = CreateTempRoot();
        try
        {
            WriteEncryptedConfig(root);

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.ConfiguredEmpty, snapshot.State);
            Assert.True(snapshot.HasOurConfig);
            Assert.True(snapshot.IsConfigEncrypted);
            Assert.False(snapshot.HasModels);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_BothPlaintextAndEncryptedConfig_PrefersPlaintextSignal_NotEncrypted()
    {
        // Mid-migration edge: both files coexist briefly. The plaintext-newer
        // branch of SsdEncryption.TryMigratePlaintextAsync handles cleanup at
        // unlock time; until then, presence of plaintext means the unlock UX
        // is unnecessary, so IsConfigEncrypted = false is the honest signal.
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);
            WriteEncryptedConfig(root);

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.ConfiguredEmpty, snapshot.State);
            Assert.True(snapshot.HasOurConfig);
            Assert.False(snapshot.IsConfigEncrypted);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_ConfigPresent_OneManifest_ReturnsFullyConfigured()
    {
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);
            SeedManifest(root, "llama3", "latest");

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.FullyConfigured, snapshot.State);
            Assert.True(snapshot.HasOurConfig);
            Assert.True(snapshot.HasModels);
            Assert.Equal(1, snapshot.ModelManifestCount);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_ConfigPresent_ManyManifests_CountsThem()
    {
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);
            SeedManifest(root, "llama3", "latest");
            SeedManifest(root, "qwen2.5", "7b");
            SeedManifest(root, "phi3", "3.8b");

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.FullyConfigured, snapshot.State);
            Assert.True(snapshot.HasModels);
            Assert.Equal(3, snapshot.ModelManifestCount);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_ManifestsWithoutConfig_TreatedAsUnconfigured_ForeignDataGuard()
    {
        // The watch-for #1 from the C6 backlog entry: a user's own Ollama
        // install on the same external disk must NOT be claimed as ours.
        var root = CreateTempRoot();
        try
        {
            SeedManifest(root, "llama3", "latest");
            SeedManifest(root, "qwen2.5", "7b");

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Same(DriveConfigurationDetector.Empty, snapshot);
            Assert.Equal(DriveConfigurationState.Unconfigured, snapshot.State);
            Assert.False(snapshot.HasOurConfig);
            Assert.False(snapshot.HasModels);
            Assert.Equal(0, snapshot.ModelManifestCount);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_ConfigPresent_ManifestsDirEmpty_ReturnsConfiguredEmpty()
    {
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);
            Directory.CreateDirectory(Path.Combine(root, SsdLayout.Models, "manifests"));

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.ConfiguredEmpty, snapshot.State);
            Assert.True(snapshot.HasOurConfig);
            Assert.False(snapshot.HasModels);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_ConfigPresent_ManifestsDirMissing_ReturnsConfiguredEmpty()
    {
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);
            // No models/manifests/ directory at all.

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.ConfiguredEmpty, snapshot.State);
            Assert.True(snapshot.HasOurConfig);
            Assert.False(snapshot.HasModels);
            Assert.Equal(0, snapshot.ModelManifestCount);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_RealisticLayout_AfterEnsureStructure_FullyConfigured()
    {
        // End-to-end: SsdLayout.EnsureStructure builds the canonical tree,
        // a real manifest is dropped at the canonical Ollama path, and the
        // detector returns FullyConfigured.
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            WriteEncryptedConfig(root);
            SeedManifest(root, "llama3.2", "3b");

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.FullyConfigured, snapshot.State);
            Assert.True(snapshot.HasOurConfig);
            Assert.True(snapshot.IsConfigEncrypted);
            Assert.True(snapshot.HasModels);
            Assert.Equal(1, snapshot.ModelManifestCount);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Detect_ManifestCountExceedsCap_StopsAtCap()
    {
        var root = CreateTempRoot();
        try
        {
            WritePlaintextConfig(root);

            // Seed cap + 10 manifest files in a flat dir under manifests/.
            var manifestsRoot = Path.Combine(root, SsdLayout.Models, "manifests");
            Directory.CreateDirectory(manifestsRoot);
            var total = DriveConfigurationDetector.ManifestEnumerationCap + 10;
            for (var i = 0; i < total; i++)
            {
                File.WriteAllText(Path.Combine(manifestsRoot, $"m{i}"), "{}");
            }

            var snapshot = DriveConfigurationDetector.Detect(root);

            Assert.Equal(DriveConfigurationState.FullyConfigured, snapshot.State);
            Assert.True(snapshot.HasModels);
            Assert.Equal(DriveConfigurationDetector.ManifestEnumerationCap, snapshot.ModelManifestCount);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void Empty_StaticSnapshot_HasExpectedShape()
    {
        var empty = DriveConfigurationDetector.Empty;

        Assert.Equal(DriveConfigurationState.Unconfigured, empty.State);
        Assert.False(empty.HasOurConfig);
        Assert.False(empty.IsConfigEncrypted);
        Assert.False(empty.HasModels);
        Assert.Equal(0, empty.ModelManifestCount);
    }

    private static void WritePlaintextConfig(string root)
    {
        var configDir = Path.Combine(root, SsdLayout.Config);
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, DriveConfigurationDetector.PlaintextConfigFileName),
            "{}");
    }

    private static void WriteEncryptedConfig(string root)
    {
        var configDir = Path.Combine(root, SsdLayout.Config);
        Directory.CreateDirectory(configDir);
        File.WriteAllText(
            Path.Combine(configDir, SsdEncryption.EncryptedConfigFileName),
            "{}");
    }

    private static void SeedManifest(string root, string model, string tag)
    {
        var manifestDir = Path.Combine(
            root, SsdLayout.Models, "manifests", "registry.ollama.ai", "library", model);
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, tag), "{}");
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "free-ai-ssd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CleanupTempRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
