using System.Text;
using UglyToad.PdfPig;

namespace FreeAiSsd.Shared.Documents;

public static class DocumentParser
{
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
        return new ParsedDocument { Segments = new List<ParsedSegment> { new() { Text = text } } };
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
