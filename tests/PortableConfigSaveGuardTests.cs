using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Tests;

/// <summary>
/// Covers the encrypted-config save guard and plaintext migration:
///  - The default <c>NetworkBindAddress</c> is loopback, not "0.0.0.0".
///  - <see cref="ConfigStore.SaveAsync"/> fails closed when Network Mode + Require API Key
///    are both on but SSD config encryption is not effectively enabled.
///  - <see cref="ConfigStore.SaveAsync"/> succeeds (encrypted round-trip) when the session
///    is unlocked.
///  - Finalize via in-memory <see cref="SsdEncryption.EnableConfigEncryptionAsync"/> leaves
///    no plaintext on disk and survives Network Mode + Require API Key.
///  - <see cref="SsdEncryption.TryMigratePlaintextAsync"/> absorbs a newer plaintext into
///    the encrypted blob (Branch A) or silently removes a stale plaintext (Branch B).
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
    public async Task ConfigStore_SaveAsync_NetworkOnAndRequireKey_UnlockedEncryptedSession_Succeeds()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);

            var original = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkRequireApiKey = true,
                NetworkApiKey = "shared-secret",
            };

            // Establish encrypted state via the in-memory finalize overload.
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, original, "pw-123456");
            Assert.True(SsdEncryption.IsEffectivelyEncryptedForWriteGuard(root));

            // Simulate a Runner save: unlock the session, then save through ConfigStore.
            var store = new ConfigStore();
            store.UnlockSession(material);

            var updated = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkRequireApiKey = true,
                NetworkApiKey = "updated-secret",
            };
            await store.SaveAsync(root, updated, CancellationToken.None);

            // Round-trip: decrypt and confirm the updated key survived.
            Assert.True(SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "pw-123456", out var roundTripped, out _, out _));
            Assert.Equal("updated-secret", roundTripped!.NetworkApiKey);

            // No plaintext on disk.
            Assert.False(File.Exists(Path.Combine(root, SsdLayout.Config, "portable-config.json")));
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

    // ── Finalize E2E ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Finalize_InMemoryEncrypt_NoPlaintextOnDisk()
    {
        // Seeds a pre-existing plaintext (as ModelService/ReadinessService write it during
        // prep operations before finalize runs) and asserts it is deleted by the in-memory
        // overload — not just a vacuous check on an empty directory.
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var plaintextPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");

            // Simulate pre-finalize plaintext written by ReadinessService.
            await new PortableConfig { NetworkModeEnabled = false }.SaveAsync(plaintextPath);
            Assert.True(File.Exists(plaintextPath));

            var config = new PortableConfig { NetworkModeEnabled = false };
            await SsdEncryption.EnableConfigEncryptionAsync(root, config, "pass-abc");

            Assert.False(File.Exists(plaintextPath));
            Assert.True(File.Exists(Path.Combine(root, SsdLayout.Config, SsdEncryption.EncryptedConfigFileName)));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task Finalize_NetworkModeAndApiKey_InMemoryEncrypt_RoundTrips()
    {
        // Regression: before Stage 4, finalize with NM+RequireApiKey threw because the
        // plaintext save guard fired before the encrypted artifact existed.
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);

            var config = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkRequireApiKey = true,
                NetworkApiKey = "api-key-123",
            };

            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, config, "pass-xyz");

            Assert.False(File.Exists(Path.Combine(root, SsdLayout.Config, "portable-config.json")));
            Assert.True(SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "pass-xyz", out var loaded, out _, out _));
            Assert.Equal("api-key-123", loaded!.NetworkApiKey);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    // ── Migration ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migration_PlaintextNewer_MergesIntoEncryptedAndDeletesPlaintext()
    {
        // Seeds Stephen's field scenario: encrypted blob exists, but subsequent saves
        // (pre-Stage-4 bug) wrote to plaintext. Plaintext is newer and contains the
        // live API key. Migration should absorb it and remove the plaintext.
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configDir = Path.Combine(root, SsdLayout.Config);
            var plaintextPath = Path.Combine(configDir, "portable-config.json");

            // Create the encrypted blob first.
            var staleConfig = new PortableConfig { NetworkApiKey = "stale-key" };
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, staleConfig, "pw");

            // Write a newer plaintext (simulates post-unlock saves landing in plaintext).
            // Bump mtime by touching the file after a short delay or setting it explicitly.
            var liveConfig = new PortableConfig { NetworkApiKey = "live-key" };
            await liveConfig.SaveAsync(plaintextPath);
            // Force plaintext mtime strictly after the encrypted blob.
            var encryptedMtime = File.GetLastWriteTimeUtc(Path.Combine(configDir, SsdEncryption.EncryptedConfigFileName));
            File.SetLastWriteTimeUtc(plaintextPath, encryptedMtime.AddSeconds(10));

            var result = await SsdEncryption.TryMigratePlaintextAsync(root, material);

            Assert.True(result.WasPlaintextNewer);
            Assert.NotNull(result.MergedConfig);
            Assert.Equal("live-key", result.MergedConfig!.NetworkApiKey);
            Assert.False(File.Exists(plaintextPath));

            // Encrypted blob should now contain the live key.
            Assert.True(SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "pw", out var unlocked, out _, out _));
            Assert.Equal("live-key", unlocked!.NetworkApiKey);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task Migration_EncryptedNewer_DeletesStaleTextSilently()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configDir = Path.Combine(root, SsdLayout.Config);
            var plaintextPath = Path.Combine(configDir, "portable-config.json");

            // Write plaintext first (stale), then create encrypted blob with a newer mtime.
            var staleConfig = new PortableConfig { NetworkApiKey = "stale" };
            await staleConfig.SaveAsync(plaintextPath);
            var staleMtime = File.GetLastWriteTimeUtc(plaintextPath);

            var liveConfig = new PortableConfig { NetworkApiKey = "current" };
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, liveConfig, "pw2");
            // Ensure encrypted blob is strictly newer than plaintext.
            File.SetLastWriteTimeUtc(
                Path.Combine(configDir, SsdEncryption.EncryptedConfigFileName),
                staleMtime.AddSeconds(10));

            var result = await SsdEncryption.TryMigratePlaintextAsync(root, material);

            Assert.False(result.WasPlaintextNewer);
            Assert.Null(result.MergedConfig);
            Assert.False(File.Exists(plaintextPath));

            // Encrypted blob is untouched.
            Assert.True(SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "pw2", out var unlocked, out _, out _));
            Assert.Equal("current", unlocked!.NetworkApiKey);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task Migration_EqualMtime_TreatsEncryptedAsAuthoritative()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configDir = Path.Combine(root, SsdLayout.Config);
            var plaintextPath = Path.Combine(configDir, "portable-config.json");

            var config = new PortableConfig { NetworkApiKey = "enc-key" };
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, config, "pw3");

            // Write plaintext with the same mtime as the encrypted blob.
            var encMtime = File.GetLastWriteTimeUtc(
                Path.Combine(configDir, SsdEncryption.EncryptedConfigFileName));
            await new PortableConfig { NetworkApiKey = "plain-key" }.SaveAsync(plaintextPath);
            File.SetLastWriteTimeUtc(plaintextPath, encMtime);

            var result = await SsdEncryption.TryMigratePlaintextAsync(root, material);

            Assert.False(result.WasPlaintextNewer);
            Assert.False(File.Exists(plaintextPath));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task Migration_NoPlaintext_ReturnsNoMigration()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);

            var config = new PortableConfig();
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, config, "pw4");

            var result = await SsdEncryption.TryMigratePlaintextAsync(root, material);

            Assert.False(result.WasPlaintextNewer);
            Assert.Null(result.MergedConfig);
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
