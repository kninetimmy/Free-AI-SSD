namespace FreeAiSsd.Shared.Documents;

public static class DocumentChunker
{
    public const string Version = "1";
    public static List<string> ChunkText(string text, int chunkSize, int overlap)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        chunkSize = Math.Max(200, chunkSize);
        overlap = Math.Max(0, Math.Min(overlap, chunkSize / 2));

        var cleaned = text.Replace("\r\n", "\n").Trim();
        var chunks = new List<string>();
        var index = 0;
        while (index < cleaned.Length)
        {
            var remaining = cleaned.Length - index;
            var take = Math.Min(chunkSize, remaining);
            var end = index + take;
            if (end < cleaned.Length)
            {
                var boundary = cleaned.LastIndexOfAny(new[] { ' ', '\n', '\t', '.', ',', ';', ':' }, end - 1, take);
                if (boundary > index + 100)
                {
                    end = boundary + 1;
                }
            }

            var chunk = cleaned[index..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk);
            }

            if (end >= cleaned.Length)
            {
                break;
            }

            index = Math.Max(end - overlap, index + 1);
        }

        return chunks;
    }
}
