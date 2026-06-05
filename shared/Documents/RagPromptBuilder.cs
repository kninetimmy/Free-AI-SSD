namespace FreeAiSsd.Shared.Documents;

public static class RagPromptBuilder
{
    /// <summary>
    /// Builds the augmented prompt from retrieval results. Results are consumed in the
    /// order supplied — the retriever owns ranking (dense Search returns score-descending;
    /// hybrid SearchHybrid returns fused-rank order, with neighbor expansion interleaving
    /// context chunks adjacent to their hit), so this builder must not re-sort. Matched
    /// chunks (<see cref="RetrievalResult.IsNeighbor"/> false) claim the context budget
    /// first so an expansion neighbor can never displace its own hit; remaining budget is
    /// then filled with neighbors. Output preserves the supplied order so neighbors stay
    /// adjacent to their hit. When <paramref name="librarySearched"/> is true and no
    /// results are provided, a "No relevant documents found" note is included so the LLM
    /// knows the library was consulted but yielded nothing.
    /// </summary>
    public static RagPromptBuildResult Build(string userPrompt, IReadOnlyList<RetrievalResult> retrieval, int maxContextChars = 5000, bool librarySearched = false, bool inlineCitations = false)
    {
        if (retrieval.Count == 0)
        {
            if (librarySearched)
            {
                var noResultPrompt = "No relevant documents found in the library.\n\nUser question:\n" + userPrompt;
                return new RagPromptBuildResult { Prompt = noResultPrompt, UsedContext = false };
            }

            return new RagPromptBuildResult { Prompt = userPrompt, UsedContext = false };
        }

        // Render each chunk's block once, then decide inclusion under the budget.
        var entries = new List<(RetrievalResult Result, string Block)>(retrieval.Count);
        foreach (var result in retrieval)
        {
            var block = $"{CitationBuilder.Build(result.Chunk)}\n{result.Chunk.Text}\n\n";
            entries.Add((result, block));
        }

        var included = new HashSet<(string Path, int Index)>();
        var usedChars = 0;

        // Pass 1 — matched chunks are the priority. Stop at the first hit that doesn't fit;
        // the rest are lower-ranked. (For a hit-only list this matches the prior behavior.)
        foreach (var (result, block) in entries)
        {
            if (result.IsNeighbor) continue;
            if (usedChars + block.Length > maxContextChars) break;
            included.Add((result.Chunk.StoredRelativePath, result.Chunk.ChunkIndex));
            usedChars += block.Length;
        }

        // Pass 2 — fill the remaining budget with neighbors, in the supplied order.
        foreach (var (result, block) in entries)
        {
            if (!result.IsNeighbor) continue;
            if (usedChars + block.Length > maxContextChars) continue;
            included.Add((result.Chunk.StoredRelativePath, result.Chunk.ChunkIndex));
            usedChars += block.Length;
        }

        // Pass 3 — emit in the supplied order so neighbors stay adjacent to their hit.
        var sb = new StringBuilder();
        // Grounding enforcement (X22): require the model to answer only from the
        // supplied context and refuse with an exact phrase when the answer isn't present —
        // rather than the prior soft "say so if insufficient". Inline citation labels are
        // opt-in (default off) so answers stay concise and TTS doesn't read bracketed
        // file/section/page labels aloud; retrieved sources are surfaced separately by the
        // caller (Sources panel) regardless.
        sb.AppendLine("Answer the user's question using ONLY the reference context below.");
        sb.AppendLine("Be concise and direct — answer the question without preamble, without restating the question, and without summarizing the context you were given.");
        if (inlineCitations)
        {
            sb.AppendLine("After each factual claim, cite its source inline using the exact bracketed label shown with that context, e.g. [guide.pdf §Engine Start p.12].");
        }
        else
        {
            sb.AppendLine("Do not include source labels, file names, page numbers, or bracketed citations in your answer.");
        }
        sb.AppendLine("Use only that context — do not add outside knowledge or invent specifics such as numbers, names, or settings. If the context covers the question only partially, answer with what it does support and state what it does not specify. Reply exactly \"That information is not in the provided context.\" only when the context is unrelated to the question.");
        sb.AppendLine();
        sb.AppendLine("Reference context:");

        var emittedHits = new List<DocumentChunk>();
        foreach (var (result, block) in entries)
        {
            if (!included.Contains((result.Chunk.StoredRelativePath, result.Chunk.ChunkIndex)))
            {
                continue;
            }

            sb.Append(block);
            if (!result.IsNeighbor)
            {
                emittedHits.Add(result.Chunk);
            }
        }

        // No matched chunk fit — fall back to the bare prompt (neighbors are never shown alone).
        if (emittedHits.Count == 0)
        {
            return new RagPromptBuildResult { Prompt = userPrompt, UsedContext = false };
        }

        sb.AppendLine("User question:");
        sb.AppendLine(userPrompt);
        return new RagPromptBuildResult
        {
            Prompt = sb.ToString(),
            Sources = CitationBuilder.BuildDistinct(emittedHits),
            UsedContext = true
        };
    }
}
