using System.Text;

namespace FreeAiSsd.Shared.Documents;

public static class DocumentChunker
{
    public const string Version = "2";
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

    /// <summary>
    /// Section-aware chunking. Walks ordered <see cref="DocumentBlock"/>s maintaining a heading
    /// stack so each emitted span carries its section title and breadcrumb. Body text accumulates
    /// into the current section and is flushed (split via <see cref="ChunkText"/>) whenever a new
    /// heading or a page change is reached, so a chunk never crosses a heading or page boundary and
    /// its <c>(page, section)</c> attribution stays clean. The heading stack persists across pages,
    /// so a section spanning pages keeps its breadcrumb.
    /// <para>
    /// <c>CharOffsetStart</c>/<c>CharOffsetEnd</c> are offsets within the section's body text for the
    /// current page — monotonic and non-overlapping (modulo chunk overlap), which is what Stage 4's
    /// section-bounded neighbor expansion needs. They reset at each heading and page boundary.
    /// </para>
    /// </summary>
    public static List<DocumentChunkSpan> ChunkBlocks(IReadOnlyList<DocumentBlock> blocks, int chunkSize, int overlap)
    {
        var spans = new List<DocumentChunkSpan>();
        if (blocks.Count == 0) return spans;

        var headingStack = new List<DocumentBlock>();
        var section = string.Empty;
        var headingPath = string.Empty;

        var buffer = new StringBuilder();
        int? bufferPage = null;
        var sectionCursor = 0; // running offset base within the current (section, page) run

        void Flush()
        {
            var normalized = buffer.ToString().Replace("\r\n", "\n").Trim();
            buffer.Clear();
            if (normalized.Length == 0) return;

            var find = 0;
            foreach (var piece in ChunkText(normalized, chunkSize, overlap))
            {
                var idx = normalized.IndexOf(piece, find, StringComparison.Ordinal);
                if (idx < 0) idx = find;
                spans.Add(new DocumentChunkSpan
                {
                    Text = piece,
                    Page = bufferPage,
                    Section = section,
                    HeadingPath = headingPath,
                    CharOffsetStart = sectionCursor + idx,
                    CharOffsetEnd = sectionCursor + idx + piece.Length,
                    ContentType = "text",
                });
                // Advance past the overlap region so the next IndexOf lands near the true
                // next-chunk start even when the section text is highly repetitive.
                find = Math.Min(idx + Math.Max(1, piece.Length - overlap), normalized.Length);
            }

            sectionCursor += normalized.Length;
        }

        foreach (var block in blocks)
        {
            if (block.Kind == BlockKind.Heading)
            {
                Flush();
                // Pop to a shallower level, then push this heading; breadcrumb = stack titles.
                while (headingStack.Count > 0 && headingStack[^1].HeadingLevel >= block.HeadingLevel)
                {
                    headingStack.RemoveAt(headingStack.Count - 1);
                }
                headingStack.Add(block);
                section = block.Text;
                headingPath = string.Join(" > ", headingStack.Select(h => h.Text));
                bufferPage = null;
                sectionCursor = 0;
                continue;
            }

            // Body: a page change flushes first so a chunk never spans two pages, but the
            // heading stack (and thus section/breadcrumb) is preserved across the boundary.
            if (buffer.Length > 0 && bufferPage != block.Page)
            {
                Flush();
                bufferPage = null;
                sectionCursor = 0;
            }
            if (buffer.Length == 0)
            {
                bufferPage = block.Page;
            }
            else
            {
                buffer.Append('\n');
            }
            buffer.Append(block.Text);
        }

        Flush();
        return spans;
    }
}
