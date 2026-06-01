namespace FreeAiSsd.Shared.Documents;

public static class CitationBuilder
{
    public static string Build(DocumentChunk chunk)
    {
        // Section disambiguates chunks that share a file (and even a page) — e.g. an
        // "Engine Start" vs "Engine Shutdown" section on the same guide page. Empty
        // section renders exactly as before Stage 2, so older indexes are unaffected.
        var section = string.IsNullOrEmpty(chunk.Section) ? string.Empty : $" §{chunk.Section}";
        return chunk.Page.HasValue
            ? $"[{chunk.SourceFileName}{section} p.{chunk.Page.Value}]"
            : $"[{chunk.SourceFileName}{section}]";
    }

    public static List<string> BuildDistinct(IEnumerable<DocumentChunk> chunks)
        => chunks.Select(Build).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
