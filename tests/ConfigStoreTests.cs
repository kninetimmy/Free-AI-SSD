using System.Security.Cryptography;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Tests;

/// <summary>
/// Real-crypto tests for <see cref="ConfigStore"/> and the symmetric save path
/// on <see cref="SsdEncryption"/>. No mocks on the encrypt/decrypt surface —
/// every test derives a real key, encrypts a real <see cref="PortableConfig"/>,
/// and round-trips through disk.
/// </summary>
public sealed class ConfigStoreTests
{
    [Fact]
    public async Task SaveEncryptedConfig_RoundTrips_WithSameMaterial()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);

            // Seed an encrypted drive in-memory (no plaintext ever on disk).
            var original = new PortableConfig { OllamaPort = 54321 };
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, original, "pw-round-trip");

            var modified = new PortableConfig { OllamaPort = 11111, RetrievalTopK = 9 };
            await SsdEncryption.SaveEncryptedConfigAsync(root, modified, material);

            var ok = SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "pw-round-trip", out var decrypted, out var decryptedMaterial, out var error);

            Assert.True(ok, error);
            Assert.NotNull(decrypted);
            Assert.Equal(11111, decrypted!.OllamaPort);
            Assert.Equal(9, decrypted.RetrievalTopK);
            Assert.NotNull(decryptedMaterial);
            Assert.Equal(material.Iterations, decryptedMaterial!.Iterations);
            Assert.Equal(material.Scheme, decryptedMaterial.Scheme);
            Assert.Equal(material.Salt, decryptedMaterial.Salt);
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public async Task SaveEncryptedConfig_UsesFreshNoncePerCall()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var config = new PortableConfig { OllamaPort = 42 };
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, config, "pw-nonce");

            var encryptedPath = Path.Combine(root, SsdLayout.Config, SsdEncryption.EncryptedConfigFileName);
            var firstPayload = await File.ReadAllBytesAsync(encryptedPath);

            await SsdEncryption.SaveEncryptedConfigAsync(root, config, material);
            var secondPayload = await File.ReadAllBytesAsync(encryptedPath);

            Assert.NotEqual(firstPayload, secondPayload);
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public async Task SaveEncryptedConfig_WritesBothArtifactsConsistently()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, new PortableConfig(), "pw-pair");

            await SsdEncryption.SaveEncryptedConfigAsync(root, new PortableConfig { OllamaPort = 7 }, material);

            var encryptedPath = Path.Combine(root, SsdLayout.Config, SsdEncryption.EncryptedConfigFileName);
            var statePath = Path.Combine(root, SsdLayout.Config, SsdEncryption.StateFileName);

            Assert.True(File.Exists(encryptedPath));
            Assert.True(File.Exists(statePath));
            Assert.False(File.Exists(encryptedPath + ".tmp"));
            Assert.False(File.Exists(statePath + ".tmp"));
            Assert.False(File.Exists(encryptedPath + ".bak"));
            Assert.False(File.Exists(statePath + ".bak"));
            Assert.True(SsdEncryption.IsEncryptionEnabled(root));
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public async Task SaveEncryptedConfig_RollsBackBlob_WhenStateRenameFails()
    {
        // Inject a failure at the state-file rename by holding an exclusive handle
        // on the .tmp path so File.Replace/File.Move throw. The pre-existing encrypted
        // blob must remain the authoritative copy (no half-written replacement).
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var first = new PortableConfig { OllamaPort = 1000 };
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, first, "pw-rollback");

            var encryptedPath = Path.Combine(root, SsdLayout.Config, SsdEncryption.EncryptedConfigFileName);
            var statePath = Path.Combine(root, SsdLayout.Config, SsdEncryption.StateFileName);
            var priorBlob = await File.ReadAllBytesAsync(encryptedPath);
            var priorState = await File.ReadAllBytesAsync(statePath);

            // Lock the state file itself so the File.Replace on it fails.
            using (var stateLock = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var second = new PortableConfig { OllamaPort = 2000 };
                await Assert.ThrowsAnyAsync<Exception>(
                    () => SsdEncryption.SaveEncryptedConfigAsync(root, second, material));
            }

            // Blob must match the pre-save copy after rollback.
            var rolledBackBlob = await File.ReadAllBytesAsync(encryptedPath);
            Assert.Equal(priorBlob, rolledBackBlob);

            // State must also still be intact (it was locked read-only; the rename never
            // completed). The drive should still decrypt to the original config.
            var ok = SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "pw-rollback", out var recovered, out _, out var error);
            Assert.True(ok, error);
            Assert.Equal(1000, recovered!.OllamaPort);
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public async Task EnableConfigEncryption_InMemoryOverload_WritesNoPlaintext()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var plaintextPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");

            var material = await SsdEncryption.EnableConfigEncryptionAsync(
                root,
                new PortableConfig { OllamaPort = 9876, NetworkApiKey = "shhh" },
                "pw-no-plaintext");

            Assert.False(File.Exists(plaintextPath));
            Assert.True(File.Exists(Path.Combine(root, SsdLayout.Config, SsdEncryption.EncryptedConfigFileName)));
            Assert.NotNull(material);
            Assert.Equal(SsdEncryption.SchemeName, material.Scheme);

            var ok = SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "pw-no-plaintext", out var decrypted, out _, out var error);
            Assert.True(ok, error);
            Assert.Equal(9876, decrypted!.OllamaPort);
            Assert.Equal("shhh", decrypted.NetworkApiKey);
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public async Task ConfigStore_SerializesConcurrentSaves()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, new PortableConfig(), "pw-concurrent");

            var store = new ConfigStore();
            store.UnlockSession(material);

            // Fire a burst of saves. The store's semaphore must serialize them so no
            // two File.Replace calls collide.
            var tasks = new List<Task>();
            for (var i = 0; i < 16; i++)
            {
                var port = 10000 + i;
                tasks.Add(store.SaveAsync(root, new PortableConfig { OllamaPort = port }, CancellationToken.None));
            }

            await Task.WhenAll(tasks);

            // Final state must decrypt to one of the written configs.
            var ok = SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "pw-concurrent", out var decrypted, out _, out var error);
            Assert.True(ok, error);
            Assert.InRange(decrypted!.OllamaPort, 10000, 10015);
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public async Task ConfigStore_FlushAsync_WaitsForInFlightSave()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, new PortableConfig(), "pw-flush");

            var store = new ConfigStore();
            store.UnlockSession(material);

            // Kick off a save that we deliberately do not await.
            var saveTask = store.SaveAsync(root, new PortableConfig { OllamaPort = 5555 }, CancellationToken.None);

            // FlushAsync with a generous timeout must return only once the save completes.
            await store.FlushAsync(TimeSpan.FromSeconds(5));
            Assert.True(saveTask.IsCompletedSuccessfully, "FlushAsync returned while a save was still running.");
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public async Task ConfigStore_FlushAsync_TimesOutWithoutThrowing_WhenSaveIsBlocked()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var material = await SsdEncryption.EnableConfigEncryptionAsync(root, new PortableConfig(), "pw-flush-timeout");

            var store = new ConfigStore();
            store.UnlockSession(material);

            // Block writes by holding an exclusive lock on the encrypted blob for the
            // duration of the save attempt. File.Replace will retry-fail; the save
            // task throws — but that is irrelevant to FlushAsync, which must bound
            // its wait and return cleanly without propagating the error.
            var encryptedPath = Path.Combine(root, SsdLayout.Config, SsdEncryption.EncryptedConfigFileName);
            using var blockingHandle = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.None);

            // Start an in-flight save that will fail but keeps the gate held while it does.
            var saveTask = Task.Run(async () =>
            {
                try { await store.SaveAsync(root, new PortableConfig(), CancellationToken.None); }
                catch { /* expected: blob is locked */ }
            });

            // Tight timeout — Flush must return without throwing even if the save
            // has not yet completed.
            var flush = store.FlushAsync(TimeSpan.FromMilliseconds(50));
            await flush; // must complete without throwing

            // Release the lock so the save task can finish draining.
            blockingHandle.Dispose();
            await saveTask;
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public void ConfigStore_LockSession_ZeroesKey()
    {
        var store = new ConfigStore();
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        var original = key.ToArray();

        store.UnlockSession(new UnlockMaterial(key, new byte[16], 210_000, SsdEncryption.SchemeName));
        Assert.True(store.IsSessionUnlocked);

        store.LockSession();

        Assert.False(store.IsSessionUnlocked);
        // The store holds the same byte[] reference we handed in; LockSession must
        // have zeroed it in place so a memory-scan attacker cannot recover the key.
        Assert.All(key, b => Assert.Equal(0, b));
        Assert.NotEqual(original, key);
    }

    [Fact]
    public async Task ConfigStore_SaveAsync_FailsClosed_OnNetworkModeWithoutEncryption()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var store = new ConfigStore();

            var dangerous = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkRequireApiKey = true,
                NetworkApiKey = "shared-secret",
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.SaveAsync(root, dangerous, CancellationToken.None));
            Assert.Equal(PortableConfig.NetworkModeEncryptionRequiredMessage, ex.Message);

            var plaintextPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");
            Assert.False(File.Exists(plaintextPath));
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public async Task ConfigStore_SaveAsync_FailsClosed_OnNonLoopbackBindWithoutApiKey()
    {
        // #114: a non-loopback (LAN-reachable) bind with no API key would serve the
        // Runner API unauthenticated. The write chokepoint must refuse to persist it
        // — even with RequireApiKey unchecked — so the drive can't reach that state.
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var store = new ConfigStore();

            var wideOpen = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkBindAddress = "0.0.0.0",
                NetworkRequireApiKey = false,
                NetworkApiKey = "",
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.SaveAsync(root, wideOpen, CancellationToken.None));
            Assert.Equal(PortableConfig.NetworkApiKeyRequiredForLanMessage, ex.Message);

            var plaintextPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");
            Assert.False(File.Exists(plaintextPath));
        }
        finally { CleanupTempRoot(root); }
    }

    [Fact]
    public async Task ConfigStore_SaveAsync_AllowsLoopbackBind_WithoutApiKey()
    {
        // #114 guard must target non-loopback only: a device-only loopback bind with
        // no key is the normal case and must still save.
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var store = new ConfigStore();

            var loopback = new PortableConfig
            {
                NetworkModeEnabled = true,
                NetworkBindAddress = "127.0.0.1",
                NetworkRequireApiKey = false,
                NetworkApiKey = "",
                OllamaPort = 12345,
            };

            await store.SaveAsync(root, loopback, CancellationToken.None);

            var plaintextPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");
            Assert.True(File.Exists(plaintextPath));
        }
        finally { CleanupTempRoot(root); }
    }

    /// <summary>
    /// Stage 3 integration test: simulates the Runner wiring path.
    /// TryUnlockPortableConfigWithMaterial → UnlockSession → SaveAsync → LockSession
    /// → re-unlock confirms the edit persisted and the key is zeroed after lock.
    /// </summary>
    [Fact]
    public async Task RunnerWiring_UnlockSaveLockReUnlock_RoundTrips()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);

            // Seed an encrypted drive.
            var initial = new PortableConfig { OllamaPort = 11434 };
            await SsdEncryption.EnableConfigEncryptionAsync(root, initial, "runner-stage3-pw");

            // Simulate Runner unlock: TryUnlockPortableConfigWithMaterial → UnlockSession.
            var ok = SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "runner-stage3-pw", out var unlocked, out var material, out var error);
            Assert.True(ok, error);
            Assert.NotNull(unlocked);
            Assert.NotNull(material);

            var store = new ConfigStore();
            store.UnlockSession(material!);
            Assert.True(store.IsSessionUnlocked);

            // Simulate a Runner save (post-unlock config edit).
            var edited = new PortableConfig { OllamaPort = 22222, RetrievalTopK = 7 };
            await store.SaveAsync(root, edited, CancellationToken.None);

            // Simulate OnClosing: flush then lock.
            await store.FlushAsync(TimeSpan.FromSeconds(5));
            store.LockSession();
            Assert.False(store.IsSessionUnlocked);

            // Key must be zeroed in place.
            Assert.All(material!.DerivedKey, b => Assert.Equal(0, b));

            // Re-unlock and verify the edit persisted.
            var ok2 = SsdEncryption.TryUnlockPortableConfigWithMaterial(
                root, "runner-stage3-pw", out var reUnlocked, out _, out var error2);
            Assert.True(ok2, error2);
            Assert.Equal(22222, reUnlocked!.OllamaPort);
            Assert.Equal(7, reUnlocked.RetrievalTopK);
        }
        finally { CleanupTempRoot(root); }
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
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
