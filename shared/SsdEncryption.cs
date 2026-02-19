using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.Shared;

/// <summary>
/// Provides password-based encryption for the portable SSD configuration using
/// AES-256-GCM authenticated encryption with PBKDF2-SHA256 key derivation.
///
/// Encryption flow:
/// 1. User provides a password during SSD finalization in PrepApp.
/// 2. A random 16-byte salt and 12-byte nonce are generated.
/// 3. PBKDF2-SHA256 derives a 256-bit key from the password + salt (210,000 iterations).
/// 4. AES-256-GCM encrypts the plaintext config, producing ciphertext + 16-byte authentication tag.
/// 5. The encrypted payload (salt, nonce, tag, ciphertext) is written to portable-config.encrypted.json.
/// 6. An encryption-state.json metadata file records the encryption scheme and parameters.
/// 7. The plaintext config file is deleted.
///
/// Decryption flow (in Runner):
/// 1. User enters their password in the unlock dialog.
/// 2. PBKDF2 re-derives the key using the stored salt and iteration count.
/// 3. AES-256-GCM decrypts and authenticates the ciphertext.
/// 4. Authentication failure (wrong password or tampered data) throws CryptographicException → "Incorrect password."
///
/// Security properties:
/// - AES-256-GCM provides both confidentiality and integrity (AEAD).
/// - 210,000 PBKDF2 iterations resist offline brute-force attacks per OWASP 2023 guidance.
/// - Random salt prevents rainbow table attacks.
/// - GCM tag detects any tampering with the encrypted payload.
/// </summary>
public static class SsdEncryption
{
    /// <summary>Human-readable encryption scheme identifier for metadata files.</summary>
    public const string SchemeName = "aes-256-gcm+pbkdf2-sha256-v1";
    /// <summary>Filename for the encryption state metadata.</summary>
    public const string StateFileName = "encryption-state.json";
    /// <summary>Filename for the encrypted portable config payload.</summary>
    public const string EncryptedConfigFileName = "portable-config.encrypted.json";

    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    /// <summary>PBKDF2 iteration count per OWASP 2023 recommendation for SHA-256.</summary>
    private const int Pbkdf2Iterations = 210_000;

