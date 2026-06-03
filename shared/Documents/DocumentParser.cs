using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace FreeAiSsd.Shared.Documents;

public static class DocumentParser
{
    // v2: per-page heading-density backoff so graphically dense pages stop fragmenting (#68).
    public const string Version = "2";

    /// <summary>A heading tier must be at least this many times the body font size.</summary>
    private const double HeadingSizeFactor = 1.15;

    /// <summary>A bold, body-or-larger line longer than this is treated as body, not a heading.</summary>
    private const int BoldHeadingMaxChars = 80;

    /// <summary>A page must carry at least this many lines to be eligible for heading-density backoff.</summary>
    private const int HeadingDensityMinLines = 6;

    /// <summary>
    /// When more than this fraction of an eligible page's lines classify as headings, the
    /// typographic signal on that page is treated as noise (a graphically dense diagram page,
    /// e.g. a cockpit layout whose every callout is a short bold label) and heading detection
    /// is suppressed for the whole page. Without this, such a page fragments into one chunk per
    /// label — the F18-guide ingest explosion (#68).
    /// </summary>
    private const double HeadingDensityMaxFraction = 0.40;
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".json", ".csv"
    };

    /// <summary>PDF magic bytes: %PDF</summary>
    private static readonly byte[] PdfMagic = { 0x25, 0x50, 0x44, 0x46 };

    public static bool IsSupported(string path) => Supported.Contains(Path.GetExtension(path));

    /// <summary>
    /// Validates that the file extension is in the supported set and that the file's
    /// leading bytes are consistent with its claimed format. Throws if validation fails.
    /// </summary>
    public static void ValidateBeforeParse(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (!Supported.Contains(ext))
        {
            throw new InvalidOperationException(
                $"Unsupported file extension '{ext}' for document: {Path.GetFileName(filePath)}");
        }

        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ValidatePdfMagicBytes(filePath);
        }
        else
        {
            // For text-based formats, verify the file does not contain a binary header
            // that would indicate the extension is spoofed.
            ValidateTextFile(filePath);
        }
    }

    public static ParsedDocument Parse(string filePath)
    {
        ValidateBeforeParse(filePath);

        var ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePdf(filePath);
        }

        var text = File.ReadAllText(filePath);
        var doc = new ParsedDocument
        {
            Segments = new List<ParsedSegment> { new() { Text = text } },
        };

        // Markdown carries explicit structure; everything else is one structureless body block.
        doc.Blocks = string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase)
            ? ParseMarkdownBlocks(text)
            : new List<DocumentBlock> { new() { Kind = BlockKind.Body, Text = text } };

        return doc;
    }

    private static void ValidatePdfMagicBytes(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var header = new byte[PdfMagic.Length];
        var bytesRead = stream.Read(header, 0, header.Length);
        if (bytesRead < PdfMagic.Length || !header.AsSpan().SequenceEqual(PdfMagic))
        {
            throw new InvalidOperationException(
                $"File does not have valid PDF magic bytes: {Path.GetFileName(filePath)}");
        }
    }

    private static void ValidateTextFile(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var header = new byte[(int)Math.Min(4, stream.Length)];
        var bytesRead = stream.Read(header, 0, header.Length);
        if (bytesRead == 0)
        {
            return; // Empty files are allowed for text formats.
        }

        // Reject files whose header matches a known binary format (PDF, ZIP/OOXML, ELF, PE).
        ReadOnlySpan<byte> span = header.AsSpan(0, bytesRead);
        if (span.Length >= 4 && span[..4].SequenceEqual(PdfMagic))
        {
            throw new InvalidOperationException(
                $"File has PDF magic bytes but a text extension: {Path.GetFileName(filePath)}");
        }

        ReadOnlySpan<byte> zipMagic = stackalloc byte[] { 0x50, 0x4B, 0x03, 0x04 };
        if (span.Length >= 4 && span[..4].SequenceEqual(zipMagic))
        {
            throw new InvalidOperationException(
                $"File has ZIP/OOXML magic bytes but a text extension: {Path.GetFileName(filePath)}");
        }

        ReadOnlySpan<byte> elfMagic = stackalloc byte[] { 0x7F, 0x45, 0x4C, 0x46 };
        if (span.Length >= 4 && span[..4].SequenceEqual(elfMagic))
        {
            throw new InvalidOperationException(
                $"File has ELF magic bytes but a text extension: {Path.GetFileName(filePath)}");
        }

        // PE executable: MZ header
        if (span.Length >= 2 && span[0] == 0x4D && span[1] == 0x5A)
        {
            throw new InvalidOperationException(
                $"File has PE/MZ magic bytes but a text extension: {Path.GetFileName(filePath)}");
        }
    }

    private static ParsedDocument ParsePdf(string filePath)
    {
        var doc = new ParsedDocument();
        var allLines = new List<LineRecord>();

        using (var pdf = PdfDocument.Open(filePath))
        {
            foreach (var page in pdf.GetPages())
            {
                // Keep the flat per-page text for back-compat consumers (e.g. _FixtureDumper).
                doc.Segments.Add(new ParsedSegment { Page = page.Number, Text = page.Text });
                allLines.AddRange(ExtractPdfLines(page));
            }
        }

        // Font tiers are computed across the whole document so heading levels stay
        // consistent over hundreds of pages.
        doc.Blocks = ClassifyLinesIntoBlocks(allLines);

        // Fallback: image-only / no-text-layer PDFs yield no lines (and uniform-font pages
        // yield only body). If nothing structural came out, degrade to page-as-body so
        // chunking behaves exactly as it did before section awareness.
        if (doc.Blocks.Count == 0)
        {
            foreach (var seg in doc.Segments)
            {
                doc.Blocks.Add(new DocumentBlock { Page = seg.Page, Kind = BlockKind.Body, Text = seg.Text });
            }
        }

        return doc;
    }

    /// <summary>
    /// Parses markdown into ordered heading/body blocks using ATX (<c>#</c>) headings.
    /// Setext (underline) headings and indented code fences are intentionally not treated
    /// as headings — the corpus authors ATX exclusively.
    /// </summary>
    internal static List<DocumentBlock> ParseMarkdownBlocks(string text)
    {
        var blocks = new List<DocumentBlock>();
        var body = new StringBuilder();

        void FlushBody()
        {
            var t = body.ToString();
            body.Clear();
            if (!string.IsNullOrWhiteSpace(t))
            {
                blocks.Add(new DocumentBlock { Kind = BlockKind.Body, Text = t.Trim() });
            }
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var heading = TryParseAtxHeading(line);
            if (heading is not null)
            {
                FlushBody();
                blocks.Add(heading);
            }
            else
            {
                if (body.Length > 0) body.Append('\n');
                body.Append(line);
            }
        }

        FlushBody();
        return blocks;
    }

    private static DocumentBlock? TryParseAtxHeading(string line)
    {
        var i = 0;
        // CommonMark permits up to three leading spaces before an ATX heading.
        while (i < line.Length && i < 3 && line[i] == ' ') i++;

        var hashes = 0;
        while (i < line.Length && line[i] == '#') { hashes++; i++; }
        if (hashes is < 1 or > 6) return null;

        // The hash run must be followed by a space (or end of line) to count as a heading.
        if (i < line.Length && line[i] != ' ') return null;

        var title = line[i..].Trim().TrimEnd('#').Trim();
        if (title.Length == 0) return null;

        return new DocumentBlock { Kind = BlockKind.Heading, HeadingLevel = hashes, Text = title };
    }

    /// <summary>
    /// Groups a PDF page's words into visual lines (by baseline) and records each line's
    /// char-weighted representative font size and bold-majority flag. The impure half of
    /// PDF structure detection; the pure classification lives in <see cref="ClassifyLinesIntoBlocks"/>.
    /// </summary>
    private static List<LineRecord> ExtractPdfLines(Page page)
    {
        var lines = new List<LineRecord>();
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Letters.Count > 0)
            .ToList();
        if (words.Count == 0) return lines;

        // PDF Y grows upward, so a higher baseline Y is nearer the top of the page.
        var byBaseline = new Dictionary<int, List<Word>>();
        foreach (var w in words)
        {
            var key = (int)Math.Round(w.Letters[0].StartBaseLine.Y);
            if (!byBaseline.TryGetValue(key, out var bucket))
            {
                bucket = new List<Word>();
                byBaseline[key] = bucket;
            }
            bucket.Add(w);
        }

        foreach (var group in byBaseline.OrderByDescending(kv => kv.Key))
        {
            var ordered = group.Value.OrderBy(w => w.BoundingBox.Left).ToList();
            var text = string.Join(' ', ordered.Select(w => w.Text)).Trim();
            if (text.Length == 0) continue;

            var sizeWeights = new Dictionary<double, int>();
            var boldChars = 0;
            var totalChars = 0;
            foreach (var letter in ordered.SelectMany(w => w.Letters))
            {
                var size = Math.Round(letter.PointSize, 1);
                sizeWeights.TryGetValue(size, out var c);
                sizeWeights[size] = c + 1;
                totalChars++;
                if (IsBoldFont(letter.FontName)) boldChars++;
            }

            var repSize = sizeWeights
                .OrderByDescending(kv => kv.Value)
                .ThenByDescending(kv => kv.Key)
                .First().Key;

            lines.Add(new LineRecord
            {
                Page = page.Number,
                Text = text,
                FontSize = repSize,
                IsBold = totalChars > 0 && boldChars * 2 >= totalChars,
            });
        }

        return lines;
    }

    /// <summary>
    /// Pure classification of extracted lines into heading/body blocks using document-wide
    /// font tiers. A single char-weighted histogram across all lines establishes the body
    /// size (the mode); distinct sizes at least <see cref="HeadingSizeFactor"/>× the body size
    /// form heading tiers, ranked largest-first into levels 1..N. Bold, short, body-or-larger
    /// lines that aren't in a size tier are treated as the deepest heading level. Body lines
    /// coalesce into one block but never across a page boundary.
    /// <para>
    /// A per-page backoff guards against graphically dense pages (cockpit diagrams etc.) where
    /// nearly every short label trips a heading heuristic: when an eligible page classifies more
    /// than <see cref="HeadingDensityMaxFraction"/> of its lines as headings, the page's
    /// typographic signal is treated as noise and all its lines degrade to body (#68).
    /// </para>
    /// </summary>
    public static List<DocumentBlock> ClassifyLinesIntoBlocks(IReadOnlyList<LineRecord> lines)
    {
        var blocks = new List<DocumentBlock>();
        if (lines.Count == 0) return blocks;

        // 1. Char-weighted font-size histogram across the whole document.
        var sizeWeights = new Dictionary<double, int>();
        foreach (var line in lines)
        {
            var size = Math.Round(line.FontSize, 1);
            sizeWeights.TryGetValue(size, out var w);
            sizeWeights[size] = w + line.Text.Length;
        }

        // Body size = the size carrying the most characters (ties → smaller size).
        var bodySize = sizeWeights.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;

        // 2. Heading tiers: sizes meaningfully larger than body, largest → level 1.
        var headingSizes = sizeWeights.Keys
            .Where(s => s >= bodySize * HeadingSizeFactor)
            .OrderByDescending(s => s)
            .ToList();
        var levelBySize = new Dictionary<double, int>();
        for (var i = 0; i < headingSizes.Count; i++)
        {
            levelBySize[headingSizes[i]] = i + 1;
        }

        // Would this line be classified as a heading by the size-tier or bold-caption rule?
        bool IsHeadingLine(LineRecord line, out int level)
        {
            var size = Math.Round(line.FontSize, 1);
            if (levelBySize.TryGetValue(size, out level)) return true;
            if (line.IsBold && size >= bodySize && line.Text.Length <= BoldHeadingMaxChars)
            {
                // A bold caption with no distinct size still reads as a section break; place it
                // one level below the smallest size tier (level 1 when there are no size tiers).
                level = headingSizes.Count + 1;
                return true;
            }
            level = 0;
            return false;
        }

        // 3. Per-page heading-density backoff: find pages where the heading heuristics fire on
        // too large a share of the lines, so the page is a diagram/label sheet rather than prose.
        var linesByPage = new Dictionary<int, int>();
        var headingsByPage = new Dictionary<int, int>();
        foreach (var line in lines)
        {
            linesByPage[line.Page] = linesByPage.GetValueOrDefault(line.Page) + 1;
            if (IsHeadingLine(line, out _))
            {
                headingsByPage[line.Page] = headingsByPage.GetValueOrDefault(line.Page) + 1;
            }
        }
        var suppressedPages = new HashSet<int>();
        foreach (var (page, count) in linesByPage)
        {
            if (count >= HeadingDensityMinLines &&
                headingsByPage.GetValueOrDefault(page) > count * HeadingDensityMaxFraction)
            {
                suppressedPages.Add(page);
            }
        }

        // 4. Walk lines, coalescing body and flushing it at each heading or page change.
        DocumentBlock? bodyBlock = null;

        void FlushBody()
        {
            if (bodyBlock is not null)
            {
                blocks.Add(bodyBlock);
                bodyBlock = null;
            }
        }

        foreach (var line in lines)
        {
            var level = 0;
            var isHeading = !suppressedPages.Contains(line.Page) && IsHeadingLine(line, out level);

            if (isHeading)
            {
                FlushBody();
                blocks.Add(new DocumentBlock
                {
                    Page = line.Page,
                    Kind = BlockKind.Heading,
                    HeadingLevel = level,
                    Text = line.Text,
                });
                continue;
            }

            if (bodyBlock is not null && bodyBlock.Page != line.Page)
            {
                FlushBody();
            }
            if (bodyBlock is null)
            {
                bodyBlock = new DocumentBlock { Page = line.Page, Kind = BlockKind.Body, Text = line.Text };
            }
            else
            {
                bodyBlock.Text += "\n" + line.Text;
            }
        }

        FlushBody();
        return blocks;
    }

    private static bool IsBoldFont(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName)) return false;
        return fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            || fontName.Contains("Semibold", StringComparison.OrdinalIgnoreCase)
            || fontName.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || fontName.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
    }
}
