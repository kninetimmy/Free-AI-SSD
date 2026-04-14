using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Covers the Phase-A default-hardening changes to <see cref="PortableConfig"/>:
///  - The default <c>NetworkBindAddress</c> is loopback, not "0.0.0.0".
///  - <see cref="PortableConfig.SaveAsync"/> fails closed when Network Mode + Require
///    API Key are both on but SSD config encryption is not effectively enabled.
/// </summary>
public sealed class PortableConfigSaveGuardTests
{
    [Fact]
    public void FreshConfig_NetworkBindAddress_DefaultsToLoopback()
    {
        var config = new PortableConfig();
        Assert.Equal("127.0.0.1", config.NetworkBindAddress);
    }

    [Fact]
    public async Task SaveAsync_NetworkOffAndNotEncrypted_Succeeds()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");

            var config = new PortableConfig
            {
                NetworkModeEnabled = false,
                NetworkRequireApiKey = true,
            };

            await config.SaveAsync(configPath);

            Assert.True(File.Exists(configPath));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task SaveAsync_NetworkOnAndRequireKey_WithoutEncryption_Throws()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");

            var config = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkRequireApiKey = true,
                NetworkApiKey = "shared-secret",
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => config.SaveAsync(configPath));
            Assert.Equal(PortableConfig.NetworkModeEncryptionRequiredMessage, ex.Message);
            Assert.False(File.Exists(configPath));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task SaveAsync_NetworkOnAndRequireKey_WithEncryption_Succeeds()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");

            // Seed encrypted state by running the real encryption flow on a blank config.
            await new PortableConfig().SaveAsync(configPath);
            await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, "pw-123456");
            Assert.True(SsdEncryption.IsEffectivelyEncryptedForWriteGuard(root));

            // Now saving a config with Network Mode + Require API Key should succeed,
            // because the SSD is effectively encrypted.
            var config = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkRequireApiKey = true,
                NetworkApiKey = "shared-secret",
            };

            await config.SaveAsync(configPath);
            Assert.True(File.Exists(configPath));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task SaveAsync_NetworkOnButRequireKeyOff_WithoutEncryption_Succeeds()
    {
        // The guard only trips when BOTH NetworkModeEnabled and NetworkRequireApiKey
        // are true. Users who explicitly disable RequireApiKey are opting out of the
        // shared-secret model, so the plaintext-save guard does not apply.
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");

            var config = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkRequireApiKey = false,
            };

            await config.SaveAsync(configPath);
            Assert.True(File.Exists(configPath));
        }
        finally
        {
            CleanupTempRoot(root);
        }
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
