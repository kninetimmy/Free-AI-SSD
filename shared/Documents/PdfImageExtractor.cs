using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// One raster image extracted from a PDF, as encoded bytes ready to hand to an image decoder / OCR.
/// </summary>
/// <param name="Page">1-based source page number.</param>
/// <param name="Bytes">Encoded image bytes (PNG or JPEG, per <see cref="Format"/>).</param>
/// <param name="Format">"png" or "jpg".</param>
/// <param name="Width">Image width in samples.</param>
/// <param name="Height">Image height in samples.</param>
public sealed record PdfEmbeddedImage(int Page, byte[] Bytes, string Format, int Width, int Height);

/// <summary>
/// Pure extraction of embedded raster images from a PDF via PdfPig. Kept separate from the (impure,
/// shell-out) OCR service so the byte-wrangling is unit-testable on its own.
/// <para>
/// PdfPig's <see cref="IPdfImage.TryGetPng"/> handles Flate/raw images, but in the build we ship it
/// fails to re-encode DCTDecode (JPEG) images — and those are the overwhelming majority in real
/// guides (verified: 46/47 sampled images). For a DCTDecode image the <see cref="IPdfImage.RawBytes"/>
/// are already a complete JPEG, so we emit them directly. Images stored under filters we can surface
/// as neither PNG nor JPEG (JPXDecode, CCITTFax, JBIG2) are skipped in Phase 1 rather than guessed at.
/// </para>
/// </summary>
public static class PdfImageExtractor
{
    /// <summary>
    /// Streams the embedded images of <paramref name="pdfPath"/> at or above <paramref name="minPixels"/>
    /// (width × height). Lazy: the PDF stays open for the duration of enumeration, so callers that OCR
    /// each image as they go don't buffer every bitmap in memory.
    /// </summary>
    public static IEnumerable<PdfEmbeddedImage> Extract(string pdfPath, int minPixels)
    {
        using var doc = PdfDocument.Open(pdfPath);
        foreach (var page in doc.GetPages())
        {
            foreach (var image in page.GetImages())
            {
                var extracted = TryExtract(image, page.Number, minPixels);
                if (extracted is not null)
                {
                    yield return extracted;
                }
            }
        }
    }

    private static PdfEmbeddedImage? TryExtract(IPdfImage image, int page, int minPixels)
    {
        try
        {
            var width = image.WidthInSamples;
            var height = image.HeightInSamples;
            if ((long)width * height < minPixels)
            {
                return null;
            }

            if (image.TryGetPng(out var png) && png is { Length: > 0 })
            {
                return new PdfEmbeddedImage(page, png, "png", width, height);
            }

            var raw = image.RawBytes;
            if (IsJpeg(raw))
            {
                return new PdfEmbeddedImage(page, raw.ToArray(), "jpg", width, height);
            }

            return null;
        }
        catch
        {
            // A single malformed image must not abort extraction of the rest of the document.
            return null;
        }
    }

    private static bool IsJpeg(IReadOnlyList<byte> bytes)
        => bytes.Count >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
}
