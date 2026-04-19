namespace FreeAiSsd.Shared.Services;

/// <summary>
/// Single chokepoint for persisting the portable config to disk, whether the
/// SSD is encrypted or not. Holds the current <see cref="UnlockMaterial"/> while
/// a session is unlocked so saves re-encrypt symmetrically without prompting
/// for the password again, and serializes all saves via an internal semaphore
/// to prevent concurrent <c>File.Replace</c> collisions.
///
/// Contract is frozen by the X9 Stage 1 plan (PR #142). Stages 3/4 wire Runner
/// and Prep callers to this interface.
/// </summary>
public interface IConfigStore
{
    /// <summary>
    /// Loads the portable config from the SSD. For encrypted drives the caller
    /// is expected to have already unlocked the session (otherwise returns null
    /// for encrypted drives; plaintext drives load directly).
    /// </summary>
    Task<PortableConfig?> LoadAsync(string ssdRoot, CancellationToken ct);

    /// <summary>
    /// Persists <paramref name="config"/> to the SSD, re-encrypting when the
    /// session is unlocked and the drive is encrypted, or writing plaintext
    /// when it is not. Fails closed when Network Mode + Require API Key are on
    /// but the drive is unencrypted.
    /// </summary>
    Task SaveAsync(string ssdRoot, PortableConfig config, CancellationToken ct);

    /// <summary>
    /// Caches the derived key so subsequent <see cref="SaveAsync"/> calls can
    /// encrypt without re-running PBKDF2. Takes ownership of the key bytes
    /// inside <paramref name="material"/>.
    /// </summary>
    void UnlockSession(UnlockMaterial material);

    /// <summary>
    /// Waits for any in-flight save to drain, bounded by <paramref name="timeout"/>.
    /// Returns without throwing on timeout (shutdown path); callers are expected
    /// to call <see cref="LockSession"/> immediately after.
    /// </summary>
    Task FlushAsync(TimeSpan timeout);

    /// <summary>
    /// Zeroes the cached key with <see cref="System.Security.Cryptography.CryptographicOperations.ZeroMemory"/>
    /// and drops the <see cref="UnlockMaterial"/>. Safe to call when no session
    /// is unlocked.
    /// </summary>
    void LockSession();

    /// <summary>True between <see cref="UnlockSession"/> and <see cref="LockSession"/>.</summary>
    bool IsSessionUnlocked { get; }
}
