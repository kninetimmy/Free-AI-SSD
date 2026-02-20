namespace FreeAiSsd.Shared.Documents;

public static class RagPromptBuilder
{
    /// <summary>
    /// Builds the augmented prompt from retrieval results. When <paramref name="librarySearched"/>
    /// is true and no results are provided, a "No relevant documents found" note is included
    /// so the LLM knows the library was consulted but yielded nothing.
    /// </summary>
    public static RagPromptBuildResult Build(string userPrompt, IReadOnlyList<RetrievalResult> retrieval, int maxContextChars = 5000, bool librarySearched = false)
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

        var sb = new StringBuilder();
        sb.AppendLine("Use the following reference context when answering. If context is insufficient, say so.");
        sb.AppendLine();
        sb.AppendLine("Reference context:");

        var usedChunks = new List<DocumentChunk>();
        var currentChars = 0;
        foreach (var result in retrieval.OrderByDescending(r => r.Score))
        {
            var citation = CitationBuilder.Build(result.Chunk);
            var block = $"{citation}\n{result.Chunk.Text}\n\n";
            if (currentChars + block.Length > maxContextChars)
            {
                break;
            }

            sb.Append(block);
            currentChars += block.Length;
            usedChunks.Add(result.Chunk);
        }

        if (usedChunks.Count == 0)
        {
            return new RagPromptBuildResult { Prompt = userPrompt, UsedContext = false };
        }

        sb.AppendLine("User question:");
        sb.AppendLine(userPrompt);
        return new RagPromptBuildResult
        {
            Prompt = sb.ToString(),
            Sources = CitationBuilder.BuildDistinct(usedChunks),
            UsedContext = true
        };
    }
}
