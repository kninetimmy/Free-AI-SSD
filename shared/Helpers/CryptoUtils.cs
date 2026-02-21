using System.Security.Cryptography;

namespace FreeAiSsd.Shared.Helpers;

/// <summary>
/// Shared SHA-256 file-hashing utilities used across the solution for
/// download verification, document deduplication, and supply-chain integrity checks.
/// </summary>
public static class CryptoUtils
{
    /// <summary>
    /// Computes the SHA-256 hash of a file and returns it as a lowercase hex string.
    /// Uses a buffered stream for memory efficiency on large files.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the file to hash.</param>
    /// <returns>Lowercase hexadecimal SHA-256 digest (64 characters).</returns>
    /// <exception cref="FileNotFoundException">Thrown when <paramref name="filePath"/> does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller lacks read access to the file.</exception>
    public static string ComputeSha256Hex(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Asynchronously computes the SHA-256 hash of a file and returns it as a lowercase hex string.
    /// Uses a buffered stream for memory efficiency on large files.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the file to hash.</param>
    /// <param name="ct">Cancellation token to abort the operation.</param>
    /// <returns>Lowercase hexadecimal SHA-256 digest (64 characters).</returns>
    /// <exception cref="FileNotFoundException">Thrown when <paramref name="filePath"/> does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when the caller lacks read access to the file.</exception>
    public static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
