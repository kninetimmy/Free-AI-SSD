using System.Text;
using UglyToad.PdfPig;

namespace FreeAiSsd.Shared.Documents;

public static class DocumentParser
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".txt", ".md", ".json", ".csv"
    };

    public static bool IsSupported(string path) => Supported.Contains(Path.GetExtension(path));

    public static ParsedDocument Parse(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ParsePdf(filePath);
        }

        var text = File.ReadAllText(filePath);
        return new ParsedDocument { Segments = new List<ParsedSegment> { new() { Text = text } } };
    }

    private static ParsedDocument ParsePdf(string filePath)
    {
        var doc = new ParsedDocument();
        using var pdf = PdfDocument.Open(filePath);
        foreach (var page in pdf.GetPages())
        {
            var sb = new StringBuilder();
            sb.Append(page.Text);
            doc.Segments.Add(new ParsedSegment
            {
                Page = page.Number,
                Text = sb.ToString()
            });
        }

        return doc;
    }
}
