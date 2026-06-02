namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Reciprocal Rank Fusion: merges two independently-ranked result lists into a
/// single scale-free ranking. Each list contributes <c>1 / (k + rank)</c> to a
/// chunk's fused score (<c>rank</c> is the chunk's 0-based position in that
/// list); a chunk present in both lists sums both contributions. <c>k</c> dampens
/// the pull of low ranks — 60 is the value from Cormack et al.'s original RRF
/// paper and the de-facto default.
/// <para>
/// RRF is used here instead of weighting raw scores because the two arms produce
/// incomparable scales — cosine similarity (0..1, higher better) versus BM25
/// (unbounded, lower better). Fusing on rank sidesteps any normalization.
/// </para>
/// <para>
/// Pure and deterministic: no I/O, so it is unit-testable without a database or
/// Ollama. Chunks are identified across the two lists by
/// <c>(StoredRelativePath, ChunkIndex)</c>, which is unique within a library.
/// </para>
/// </summary>
public static class RrfFusion
{
    /// <summary>Default rank-fusion constant (Cormack et al. 2009).</summary>
    public const int DefaultK = 60;

    /// <summary>
    /// Fuses the dense and lexical result lists. The returned list is ordered by
    /// fused score descending; each result's <see cref="RetrievalResult.Score"/>
    /// carries that fused score (not the original cosine/BM25 value). When a chunk
    /// appears in both arms, the dense arm's <see cref="RetrievalResult.Chunk"/> is
    /// kept as the representative (it carries the cosine-derived metadata).
    /// Ties break deterministically on <c>(StoredRelativePath, ChunkIndex)</c>.
    /// </summary>
    public static List<RetrievalResult> Fuse(
        IReadOnlyList<RetrievalResult> dense,
        IReadOnlyList<RetrievalResult> lexical,
        int k = DefaultK)
    {
        var scores = new Dictionary<(string Path, int Index), double>();
        var representative = new Dictionary<(string Path, int Index), RetrievalResult>();

        void Accumulate(IReadOnlyList<RetrievalResult> list)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                var result = list[rank];
                var key = (result.Chunk.StoredRelativePath, result.Chunk.ChunkIndex);
                var contribution = 1.0 / (k + rank);
                scores[key] = scores.TryGetValue(key, out var existing) ? existing + contribution : contribution;
                // Dense is accumulated first, so its richer Chunk wins as representative.
                if (!representative.ContainsKey(key)) representative[key] = result;
            }
        }

        Accumulate(dense);
        Accumulate(lexical);

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.Path, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Index)
            .Select(kv => new RetrievalResult { Chunk = representative[kv.Key].Chunk, Score = kv.Value })
            .ToList();
    }
}
