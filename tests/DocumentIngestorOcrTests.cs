using System.Net;
using System.Net.Http.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using Microsoft.Data.Sqlite;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace FreeAiSsd.Tests;

/// <summary>
/// Verifies the DocumentIngestor OCR-append seam: when OCR is enabled and a
/// stub <see cref="IOcrService"/> returns image text, those words land in the
/// index as additive <c>content_type="ocr"</c> chunks without disturbing the
/// text-layer chunks; and when OCR is off or unavailable, nothing is added and
/// the service isn't even consulted. The real Tesseract binary is exercised
/// separately in <see cref="TesseractOcrServiceTests"/> — here OCR output is a
/// stub so the test is deterministic and offline.
/// </summary>
public sealed class DocumentIngestorOcrTests : IDisposable
{
    private readonly string _tempRoot;

    public DocumentIngestorOcrTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"doc-ingestor-ocr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
        {
            try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Ingest_OcrEnabled_AppendsOcrChunks_WithoutReplacingTextLayer()
    {
        var ocr = new StubOcrService(available: true,
            new OcrPageText(3, "MASTER ARM SELECTED"));
        var (manager, manifest, ingestor) = await CreateIngestorAsync(ocr);
        var pdfPath = BuildTextPdf("Reference manual body paragraph for the avionics suite.");
        var config = CreateConfig(ocrEnabled: true);

        await ingestor.IngestFilesAsync(manifest, new[] { pdfPath }, "localhost:11434", config);

        Assert.Equal(1, ocr.CallCount);
        var rows = ReadChunks(manager, manifest.Id);
        var ocrRows = rows.Where(r => r.ContentType == "ocr").ToList();
        Assert.NotEmpty(ocrRows);
        Assert.Contains(ocrRows, r => r.Text.Contains("MASTER ARM SELECTED", StringComparison.Ordinal));
        Assert.All(ocrRows, r => Assert.Equal(3, r.Page));
        // Additive: the text layer survived alongside the OCR chunks.
        Assert.Contains(rows, r => r.ContentType == "text");
    }

    [Fact]
    public async Task Ingest_OcrDisabled_AddsNothing_AndDoesNotConsultService()
    {
        var ocr = new StubOcrService(available: true,
            new OcrPageText(1, "SHOULD NOT APPEAR"));
        var (manager, manifest, ingestor) = await CreateIngestorAsync(ocr);
        var pdfPath = BuildTextPdf("Plain reference paragraph with no OCR requested.");
        var config = CreateConfig(ocrEnabled: false);

        await ingestor.IngestFilesAsync(manifest, new[] { pdfPath }, "localhost:11434", config);

        Assert.Equal(0, ocr.CallCount);
        var rows = ReadChunks(manager, manifest.Id);
        Assert.DoesNotContain(rows, r => r.ContentType == "ocr");
        Assert.NotEmpty(rows); // the text layer still indexed
    }

    [Fact]
    public async Task Ingest_OcrEnabledButServiceUnavailable_AddsNothing()
    {
        var ocr = new StubOcrService(available: false,
            new OcrPageText(1, "UNREACHABLE"));
        var (manager, manifest, ingestor) = await CreateIngestorAsync(ocr);
        var pdfPath = BuildTextPdf("Body text where the OCR engine is not staged.");
        var config = CreateConfig(ocrEnabled: true);

        await ingestor.IngestFilesAsync(manifest, new[] { pdfPath }, "localhost:11434", config);

        Assert.Equal(0, ocr.CallCount);
        var rows = ReadChunks(manager, manifest.Id);
        Assert.DoesNotContain(rows, r => r.ContentType == "ocr");
    }

    [Fact]
    public async Task Ingest_NonPdf_DoesNotConsultOcrService()
    {
        var ocr = new StubOcrService(available: true,
            new OcrPageText(1, "NOT FOR TXT"));
        var (manager, manifest, ingestor) = await CreateIngestorAsync(ocr);
        var txtPath = Path.Combine(_tempRoot, "notes.txt");
        await File.WriteAllTextAsync(txtPath,
            "A plain text file should never be routed through PDF image OCR even when enabled.");
        var config = CreateConfig(ocrEnabled: true);

        await ingestor.IngestFilesAsync(manifest, new[] { txtPath }, "localhost:11434", config);

        Assert.Equal(0, ocr.CallCount);
        var rows = ReadChunks(manager, manifest.Id);
        Assert.DoesNotContain(rows, r => r.ContentType == "ocr");
    }

    private async Task<(DocumentLibraryManager Manager, DocumentLibraryManifest Manifest, DocumentIngestor Ingestor)>
        CreateIngestorAsync(IOcrService ocr)
    {
        var ssdRoot = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);
        var manifest = await manager.CreateLibraryAsync("ocr-lib");
        var embeddingClient = new EmbeddingClient(new HttpClient(new SuccessEmbeddingHandler()));
        var ingestor = new DocumentIngestor(manager, embeddingClient, logger: null, ocrService: ocr);
        return (manager, manifest, ingestor);
    }

    private static PortableConfig CreateConfig(bool ocrEnabled) => new()
    {
        ChunkSize = 200,
        ChunkOverlap = 0,
        MaxEmbeddingConcurrency = 1,
        OcrEnabled = ocrEnabled,
    };

    private string BuildTextPdf(string text)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(612, 792);
        page.AddText(text, 12, new PdfPoint(50, 700), font);
        var bytes = builder.Build();

        var path = Path.Combine(_tempRoot, $"src-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static List<(string Text, string ContentType, int? Page)> ReadChunks(
        DocumentLibraryManager manager, string libraryId)
    {
        var rows = new List<(string, string, int?)>();
        var dbPath = Path.Combine(manager.GetIndexPath(libraryId), "vectors.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT text, content_type, page FROM chunks";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var text = reader.GetString(0);
            var contentType = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            int? page = reader.IsDBNull(2) ? null : reader.GetInt32(2);
            rows.Add((text, contentType, page));
        }
        return rows;
    }

    private sealed class StubOcrService : IOcrService
    {
        private readonly IReadOnlyList<OcrPageText> _pages;

        public StubOcrService(bool available, params OcrPageText[] pages)
        {
            IsAvailable = available;
            _pages = pages;
        }

        public bool IsAvailable { get; }
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<OcrPageText>> ExtractPdfImageTextAsync(
            string pdfPath, PortableConfig config, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_pages);
        }
    }

    // Batch-aware success handler: one [1,0,0] embedding per input, mirroring
    // Ollama's /api/embed array contract. Enough for the OCR-append assertions,
    // which care about which chunks land — not their vectors.
    private sealed class SuccessEmbeddingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var count = 1;
            if (doc.RootElement.TryGetProperty("input", out var input))
            {
                count = input.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? input.GetArrayLength()
                    : 1;
            }
            var embeddings = Enumerable.Range(0, count).Select(_ => new[] { 1f, 0f, 0f }).ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { embeddings })
            };
        }
    }
}
