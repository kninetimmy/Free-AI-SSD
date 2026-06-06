using System.Text.Json;
using System.Text.Json.Nodes;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for the SSD encryption subsystem (AES-256-GCM + PBKDF2-SHA256).
/// Covers the full encrypt-decrypt cycle, error handling for every failure mode
/// (wrong password, missing files, corrupt metadata, tampered ciphertext,
/// invalid parameters), and the "fail closed" write-guard security model.
///
/// Each test creates an isolated temp directory and cleans up in finally blocks.
/// </summary>
public sealed class SsdEncryptionTests
{
    /// <summary>
    /// End-to-end test: encrypts a config with a password, then decrypts it.
    /// Verifies the plaintext file is deleted, encrypted artifacts exist,
    /// and the decrypted config matches the original values.
    /// </summary>
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

    /// <summary>
    /// Verifies that an incorrect password is rejected with a clear error message.
    /// GCM authentication failure manifests as "Incorrect password."
    /// </summary>
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

    /// <summary>
    /// Verifies the error message when no encryption metadata files exist on disk.
    /// </summary>
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

    /// <summary>
    /// Verifies error handling when the state JSON file contains invalid JSON.
    /// </summary>
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

    /// <summary>
    /// Verifies error handling when the state file deserializes to null (e.g., "null" literal).
    /// </summary>
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

    /// <summary>
    /// Verifies that missing/empty salt is detected as a parameter validation error.
    /// </summary>
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

