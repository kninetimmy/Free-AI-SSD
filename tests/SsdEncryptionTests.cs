using System.Text.Json;
using System.Text.Json.Nodes;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class SsdEncryptionTests
{
    [Fact]
    public async Task EnableConfigEncryption_EncryptsPlainConfig_AndUnlocksWithPassword()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configPath = Path.Combine(root, "config", "portable-config.json");
            var config = new PortableConfig
            {
                OllamaPort = 12500,
                Models = new List<ModelConfigEntry>
                {
                    new() { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed }
                }
            };
            await config.SaveAsync(configPath);

            await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, "test-password-123");

            Assert.True(SsdEncryption.IsEncryptionEnabled(root));
            Assert.False(File.Exists(configPath));
            Assert.True(File.Exists(Path.Combine(root, "config", SsdEncryption.EncryptedConfigFileName)));

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);
            Assert.True(unlocked);
            Assert.NotNull(decrypted);
            Assert.True(string.IsNullOrWhiteSpace(error));
            Assert.Equal(12500, decrypted!.OllamaPort);
            Assert.Contains(decrypted.Models, m => m.Name == "llama3.2:3b");
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task TryUnlockPortableConfig_WithWrongPassword_ReturnsFalse()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configPath = Path.Combine(root, "config", "portable-config.json");
            await new PortableConfig().SaveAsync(configPath);
            await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, "correct-password");

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "wrong-password", out var decrypted, out var error);
            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Incorrect password.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void TryUnlockPortableConfig_WhenMetadataFilesAreMissing_ReturnsMissingError()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Encrypted drive metadata is missing.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void TryUnlockPortableConfig_WhenMetadataJsonIsUnreadable_ReturnsUnreadableError()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "{");
            File.WriteAllText(EncryptedPath(root), "{}");

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Encrypted drive metadata is unreadable.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void TryUnlockPortableConfig_WhenStateMetadataIsInvalid_ReturnsInvalidMetadataError()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "null");
            File.WriteAllText(EncryptedPath(root), "{}");

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Encrypted drive metadata is invalid.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task TryUnlockPortableConfig_WhenSaltIsMissing_ReturnsParametersMissingError()
    {
        var root = CreateTempRoot();
        try
        {
            await SeedEncryptedConfigAsync(root, "test-password-123");
            MutateEncryptedConfigJson(root, node => node["salt"] = "");

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Encryption parameters are missing.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task TryUnlockPortableConfig_WhenIterationsAreInvalid_ReturnsParametersMissingError()
    {
        var root = CreateTempRoot();
        try
        {
            await SeedEncryptedConfigAsync(root, "test-password-123");
            MutateEncryptedConfigJson(root, node => node["iterations"] = 0);

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Encryption parameters are missing.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task TryUnlockPortableConfig_WhenBase64FieldsAreMalformed_ReturnsDecryptFailure()
    {
        var root = CreateTempRoot();
        try
        {
            await SeedEncryptedConfigAsync(root, "test-password-123");
            MutateEncryptedConfigJson(root, node => node["salt"] = "not-base64!");

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Failed to decrypt portable config.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task TryUnlockPortableConfig_WhenEncryptedPayloadIsTampered_ReturnsIncorrectPassword()
    {
        var root = CreateTempRoot();
        try
        {
            await SeedEncryptedConfigAsync(root, "test-password-123");
            MutateEncryptedConfigJson(root, node =>
            {
                var ciphertext = Convert.FromBase64String(node["ciphertext"]!.GetValue<string>());
                ciphertext[0] ^= 0xFF;
                node["ciphertext"] = Convert.ToBase64String(ciphertext);
            });

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Incorrect password.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void CorruptStateFile_WithEncryptedArtifacts_FailsClosed()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "{ this-is-not-valid-json");
            File.WriteAllText(EncryptedPath(root), "{}");

            Assert.True(SsdEncryption.IsEffectivelyEncryptedForWriteGuard(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void MissingStateFile_WithEncryptedArtifacts_IsEffectivelyEncrypted()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(EncryptedPath(root), "{}");

            Assert.True(SsdEncryption.IsEffectivelyEncryptedForWriteGuard(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void ValidDisabledState_WithoutEncryptedArtifacts_IsNotEffectivelyEncrypted()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "{\"enabled\":false}");

            Assert.False(SsdEncryption.IsEffectivelyEncryptedForWriteGuard(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public void ValidEnabledState_IsEffectivelyEncrypted()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "{\"enabled\":true}");

            Assert.True(SsdEncryption.IsEffectivelyEncryptedForWriteGuard(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    private static async Task SeedEncryptedConfigAsync(string root, string password)
    {
        SsdLayout.EnsureStructure(root);
        var configPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");
        await new PortableConfig().SaveAsync(configPath);
        await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, password);
    }

    private static void MutateEncryptedConfigJson(string root, Action<JsonObject> mutate)
    {
        var encryptedPath = EncryptedPath(root);
        var json = File.ReadAllText(encryptedPath);
        var node = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Encrypted config payload is not a JSON object.");
        mutate(node);
        File.WriteAllText(
            encryptedPath,
            node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string StatePath(string root) => Path.Combine(root, SsdLayout.Config, SsdEncryption.StateFileName);
    private static string EncryptedPath(string root) => Path.Combine(root, SsdLayout.Config, SsdEncryption.EncryptedConfigFileName);

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
