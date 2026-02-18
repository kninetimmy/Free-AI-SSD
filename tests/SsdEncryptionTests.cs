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