    /// <summary>
    /// Verifies that a zero iteration count is rejected as an invalid parameter
    /// (it falls below the hardened PBKDF2 floor).
    /// </summary>
    [Fact]
    public async Task TryUnlockPortableConfig_WhenIterationsAreZero_ReturnsInvalidParametersError()
    {
        var root = CreateTempRoot();
        try
        {
            await SeedEncryptedConfigAsync(root, "test-password-123");
            MutateEncryptedConfigJson(root, node => node["iterations"] = 0);

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Encryption parameters are invalid.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// Security regression (#113): a PBKDF2 iteration count tampered DOWN below the
    /// hardened floor must be rejected at the validation gate — never fed to the KDF.
    /// The count is read from attacker-editable portable-config.encrypted.json and is
    /// not authenticated by AES-GCM, so a downgrade must fail closed.
    /// </summary>
    [Fact]
    public async Task TryUnlockPortableConfig_WhenIterationsTamperedBelowFloor_FailsClosed()
    {
        var root = CreateTempRoot();
        try
        {
            await SeedEncryptedConfigAsync(root, "test-password-123");
            MutateEncryptedConfigJson(root, node => node["iterations"] = 1000);

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Encryption parameters are invalid.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// Security regression (#113): a PBKDF2 iteration count tampered UP past the sane
    /// ceiling must be rejected rather than honoured — otherwise a one-character edit
    /// turns unlock into a denial of service (the KDF runs for minutes/hours).
    /// </summary>
    [Fact]
    public async Task TryUnlockPortableConfig_WhenIterationsTamperedAboveCeiling_FailsClosed()
    {
        var root = CreateTempRoot();
        try
        {
            await SeedEncryptedConfigAsync(root, "test-password-123");
            MutateEncryptedConfigJson(root, node => node["iterations"] = 2_000_000_000);

            var unlocked = SsdEncryption.TryUnlockPortableConfig(root, "test-password-123", out var decrypted, out var error);

            Assert.False(unlocked);
            Assert.Null(decrypted);
            Assert.Equal("Encryption parameters are invalid.", error);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// Verifies that malformed Base64 in fields (not valid Base64) produces a
    /// generic decryption failure rather than an unhandled exception.
    /// </summary>
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

    /// <summary>
    /// Verifies that tampered ciphertext (bit-flipped) is detected by GCM
    /// authentication and reported as "Incorrect password." (same as wrong password,
    /// since GCM cannot distinguish between the two cases).
    /// </summary>
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

    /// <summary>
    /// "Fail closed" test: corrupt state file + encrypted artifacts → treated as encrypted.
    /// This prevents accidental writes to a drive whose encryption state is unclear.
    /// </summary>
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

    /// <summary>
    /// Missing state file + encrypted artifact present → treat as encrypted (fail closed).
    /// </summary>
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

    /// <summary>
    /// State explicitly disabled + no encrypted artifacts → not encrypted.
    /// This is the normal unencrypted drive state.
    /// </summary>
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

    /// <summary>
    /// Corrupt state file WITHOUT encrypted artifacts → still fail closed.
    /// This is the scenario that previously caused the Runner to show "Config not found"
    /// instead of prompting for unlock.
    /// </summary>
    [Fact]
    public void CorruptStateFile_WithoutEncryptedArtifacts_FailsClosed()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "{ this-is-not-valid-json");

            Assert.True(SsdEncryption.IsEffectivelyEncryptedForWriteGuard(root));
            Assert.False(SsdEncryption.IsEncryptionEnabled(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// Null-deserializing state file (e.g., file contains "null") without encrypted
    /// artifacts → fail closed.
    /// </summary>
    [Fact]
    public void NullStateFile_WithoutEncryptedArtifacts_FailsClosed()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            File.WriteAllText(StatePath(root), "null");

            Assert.True(SsdEncryption.IsEffectivelyEncryptedForWriteGuard(root));
            Assert.False(SsdEncryption.IsEncryptionEnabled(root));
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// State explicitly enabled → always treated as encrypted regardless of artifacts.
    /// </summary>
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

    /// <summary>Helper: creates an encrypted config from defaults for mutation tests.</summary>
    private static async Task SeedEncryptedConfigAsync(string root, string password)
    {
        SsdLayout.EnsureStructure(root);
        var configPath = Path.Combine(root, SsdLayout.Config, "portable-config.json");
        await new PortableConfig().SaveAsync(configPath);
        await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, password);
    }

    /// <summary>
    /// Helper: deserializes the encrypted config JSON, applies a mutation lambda,
    /// and writes it back. Used to simulate tampered or corrupt payloads.
    /// </summary>
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

    /// <summary>
    /// C27 Stage 3: round-trip a non-null HuggingFaceToken through the
    /// encrypted-config seal so a re-prep doesn't quietly drop the
    /// user's HF credential. Pins both the field's presence in the
    /// encrypted JSON and its value-preserving decode.
    /// </summary>
    [Fact]
    public async Task EnableConfigEncryption_PreservesHuggingFaceToken()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configPath = Path.Combine(root, "config", "portable-config.json");
            var config = new PortableConfig
            {
                OllamaPort = 12500,
                HuggingFaceToken = "hf_test_abc123",
                Models = new List<ModelConfigEntry>
                {
                    new() { Name = "llama3.2:3b", Status = ModelInstallStatus.Installed }
                }
            };
            await config.SaveAsync(configPath);
            await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, "test-password-c27");

            var unlocked = SsdEncryption.TryUnlockPortableConfig(
                root, "test-password-c27", out var decrypted, out var error);

            Assert.True(unlocked);
            Assert.NotNull(decrypted);
            Assert.True(string.IsNullOrWhiteSpace(error));
            Assert.Equal("hf_test_abc123", decrypted!.HuggingFaceToken);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    /// <summary>
    /// C27 Stage 3: HuggingFaceToken is optional — old drives sealed
    /// before Stage 3 lacked the field entirely. Pin that decoding
    /// such a payload yields a null token (no exception, no garbage
    /// default) so unlock stays backward compatible.
    /// </summary>
    [Fact]
    public async Task EnableConfigEncryption_NullHuggingFaceToken_RoundTripsAsNull()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var configPath = Path.Combine(root, "config", "portable-config.json");
            var config = new PortableConfig { OllamaPort = 12500 };
            await config.SaveAsync(configPath);
            await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, "p");

            var unlocked = SsdEncryption.TryUnlockPortableConfig(
                root, "p", out var decrypted, out _);

            Assert.True(unlocked);
            Assert.NotNull(decrypted);
            Assert.Null(decrypted!.HuggingFaceToken);
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
