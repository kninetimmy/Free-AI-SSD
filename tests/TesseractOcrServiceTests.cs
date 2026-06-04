using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace FreeAiSsd.Tests;

/// <summary>
/// Coverage for the OCR seams: the TSV confidence/parse logic (pure), the
/// availability gate, embedded-image extraction (tesseract-free, runs in CI),
/// and one real-command integration test that runs the actual Tesseract binary
/// over a known-text image — per the project rule that shell-out code carries at
/// least one real-command test, not mocks only.
/// </summary>
public sealed class TesseractOcrServiceTests
{
    private static string FixturePngPath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ocr", "ocr-sample.png");

    // ---- ParseTsv (pure confidence filter) ----

    [Fact]
    public void ParseTsv_KeepsConfidentWords_DropsLowConf_BlankText_AndHeader()
    {
        var lines = new[]
        {
            "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext",
            "5\t1\t1\t1\t1\t1\t10\t10\t40\t20\t92.5\tAVIONICS",
            "5\t1\t1\t1\t1\t2\t60\t10\t30\t20\t30.0\tnoise",   // below threshold
            "5\t1\t1\t1\t1\t3\t100\t10\t30\t20\t88.1\tRADAR",
            "5\t1\t1\t1\t1\t4\t140\t10\t10\t20\t99.0\t   ",     // blank text kept out
            "short\tline",                                       // too few columns
        };

        Assert.Equal("AVIONICS RADAR", TesseractOcrService.ParseTsv(lines, minConfidence: 55));
    }

    [Fact]
    public void ParseTsv_ZeroThreshold_KeepsAllConfidentWords()
    {
        var lines = new[]
        {
            "5\t1\t1\t1\t1\t1\t0\t0\t1\t1\t5.0\tfoo",
            "5\t1\t1\t1\t1\t2\t0\t0\t1\t1\t1.0\tbar",
        };

        Assert.Equal("foo bar", TesseractOcrService.ParseTsv(lines, minConfidence: 0));
    }

    [Fact]
    public void ParseTsv_NonNumericConfidence_SkipsRow()
    {
        var lines = new[] { "5\t1\t1\t1\t1\t1\t0\t0\t1\t1\txx\tword" };
        Assert.Equal(string.Empty, TesseractOcrService.ParseTsv(lines, minConfidence: 0));
    }

    [Fact]
    public void ParseTsv_StructuralRows_NegativeConfidence_AreDropped()
    {
        // Tesseract emits conf=-1 for the block/para/line structural rows.
        var lines = new[]
        {
            "1\t1\t0\t0\t0\t0\t0\t0\t612\t792\t-1\t",
            "5\t1\t1\t1\t1\t1\t0\t0\t1\t1\t80.0\tHELLO",
        };
        Assert.Equal("HELLO", TesseractOcrService.ParseTsv(lines, minConfidence: 0));
    }

    // ---- Availability gate ----

    [Fact]
    public void IsAvailable_FalseWhenExePathNullOrMissing()
    {
        Assert.False(new TesseractOcrService(null, null).IsAvailable);
        Assert.False(new TesseractOcrService(@"X:\nope\tesseract.exe", null).IsAvailable);
    }

    [Fact]
    public async Task ExtractPdfImageTextAsync_WhenUnavailable_ReturnsEmpty()
    {
        var svc = new TesseractOcrService(tesseractExePath: null, tessdataDir: null);
        var result = await svc.ExtractPdfImageTextAsync("does-not-exist.pdf", new PortableConfig());
        Assert.Empty(result);
    }

    // ---- PdfImageExtractor (no tesseract; runs everywhere) ----

    [Fact]
    public void PdfImageExtractor_ExtractsEmbeddedImage_AttributedToPage()
    {
        var pdfPath = BuildPdfWithFixtureImage();
        try
        {
            var images = PdfImageExtractor.Extract(pdfPath, minPixels: 1).ToList();

            Assert.NotEmpty(images);
            var img = images[0];
            Assert.Equal(1, img.Page);
            Assert.True(img.Bytes.Length > 0);
            Assert.Contains(img.Format, new[] { "png", "jpg" });
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    [Fact]
    public void PdfImageExtractor_RespectsMinPixelsFloor()
    {
        var pdfPath = BuildPdfWithFixtureImage();
        try
        {
            // Fixture is 720x220 ≈ 158k px; a 1M-pixel floor excludes it.
            var images = PdfImageExtractor.Extract(pdfPath, minPixels: 1_000_000).ToList();
            Assert.Empty(images);
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    // ---- Real Tesseract binary: the shell-out integration test ----

    [TesseractIntegrationFact]
    public async Task OcrImage_RealBinary_RecoversKnownText()
    {
        var exe = TesseractTestBinary.Find()!;
        var svc = new TesseractOcrService(exe, TesseractTestBinary.TessdataDirFor(exe));
        Assert.True(svc.IsAvailable);

        var pngBytes = File.ReadAllBytes(FixturePngPath);
        var image = new PdfEmbeddedImage(Page: 1, Bytes: pngBytes, Format: "png", Width: 720, Height: 220);
        var config = new PortableConfig { OcrMinWordConfidence = 40 };

        var text = await svc.OcrImageAsync(image, config, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("AVIONICS", text, StringComparison.OrdinalIgnoreCase);
    }

    [TesseractIntegrationFact]
    public async Task ExtractPdfImageTextAsync_RealBinary_RecoversTextFromEmbeddedImage()
    {
        var exe = TesseractTestBinary.Find()!;
        var svc = new TesseractOcrService(exe, TesseractTestBinary.TessdataDirFor(exe));

        var pdfPath = BuildPdfWithFixtureImage();
        try
        {
            var config = new PortableConfig { OcrMinWordConfidence = 40, OcrMinImagePixels = 1 };
            var pages = await svc.ExtractPdfImageTextAsync(pdfPath, config);

            Assert.NotEmpty(pages);
            Assert.All(pages, p => Assert.Equal(1, p.Page));
            var text = string.Join(" ", pages.Select(p => p.Text));
            Assert.Contains("AVIONICS", text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(pdfPath);
        }
    }

    private static string BuildPdfWithFixtureImage()
    {
        var pngBytes = File.ReadAllBytes(FixturePngPath);
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(612, 792);
        page.AddPng(pngBytes, new PdfRectangle(40, 520, 580, 720));
        var bytes = builder.Build();

        var pdfPath = Path.Combine(Path.GetTempPath(), $"ocr-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(pdfPath, bytes);
        return pdfPath;
    }
}
