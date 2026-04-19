using System.Security.Cryptography;

namespace FreeAiSsd.Shared.Services;

/// <summary>
/// Cached key derivation output held in memory while an encrypted SSD session
/// is unlocked. Produced by <see cref="SsdEncryption.TryUnlockPortableConfigWithMaterial"/>
/// and the in-memory finalize overload of <see cref="SsdEncryption.EnableConfigEncryptionAsync"/>,
/// so subsequent saves can re-encrypt without re-running PBKDF2.
///
/// <see cref="DerivedKey"/> is the raw 256-bit AES key. Callers must zero it via
/// <see cref="CryptographicOperations.ZeroMemory"/> when locking the session so
/// the key does not linger in managed memory.
/// </summary>
public sealed record UnlockMaterial(byte[] DerivedKey, byte[] Salt, int Iterations, string Scheme);
