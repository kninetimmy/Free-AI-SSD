using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.Shared;

public static class SsdEncryption
{
    public const string SchemeName = "aes-256-gcm+pbkdf2-sha256-v1";
    public const string StateFileName = "encryption-state.json";
    public const string EncryptedConfigFileName = "portable-config.encrypted.json";

    private const int SaltBytes = 16;
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int Pbkdf2Iterations = 210_000;

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

        var plaintext = await File.ReadAllBytesAsync(plainConfigPath, ct);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var key = DeriveKey(password, salt, Pbkdf2Iterations);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(key, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

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

        var encryptedPath = Path.Combine(configDir, EncryptedConfigFileName);
        var encryptedJson = JsonSerializer.Serialize(encryptedConfig, JsonOptions());
        await File.WriteAllTextAsync(encryptedPath, encryptedJson, ct);

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

        File.Delete(plainConfigPath);
    }

    public static bool TryUnlockPortableConfig(string ssdRoot, string password, out PortableConfig? config, out string error)
    {
        config = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(password))
        {
            error = "Password is required.";
            return false;
        }

        var statePath = Path.Combine(ssdRoot, SsdLayout.Config, StateFileName);
        var encryptedPath = Path.Combine(ssdRoot, SsdLayout.Config, EncryptedConfigFileName);
        if (!File.Exists(statePath) || !File.Exists(encryptedPath))
        {
            error = "Encrypted drive metadata is missing.";
            return false;
        }

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

        if (encrypted.Iterations <= 0 || string.IsNullOrWhiteSpace(encrypted.Salt))
        {
            error = "Encryption parameters are missing.";
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(encrypted.Salt);
            var nonce = Convert.FromBase64String(encrypted.Nonce);
            var tag = Convert.FromBase64String(encrypted.Tag);
            var ciphertext = Convert.FromBase64String(encrypted.Ciphertext);
            var key = DeriveKey(password, salt, encrypted.Iterations);

            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

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
            error = "Incorrect password.";
            return false;
        }
        catch (Exception)
        {
            error = "Failed to decrypt portable config.";
            return false;
        }
    }

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

    private sealed class EncryptionState
    {
        public bool Enabled { get; set; }
        public string Scheme { get; set; } = SchemeName;
        public int Iterations { get; set; }
        public string EncryptedConfigFile { get; set; } = EncryptedConfigFileName;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

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