    /// <summary>
    /// Checks whether encryption is explicitly enabled in the state file.
    /// Returns false if the state file is missing or unreadable.
    /// </summary>
    public static bool IsEncryptionEnabled(string ssdRoot)
    {
        var statePath = Path.Combine(ssdRoot, SsdLayout.Config, StateFileName);
        if (!File.Exists(statePath))
        {
            return false;
        }

        try
        {
            var state = JsonSerializer.Deserialize<EncryptionState>(File.ReadAllText(statePath), JsonOptions());
            return state?.Enabled == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether the drive should be treated as encrypted for write-guard purposes.
    /// Uses a "fail closed" security model: if the state file is missing, corrupt, or
    /// inconsistent with actual encrypted artifacts on disk, the drive is treated as encrypted
    /// to prevent accidental corruption. Only returns false when the state explicitly says
    /// disabled AND no encrypted artifacts exist on disk.
    /// </summary>
    public static bool IsEffectivelyEncryptedForWriteGuard(string ssdRoot)
    {
        var configDir = Path.Combine(ssdRoot, SsdLayout.Config);
        var statePath = Path.Combine(configDir, StateFileName);
        var encryptedPath = Path.Combine(configDir, EncryptedConfigFileName);
        var hasEncryptedArtifact = File.Exists(encryptedPath);

        // No state file: encrypted artifacts alone trigger protection.
        if (!File.Exists(statePath))
        {
            return hasEncryptedArtifact;
        }

        EncryptionState? state;
        try
        {
            state = JsonSerializer.Deserialize<EncryptionState>(File.ReadAllText(statePath), JsonOptions());
        }
        catch
        {
            // Unreadable state file → fail closed (treat as encrypted).
            return true;
        }

        if (state is null)
        {
            return true;
        }

        if (state.Enabled)
        {
            return true;
        }

        // State says disabled, but encrypted artifacts still exist → inconsistent state → fail closed.
        if (hasEncryptedArtifact)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Encrypts the portable config file with the user's password and replaces
    /// the plaintext file with an encrypted version. Also writes the encryption
    /// state metadata file. The plaintext config is deleted after encryption.
    /// </summary>
    /// <param name="ssdRoot">Root path of the portable SSD.</param>
    /// <param name="plainConfigPath">Path to the plaintext portable-config.json file.</param>
    /// <param name="password">User-provided encryption password.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task EnableConfigEncryptionAsync(string ssdRoot, string plainConfigPath, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        if (!File.Exists(plainConfigPath))
        {
            throw new FileNotFoundException("Portable config file was not found for encryption.", plainConfigPath);
        }

        // Read the plaintext config bytes.
        var plaintext = await File.ReadAllBytesAsync(plainConfigPath, ct);

        // Generate cryptographically random salt and nonce.
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);

        // Derive the encryption key from the password using PBKDF2-SHA256.
        var key = DeriveKey(password, salt, Pbkdf2Iterations);

        // Encrypt with AES-256-GCM (authenticated encryption).
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // Build the encrypted config payload with all parameters needed for decryption.
        var encryptedConfig = new EncryptedConfig
        {
            Version = 1,
            Scheme = SchemeName,
            Iterations = Pbkdf2Iterations,
            Salt = Convert.ToBase64String(salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Ciphertext = Convert.ToBase64String(ciphertext),
            CreatedAtUtc = DateTime.UtcNow
        };

        var configDir = Path.GetDirectoryName(plainConfigPath) ?? Path.Combine(ssdRoot, SsdLayout.Config);
        Directory.CreateDirectory(configDir);

        // Write the encrypted payload.
        var encryptedPath = Path.Combine(configDir, EncryptedConfigFileName);
        var encryptedJson = JsonSerializer.Serialize(encryptedConfig, JsonOptions());
        await File.WriteAllTextAsync(encryptedPath, encryptedJson, ct);

        // Write the encryption state metadata.
        var state = new EncryptionState
        {
            Enabled = true,
            Scheme = SchemeName,
            Iterations = Pbkdf2Iterations,
            EncryptedConfigFile = EncryptedConfigFileName,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var statePath = Path.Combine(configDir, StateFileName);
        await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(state, JsonOptions()), ct);

        // Delete the plaintext config now that encryption is complete.
        File.Delete(plainConfigPath);
    }

    /// <summary>
    /// Attempts to decrypt the portable config using the provided password.
    /// Returns false with a descriptive error for each failure mode:
    /// - Missing metadata files
    /// - Unreadable/corrupt metadata
    /// - Invalid encryption parameters
    /// - Wrong password (GCM authentication failure)
    /// - Malformed Base64 fields
    /// </summary>
    /// <param name="ssdRoot">Root path of the portable SSD.</param>
    /// <param name="password">User-provided decryption password.</param>
    /// <param name="config">Decrypted PortableConfig on success; null on failure.</param>
    /// <param name="error">Descriptive error message on failure; empty on success.</param>
    /// <returns>True if decryption succeeded; false otherwise.</returns>
    public static bool TryUnlockPortableConfig(string ssdRoot, string password, out PortableConfig? config, out string error)
    {
        config = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Password is required.";
            return false;
        }

        // Verify both metadata files exist.
        var statePath = Path.Combine(ssdRoot, SsdLayout.Config, StateFileName);
        var encryptedPath = Path.Combine(ssdRoot, SsdLayout.Config, EncryptedConfigFileName);
        if (!File.Exists(statePath) || !File.Exists(encryptedPath))
        {
            error = "Encrypted drive metadata is missing.";
            return false;
        }

        // Parse the state and encrypted config JSON files.
        EncryptionState? state;
        EncryptedConfig? encrypted;
        try
        {
            state = JsonSerializer.Deserialize<EncryptionState>(File.ReadAllText(statePath), JsonOptions());
            encrypted = JsonSerializer.Deserialize<EncryptedConfig>(File.ReadAllText(encryptedPath), JsonOptions());
        }
        catch
        {
            error = "Encrypted drive metadata is unreadable.";
            return false;
        }

        if (state?.Enabled != true || encrypted is null)
        {
            error = "Encrypted drive metadata is invalid.";
            return false;
        }

        // Validate essential encryption parameters are present.
        if (encrypted.Iterations <= 0 || string.IsNullOrWhiteSpace(encrypted.Salt))
        {
            error = "Encryption parameters are missing.";
            return false;
        }

        try
        {
            // Decode Base64 fields and derive the decryption key.
            var salt = Convert.FromBase64String(encrypted.Salt);
            var nonce = Convert.FromBase64String(encrypted.Nonce);
            var tag = Convert.FromBase64String(encrypted.Tag);
            var ciphertext = Convert.FromBase64String(encrypted.Ciphertext);
            var key = DeriveKey(password, salt, encrypted.Iterations);

            // Decrypt with AES-256-GCM. Authentication failure (wrong password
            // or tampered data) throws CryptographicException.
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            // Deserialize the decrypted JSON into a PortableConfig.
            var loaded = JsonSerializer.Deserialize<PortableConfig>(plaintext, JsonOptions());
            if (loaded is null)
            {
                error = "Decrypted config is empty.";
                return false;
            }

            config = loaded;
            return true;
        }
        catch (CryptographicException)
        {
            // GCM authentication failure = wrong password or tampered ciphertext.
            error = "Incorrect password.";
            return false;
        }
        catch (Exception)
        {
            // Other failures (e.g., malformed Base64) = generic decryption error.
            error = "Failed to decrypt portable config.";
            return false;
        }
    }

    /// <summary>
    /// Derives a 256-bit encryption key from a password and salt using PBKDF2-SHA256.
    /// </summary>
    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Internal state tracking whether encryption is enabled on the SSD.
    /// Stored in encryption-state.json in the config directory.
    /// </summary>
    private sealed class EncryptionState
    {
        public bool Enabled { get; set; }
        public string Scheme { get; set; } = SchemeName;
        public int Iterations { get; set; }
        public string EncryptedConfigFile { get; set; } = EncryptedConfigFileName;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// The encrypted config payload containing all parameters needed for decryption:
    /// salt, nonce, authentication tag, ciphertext, and the iteration count used
    /// for key derivation. Stored as Base64-encoded fields in JSON.
    /// </summary>
    private sealed class EncryptedConfig
    {
        public int Version { get; set; }
        public string Scheme { get; set; } = SchemeName;
        public int Iterations { get; set; }
        public string Salt { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
