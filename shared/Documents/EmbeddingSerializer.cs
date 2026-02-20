namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Converts float[] embeddings to and from compact byte[] representations
/// for efficient BLOB storage in SQLite. Each float occupies exactly 4 bytes
/// in little-endian format, saving ~60% compared to JSON text encoding.
/// </summary>
public static class EmbeddingSerializer
{
    /// <summary>
    /// Converts a float[] embedding into a byte[] suitable for SQLite BLOB storage.
    /// Each float is stored as 4 little-endian bytes via <see cref="Buffer.BlockCopy"/>.
    /// </summary>
    /// <param name="embedding">The embedding vector to serialize.</param>
    /// <returns>A byte array of length <c>embedding.Length * 4</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="embedding"/> is null.</exception>
    public static byte[] ToBlob(float[] embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        var blob = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, 0, blob, 0, blob.Length);
        return blob;
    }

    /// <summary>
    /// Converts a byte[] BLOB back into a float[] embedding.
    /// </summary>
    /// <param name="blob">The raw bytes previously produced by <see cref="ToBlob"/>.</param>
    /// <returns>The reconstructed embedding vector.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="blob"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="blob"/> length is not divisible by 4 (the size of a single-precision float).
    /// </exception>
    public static float[] FromBlob(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length % sizeof(float) != 0)
        {
            throw new ArgumentException(
                $"Blob length {blob.Length} is not divisible by {sizeof(float)}. Data may be corrupted.",
                nameof(blob));
        }

        var floats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        return floats;
    }
}
