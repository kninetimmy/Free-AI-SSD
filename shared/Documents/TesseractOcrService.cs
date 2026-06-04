using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;

namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// <see cref="IOcrService"/> backed by a Tesseract binary staged on the SSD. Pulls embedded images
/// out via <see cref="PdfImageExtractor"/> and shells out to Tesseract once per image in TSV mode
/// (PSM 11 — sparse text, the best mode for scattered labels on cockpit screenshots), keeping only
/// words at/above the configured confidence to suppress the garble dense imagery produces.
/// <para>
/// Every launch goes through <see cref="ProcessRunner"/> with an argument list, so no path or option
/// is ever shell-interpolated.
/// </para>
/// </summary>
public sealed class TesseractOcrService : IOcrService
{
    private readonly string? _exePath;
    private readonly string? _tessdataDir;
    private readonly SsdLogger? _logger;

    /// <summary>
    /// Constructs the service against an explicit Tesseract binary. <paramref name="tessdataDir"/>
    /// may be null when the binary can locate its own tessdata (e.g. a system install used in tests).
    /// </summary>
    public TesseractOcrService(string? tesseractExePath, string? tessdataDir, SsdLogger? logger = null)
    {
        _exePath = tesseractExePath;
        _tessdataDir = tessdataDir;
        _logger = logger;
    }

    /// <summary>Resolves the Tesseract binary + tessdata staged on the SSD for the current OS.</summary>
    public static TesseractOcrService FromSsdRoot(string ssdRoot, SsdLogger? logger = null)
    {
        var (exe, tessdata) = ResolveStagedPaths(ssdRoot);
        return new TesseractOcrService(exe, tessdata, logger);
    }

    public bool IsAvailable => !string.IsNullOrEmpty(_exePath) && File.Exists(_exePath);

    public async Task<IReadOnlyList<OcrPageText>> ExtractPdfImageTextAsync(
        string pdfPath, PortableConfig config, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Array.Empty<OcrPageText>();
        }

        var results = new List<OcrPageText>();
        var maxImages = Math.Max(1, config.OcrMaxImagesPerFile);
        var processed = 0;

        foreach (var image in PdfImageExtractor.Extract(pdfPath, Math.Max(1, config.OcrMinImagePixels)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processed >= maxImages)
            {
                _logger?.Warn($"OCR image cap ({maxImages}) reached for '{Path.GetFileName(pdfPath)}'; remaining images skipped.");
                break;
            }
            processed++;

            var text = await OcrImageAsync(image, config, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                results.Add(new OcrPageText(image.Page, text!));
            }
        }

        return results;
    }

    internal async Task<string?> OcrImageAsync(PdfEmbeddedImage image, PortableConfig config, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"freeai-ocr-{Guid.NewGuid():N}.{image.Format}");
        try
        {
            await File.WriteAllBytesAsync(tempPath, image.Bytes, ct).ConfigureAwait(false);

            // Arg order matters: image, outputbase, then options, then the config name LAST.
            var args = new List<string> { tempPath, "stdout" };
            if (!string.IsNullOrEmpty(_tessdataDir))
            {
                args.Add("--tessdata-dir");
                args.Add(_tessdataDir!);
            }
            args.Add("--psm");
            args.Add("11");
            args.Add("-l");
            args.Add(string.IsNullOrWhiteSpace(config.OcrLanguage) ? "eng" : config.OcrLanguage.Trim());
            args.Add("tsv");

            // ProcessRunner streams stdout AND stderr concurrently to onOutput, so collect on a
            // thread-safe queue. Line order is irrelevant: each TSV row carries its own coordinates.
            var lines = new ConcurrentQueue<string>();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, config.OcrPerImageTimeoutSeconds)));

            int exit;
            try
            {
                exit = await ProcessRunner.RunAsync(
                    _exePath!, args, Path.GetTempPath(),
                    onOutput: line => lines.Enqueue(line),
                    ct: timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-image timeout (not a real cancel): skip this image and keep going.
                _logger?.Warn($"OCR timed out on a {image.Width}x{image.Height} image (page {image.Page}); skipped.");
                return null;
            }

            if (exit != 0)
            {
                _logger?.Warn($"Tesseract exited {exit} on a page-{image.Page} image; skipped.");
                return null;
            }

            return ParseTsv(lines, config.OcrMinWordConfidence);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"OCR error on a page-{image.Page} image: {ex.Message}");
            return null;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Parses Tesseract TSV output, keeping word rows whose confidence is at/above
    /// <paramref name="minConfidence"/>. TSV columns are tab-separated:
    /// level, page, block, par, line, word, left, top, width, height, conf, text.
    /// </summary>
    internal static string ParseTsv(IEnumerable<string> lines, int minConfidence)
    {
        var words = new List<string>();
        foreach (var line in lines)
        {
            var cols = line.Split('\t');
            if (cols.Length < 12) continue;          // header-less / stderr noise lines
            if (cols[0] == "level") continue;        // TSV header row
            var text = cols[11];
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!double.TryParse(cols[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf)) continue;
            if (conf < minConfidence) continue;
            words.Add(text.Trim());
        }
        return string.Join(" ", words);
    }

    private static (string ExePath, string? TessdataDir) ResolveStagedPaths(string ssdRoot)
    {
        string toolDir, exeName;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            toolDir = Path.Combine(ssdRoot, "windows", "tools", "tesseract");
            exeName = "tesseract.exe";
        }
        else
        {
            toolDir = Path.Combine(ssdRoot, "mac", "tools", "tesseract");
            exeName = "tesseract";
        }

        var exe = Path.Combine(toolDir, exeName);
        var tessdata = Path.Combine(toolDir, "tessdata");
        return (exe, Directory.Exists(tessdata) ? tessdata : null);
    }
}
