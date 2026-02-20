using System.Numerics;

namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Converts float[] embeddings to and from compact byte[] representations
/// for efficient BLOB storage in SQLite. Each float occupies exactly 4 bytes
/// in little-endian format, saving ~60% compared to JSON text encoding.
/// Also provides L2-normalization helpers so embeddings can be pre-normalized
/// at storage time, enabling cheaper dot-product similarity at search time.
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

    /// <summary>
    /// L2-normalizes the embedding vector in place so its magnitude equals 1.
    /// After normalization, cosine similarity between two vectors reduces to a
    /// simple dot product, eliminating repeated magnitude calculations during search.
    /// Uses SIMD-accelerated <see cref="Vector{T}"/> for the magnitude computation.
    /// Zero-length or zero-magnitude vectors are left unchanged.
    /// </summary>
    public static void NormalizeInPlace(float[] embedding)
    {
        if (embedding.Length == 0) return;

        float sumSq = 0;
        int i = 0;
        int simdLength = Vector<float>.Count;

        // SIMD-accelerated magnitude computation.
        if (Vector.IsHardwareAccelerated && embedding.Length >= simdLength)
        {
            var sumVec = Vector<float>.Zero;
            int limit = embedding.Length - (embedding.Length % simdLength);
            for (; i < limit; i += simdLength)
            {
                var v = new Vector<float>(embedding, i);
                sumVec += v * v;
            }
            sumSq = Vector.Sum(sumVec);
        }

        // Scalar tail.
        for (; i < embedding.Length; i++)
        {
            sumSq += embedding[i] * embedding[i];
        }

        if (sumSq <= float.Epsilon) return;

        float invMag = 1f / MathF.Sqrt(sumSq);
        i = 0;

        // SIMD-accelerated scaling.
        if (Vector.IsHardwareAccelerated && embedding.Length >= simdLength)
        {
            var scaleVec = new Vector<float>(invMag);
            int limit = embedding.Length - (embedding.Length % simdLength);
            for (; i < limit; i += simdLength)
            {
                var v = new Vector<float>(embedding, i);
                (v * scaleVec).CopyTo(embedding, i);
            }
        }

        // Scalar tail.
        for (; i < embedding.Length; i++)
        {
            embedding[i] *= invMag;
        }
    }

    /// <summary>
    /// Returns a new L2-normalized copy of the embedding without modifying the original.
    /// </summary>
    public static float[] Normalize(float[] embedding)
    {
        var copy = new float[embedding.Length];
        Array.Copy(embedding, copy, embedding.Length);
        NormalizeInPlace(copy);
        return copy;
    }
}
