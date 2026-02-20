namespace FreeAiSsd.Shared.Documents;

public static class RagPromptBuilder
{
    public static RagPromptBuildResult Build(string userPrompt, IReadOnlyList<RetrievalResult> retrieval, int maxContextChars = 5000)
    {
        if (retrieval.Count == 0)
        {
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
