namespace FreeAiSsd.Shared.Documents;

public static class CitationBuilder
{
    public static string Build(DocumentChunk chunk)
    {
        return chunk.Page.HasValue
            ? $"[{chunk.SourceFileName} p.{chunk.Page.Value}]"
            : $"[{chunk.SourceFileName}]";
    }

    public static List<string> BuildDistinct(IEnumerable<DocumentChunk> chunks)
        => chunks.Select(Build).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
