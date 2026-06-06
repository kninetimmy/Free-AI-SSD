using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeAiSsd.Shared.Io;
using FreeAiSsd.Shared.Services;

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
    /// Upper bound on the PBKDF2 iteration count accepted from an on-disk encrypted
    /// blob. The count lives in attacker-editable portable-config.encrypted.json and is
    /// not covered by the AES-GCM tag, so it must be range-checked on decrypt:
    /// <see cref="Pbkdf2Iterations"/> is the floor (reject a downgrade) and this is the
    /// ceiling (reject an inflated value that would hang unlock as a denial of service).
    /// Generous enough to honour any future hardening bump while bounding the work.
    /// </summary>
    private const int Pbkdf2IterationsMax = 10_000_000;

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
    /// In-memory finalize overload: derives a fresh key from <paramref name="password"/>,
    /// encrypts <paramref name="config"/>, and writes the encrypted blob + state file
    /// in a single two-file atomic commit. No plaintext config file is ever touched on
    /// disk. Returns the resulting <see cref="UnlockMaterial"/> so callers can hand it
    /// straight to <see cref="IConfigStore.UnlockSession"/> without a second derive.
    /// </summary>
    public static async Task<UnlockMaterial> EnableConfigEncryptionAsync(
        string ssdRoot,
        PortableConfig config,
        string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Password is required.", nameof(password));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = DeriveKey(password, salt, Pbkdf2Iterations);
        var material = new UnlockMaterial(key, salt, Pbkdf2Iterations, SchemeName);

        await SaveEncryptedConfigAsync(ssdRoot, config, material, ct).ConfigureAwait(false);

        // Delete any pre-existing plaintext — mirrors the file-path overload's post-condition.
        SafeDelete(Path.Combine(ssdRoot, SsdLayout.Config, "portable-config.json"));

        return material;
    }

    /// <summary>
    /// Encrypts <paramref name="config"/> with the cached key in <paramref name="material"/>
    /// and commits the encrypted blob + state file atomically. Uses a fresh 12-byte nonce
    /// per call (AES-GCM nonce reuse would be catastrophic). On failure after the blob has
    /// been written, restores the prior blob from backup so blob/state never drift.
    /// </summary>
    public static async Task SaveEncryptedConfigAsync(
        string ssdRoot,
        PortableConfig config,
        UnlockMaterial material,
        CancellationToken ct = default)
    {
        if (material is null) throw new ArgumentNullException(nameof(material));
        if (material.DerivedKey is null || material.DerivedKey.Length != KeyBytes)
        {
            throw new ArgumentException("UnlockMaterial is missing a valid 256-bit key.", nameof(material));
        }

        var configDir = Path.Combine(ssdRoot, SsdLayout.Config);
        Directory.CreateDirectory(configDir);

        var encryptedPath = Path.Combine(configDir, EncryptedConfigFileName);
        var statePath = Path.Combine(configDir, StateFileName);
        var encryptedTmp = encryptedPath + ".tmp";
        var stateTmp = statePath + ".tmp";
        var encryptedBak = encryptedPath + ".bak";
        var stateBak = statePath + ".bak";

        // Serialize config to plaintext bytes (matches PortableConfig's JSON shape).
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(config, PortableConfigJsonOptions());

        // Encrypt with a fresh nonce.
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagBytes];
        using (var aes = new AesGcm(material.DerivedKey, TagBytes))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var encryptedConfig = new EncryptedConfig
        {
            Version = 1,
            Scheme = material.Scheme,
            Iterations = material.Iterations,
            Salt = Convert.ToBase64String(material.Salt),
            Nonce = Convert.ToBase64String(nonce),
            Tag = Convert.ToBase64String(tag),
            Ciphertext = Convert.ToBase64String(ciphertext),
            CreatedAtUtc = DateTime.UtcNow
        };

        var state = new EncryptionState
        {
            Enabled = true,
            Scheme = material.Scheme,
            Iterations = material.Iterations,
            EncryptedConfigFile = EncryptedConfigFileName,
            UpdatedAtUtc = DateTime.UtcNow
        };

        // Stage both tmp files before touching destinations.
        await File.WriteAllTextAsync(encryptedTmp, JsonSerializer.Serialize(encryptedConfig, JsonOptions()), ct)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(stateTmp, JsonSerializer.Serialize(state, JsonOptions()), ct)
            .ConfigureAwait(false);

        // Clean stale backups from a prior crashed save.
        SafeDelete(encryptedBak);
        SafeDelete(stateBak);

        var blobExisted = File.Exists(encryptedPath);
        var stateExisted = File.Exists(statePath);

        // Commit blob first. File.Replace is atomic on NTFS and writes a backup
        // we can use to roll back if the state rename fails.
        try
        {
            if (blobExisted)
            {
                FileOps.ReplaceWithRetry(encryptedTmp, encryptedPath, encryptedBak);
            }
            else
            {
                File.Move(encryptedTmp, encryptedPath);
            }
        }
        catch
        {
            SafeDelete(encryptedTmp);
            throw;
        }

        // Commit state. If this fails, restore the prior blob so encrypted+state
        // cannot diverge (the whole point of the two-file atomic commit).
        try
        {
            if (stateExisted)
            {
                FileOps.ReplaceWithRetry(stateTmp, statePath, stateBak);
            }
            else
            {
                File.Move(stateTmp, statePath);
            }
        }
        catch
        {
            // Roll back the blob rename. File.Replace is atomic on NTFS — a crash
            // mid-rollback cannot leave us with no blob + stale state.
            try
            {
                if (blobExisted && File.Exists(encryptedBak))
                {
                    FileOps.ReplaceWithRetry(encryptedBak, encryptedPath, null);
                }
                else if (!blobExisted)
                {
                    // First-time save: no prior blob to restore; just remove the half.
                    SafeDelete(encryptedPath);
                }
            }
            catch
            {
                // Best-effort rollback; surface the original state-rename failure.
            }

            SafeDelete(stateTmp);
            throw;
        }

        // Both replaces succeeded — clean up backup files.
        SafeDelete(encryptedBak);
        SafeDelete(stateBak);
    }

    /// <summary>
    /// Decrypts the portable config like <see cref="TryUnlockPortableConfig"/> and also
    /// returns the cached <see cref="UnlockMaterial"/> (derived key + salt + iters + scheme)
    /// so the unlocked session can re-encrypt subsequent saves without re-deriving.
    /// </summary>
    public static bool TryUnlockPortableConfigWithMaterial(
        string ssdRoot,
        string password,
        out PortableConfig? config,
        out UnlockMaterial? material,
        out string error)
    {
        config = null;
        material = null;
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

        // Fail closed on a tampered PBKDF2 iteration count. The value is read from
        // attacker-editable portable-config.encrypted.json and is NOT covered by the
        // AES-GCM tag, so it is never trusted blindly: below the hardened floor is a
        // downgrade attempt, above the ceiling is an unlock-time DoS. A genuine drive
        // always stores exactly Pbkdf2Iterations, so the only legitimate values lie in
        // [Pbkdf2Iterations, Pbkdf2IterationsMax].
        if (encrypted.Iterations < Pbkdf2Iterations || encrypted.Iterations > Pbkdf2IterationsMax)
        {
            error = "Encryption parameters are invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(encrypted.Salt))
        {
            error = "Encryption parameters are missing.";
            return false;
        }

        byte[]? key = null;
        try
        {
            var salt = Convert.FromBase64String(encrypted.Salt);
            var nonce = Convert.FromBase64String(encrypted.Nonce);
            var tag = Convert.FromBase64String(encrypted.Tag);
            var ciphertext = Convert.FromBase64String(encrypted.Ciphertext);
            key = DeriveKey(password, salt, encrypted.Iterations);

            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, TagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            var loaded = JsonSerializer.Deserialize<PortableConfig>(plaintext, JsonOptions());
            if (loaded is null)
            {
                CryptographicOperations.ZeroMemory(key);
                error = "Decrypted config is empty.";
                return false;
            }

            config = loaded;
            material = new UnlockMaterial(key, salt, encrypted.Iterations, encrypted.Scheme ?? SchemeName);
            return true;
        }
        catch (CryptographicException)
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            error = "Incorrect password.";
            return false;
        }
        catch (Exception)
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            error = "Failed to decrypt portable config.";
            return false;
        }
    }

    /// <summary>
    /// Checks for a stale plaintext config alongside an encrypted blob and migrates or
    /// removes it. Called immediately after a successful unlock so the drive never
    /// accumulates plaintext secrets from the pre-Stage-4 bug.
    ///
    /// Branch A (plaintext newer): loads plaintext, saves it as encrypted, deletes
    /// plaintext only after the encrypted save succeeds.
    /// Branch B (encrypted newer or equal): deletes the stale plaintext silently and logs.
    /// </summary>
    public static async Task<PlaintextMigrationResult> TryMigratePlaintextAsync(
        string ssdRoot,
        UnlockMaterial material,
        SsdLogger? logger = null,
        CancellationToken ct = default)
    {
        var configDir = Path.Combine(ssdRoot, SsdLayout.Config);
        var plaintextPath = Path.Combine(configDir, "portable-config.json");
        var encryptedPath = Path.Combine(configDir, EncryptedConfigFileName);

        if (!File.Exists(plaintextPath))
            return new PlaintextMigrationResult(false, null);

        var plaintextMtime = File.GetLastWriteTimeUtc(plaintextPath);
        var encryptedMtime = File.Exists(encryptedPath)
            ? File.GetLastWriteTimeUtc(encryptedPath)
            : DateTime.MinValue;

        if (plaintextMtime > encryptedMtime)
        {
            // Branch A: plaintext is newer — absorb into encrypted, then delete.
            try
            {
                var (plaintextConfig, isValid) = await PortableConfig.LoadWithValidationAsync(plaintextPath).ConfigureAwait(false);
                if (!isValid)
                {
                    logger?.Warn("[Migration] Plaintext config found but is corrupt — skipping migration, plaintext preserved.");
                    return new PlaintextMigrationResult(false, null);
                }
                await SaveEncryptedConfigAsync(ssdRoot, plaintextConfig, material, ct).ConfigureAwait(false);
                SafeDelete(plaintextPath);
                logger?.Info("[Migration] Plaintext config was newer — merged into encrypted blob, plaintext deleted.");
                return new PlaintextMigrationResult(true, plaintextConfig);
            }
            catch (Exception ex)
            {
                // Do not delete plaintext if the encrypted save failed — keep both intact.
                logger?.Warn($"[Migration] Failed to absorb plaintext into encrypted blob: {ex.Message}. Plaintext preserved.");
                return new PlaintextMigrationResult(false, null);
            }
        }
        else
        {
            // Branch B: encrypted is authoritative — discard stale plaintext.
            SafeDelete(plaintextPath);
            logger?.Info("[Migration] Stale plaintext removed — encrypted is authoritative.");
            return new PlaintextMigrationResult(false, null);
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort — caller paths are tmp/bak cleanup, not correctness-critical.
        }
    }

    /// <summary>
    /// JSON options matching <see cref="PortableConfig"/>'s own serializer so the
    /// symmetric encrypted-save round-trip produces byte-for-byte the same plaintext
    /// that <see cref="PortableConfig.SaveAsync"/> would have written.
    /// </summary>
    private static JsonSerializerOptions PortableConfigJsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

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

        // Validate essential encryption parameters. Fail closed on a tampered PBKDF2
        // iteration count: it is read from attacker-editable
        // portable-config.encrypted.json and is NOT covered by the AES-GCM tag, so a
        // value below the hardened floor is a downgrade attempt and one above the
        // ceiling is an unlock-time DoS. A genuine drive always stores exactly
        // Pbkdf2Iterations; only [Pbkdf2Iterations, Pbkdf2IterationsMax] is legitimate.
        if (encrypted.Iterations < Pbkdf2Iterations || encrypted.Iterations > Pbkdf2IterationsMax)
        {
            error = "Encryption parameters are invalid.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(encrypted.Salt))
        {
            error = "Encryption parameters are missing.";
            return false;
        }

        // Declared outside the try so the finally can zero it on every path. Unlike
        // the sibling ...WithMaterial (which hands the key to UnlockMaterial), this
        // variant retains the derived key nowhere, so it must not outlive the method.
        byte[]? key = null;
        try
        {
            // Decode Base64 fields and derive the decryption key.
            var salt = Convert.FromBase64String(encrypted.Salt);
            var nonce = Convert.FromBase64String(encrypted.Nonce);
            var tag = Convert.FromBase64String(encrypted.Tag);
            var ciphertext = Convert.FromBase64String(encrypted.Ciphertext);
            key = DeriveKey(password, salt, encrypted.Iterations);

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
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
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
