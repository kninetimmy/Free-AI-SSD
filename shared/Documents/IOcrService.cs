namespace FreeAiSsd.Shared.Documents;

/// <summary>Text recovered by OCR from a single embedded image, attributed to its source page.</summary>
/// <param name="Page">1-based source page number the image appeared on.</param>
/// <param name="Text">OCR text for the image (already confidence-filtered by the service).</param>
public sealed record OcrPageText(int Page, string Text);

/// <summary>
/// Extracts text from images embedded in PDFs via OCR. The Phase-1 implementation shells out to a
/// Tesseract binary staged on the SSD; <see cref="IsAvailable"/> is false when Tesseract is not
/// staged on this host, so callers can skip OCR cleanly instead of failing the ingest.
/// </summary>
public interface IOcrService
{
    /// <summary>True when the OCR tool is staged and runnable on this host.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Runs OCR over the images embedded in <paramref name="pdfPath"/>. Returns one entry per image
    /// that produced text, attributed to its page; returns empty when OCR is unavailable or no image
    /// yielded text. Never throws for an individual bad image — those are skipped. Honors
    /// cancellation between images.
    /// </summary>
    Task<IReadOnlyList<OcrPageText>> ExtractPdfImageTextAsync(
        string pdfPath, PortableConfig config, CancellationToken cancellationToken = default);
}
