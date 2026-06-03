using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class DocumentParserTests : IDisposable
{
    private readonly string _tempDir;

    public DocumentParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"parser-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string CreateBinaryFile(string name, byte[] content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    #region IsSupported

    [Theory]
    [InlineData(".txt")]
    [InlineData(".md")]
    [InlineData(".json")]
    [InlineData(".csv")]
    [InlineData(".pdf")]
    public void IsSupported_SupportedExtensions_ReturnsTrue(string ext)
    {
        Assert.True(DocumentParser.IsSupported($"document{ext}"));
    }

    [Theory]
    [InlineData(".docx")]
    [InlineData(".xlsx")]
    [InlineData(".png")]
    [InlineData(".exe")]
    [InlineData(".html")]
    [InlineData("")]
    public void IsSupported_UnsupportedExtensions_ReturnsFalse(string ext)
    {
        Assert.False(DocumentParser.IsSupported($"document{ext}"));
    }

    #endregion

    #region Parse — valid files

    [Fact]
    public void Parse_ValidTextFile_ReturnsContent()
    {
        var path = CreateFile("hello.txt", "Hello, world!");
        var result = DocumentParser.Parse(path);

        Assert.Single(result.Segments);
        Assert.Equal("Hello, world!", result.Segments[0].Text);
        Assert.Null(result.Segments[0].Page);
    }

    [Fact]
    public void Parse_ValidMarkdownFile_ReturnsContent()
    {
        var content = "# Heading\n\nSome **bold** text.";
        var path = CreateFile("readme.md", content);
        var result = DocumentParser.Parse(path);

        Assert.Single(result.Segments);
        Assert.Equal(content, result.Segments[0].Text);
    }

    [Fact]
    public void Parse_ValidJsonFile_ReturnsContent()
    {
        var content = "{\"key\": \"value\"}";
        var path = CreateFile("data.json", content);
        var result = DocumentParser.Parse(path);

        Assert.Single(result.Segments);
        Assert.Equal(content, result.Segments[0].Text);
    }

    [Fact]
    public void Parse_ValidCsvFile_ReturnsContent()
    {
        var content = "name,age\nAlice,30\nBob,25";
        var path = CreateFile("data.csv", content);
        var result = DocumentParser.Parse(path);

        Assert.Single(result.Segments);
        Assert.Contains("Alice", result.Segments[0].Text);
    }

    [Fact]
    public void Parse_EmptyTextFile_ReturnsEmptyContent()
    {
        var path = CreateFile("empty.txt", "");
        var result = DocumentParser.Parse(path);

        Assert.Single(result.Segments);
        Assert.Equal("", result.Segments[0].Text);
    }

    #endregion

    #region Parse — unsupported extensions

    [Fact]
    public void Parse_UnsupportedExtension_ThrowsInvalidOperationException()
    {
        var path = CreateFile("image.png", "not really a png");
        var ex = Assert.Throws<InvalidOperationException>(() => DocumentParser.Parse(path));
        Assert.Contains("Unsupported file extension", ex.Message);
    }

    #endregion

    #region ValidateBeforeParse — magic byte spoofing

    [Fact]
    public void ValidateBeforeParse_TextFileWithPdfMagicBytes_Throws()
    {
        // %PDF header in a .txt file
        var pdfHeader = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 };
        var path = CreateBinaryFile("spoofed.txt", pdfHeader);

        var ex = Assert.Throws<InvalidOperationException>(() => DocumentParser.ValidateBeforeParse(path));
        Assert.Contains("PDF magic bytes", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_TextFileWithZipMagicBytes_Throws()
    {
        var zipHeader = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00 };
        var path = CreateBinaryFile("spoofed.txt", zipHeader);

        var ex = Assert.Throws<InvalidOperationException>(() => DocumentParser.ValidateBeforeParse(path));
        Assert.Contains("ZIP/OOXML magic bytes", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_TextFileWithElfMagicBytes_Throws()
    {
        var elfHeader = new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01, 0x01, 0x00 };
        var path = CreateBinaryFile("spoofed.txt", elfHeader);

        var ex = Assert.Throws<InvalidOperationException>(() => DocumentParser.ValidateBeforeParse(path));
        Assert.Contains("ELF magic bytes", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_TextFileWithPeMagicBytes_Throws()
    {
        var peHeader = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 };
        var path = CreateBinaryFile("spoofed.md", peHeader);

        var ex = Assert.Throws<InvalidOperationException>(() => DocumentParser.ValidateBeforeParse(path));
        Assert.Contains("PE/MZ magic bytes", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_ValidTextFile_DoesNotThrow()
    {
        var path = CreateFile("valid.txt", "Just a normal text file.");
        var ex = Record.Exception(() => DocumentParser.ValidateBeforeParse(path));
        Assert.Null(ex);
    }

    [Fact]
    public void ValidateBeforeParse_EmptyTextFile_DoesNotThrow()
    {
        var path = CreateFile("empty.txt", "");
        var ex = Record.Exception(() => DocumentParser.ValidateBeforeParse(path));
        Assert.Null(ex);
    }

    #endregion

    #region Block extraction — markdown

    [Fact]
    public void Parse_Markdown_DetectsAtxHeadingLevels()
    {
        var content = "# Title\n\nIntro paragraph.\n\n## Subsection\n\nDetail text.\n\n### Deeper\n\nMore.";
        var path = CreateFile("doc.md", content);

        var blocks = DocumentParser.Parse(path).Blocks;

        Assert.Collection(blocks,
            b => { Assert.Equal(BlockKind.Heading, b.Kind); Assert.Equal(1, b.HeadingLevel); Assert.Equal("Title", b.Text); },
            b => { Assert.Equal(BlockKind.Body, b.Kind); Assert.Equal("Intro paragraph.", b.Text); },
            b => { Assert.Equal(BlockKind.Heading, b.Kind); Assert.Equal(2, b.HeadingLevel); Assert.Equal("Subsection", b.Text); },
            b => { Assert.Equal(BlockKind.Body, b.Kind); Assert.Equal("Detail text.", b.Text); },
            b => { Assert.Equal(BlockKind.Heading, b.Kind); Assert.Equal(3, b.HeadingLevel); Assert.Equal("Deeper", b.Text); },
            b => { Assert.Equal(BlockKind.Body, b.Kind); Assert.Equal("More.", b.Text); });
    }

    [Fact]
    public void Parse_Markdown_HashWithoutSpace_IsNotAHeading()
    {
        // CommonMark requires a space after the hash run; "#NotAHeading" is body text.
        var path = CreateFile("doc.md", "#NotAHeading\n\nbody");

        var blocks = DocumentParser.Parse(path).Blocks;

        Assert.All(blocks, b => Assert.Equal(BlockKind.Body, b.Kind));
        Assert.Contains(blocks, b => b.Text.Contains("#NotAHeading"));
    }

    [Fact]
    public void Parse_PlainText_ProducesSingleBodyBlock()
    {
        var path = CreateFile("notes.txt", "Just plain text, no structure.");

        var blocks = DocumentParser.Parse(path).Blocks;

        var block = Assert.Single(blocks);
        Assert.Equal(BlockKind.Body, block.Kind);
        Assert.Equal("Just plain text, no structure.", block.Text);
        Assert.Null(block.Page);
    }

    #endregion

    #region Block extraction — font-tier classifier (pure function)

    [Fact]
    public void ClassifyLinesIntoBlocks_FontTiers_AssignsDescendingLevels()
    {
        var lines = new List<LineRecord>
        {
            new() { Page = 1, Text = "Chapter One", FontSize = 24 },
            new() { Page = 1, Text = "Section A", FontSize = 18 },
            new() { Page = 1, Text = "Body text line one here.", FontSize = 12 },
            new() { Page = 1, Text = "Body text line two here.", FontSize = 12 },
        };

        var blocks = DocumentParser.ClassifyLinesIntoBlocks(lines);

        Assert.Collection(blocks,
            b => { Assert.Equal(BlockKind.Heading, b.Kind); Assert.Equal(1, b.HeadingLevel); Assert.Equal("Chapter One", b.Text); },
            b => { Assert.Equal(BlockKind.Heading, b.Kind); Assert.Equal(2, b.HeadingLevel); Assert.Equal("Section A", b.Text); },
            b =>
            {
                Assert.Equal(BlockKind.Body, b.Kind);
                Assert.Contains("line one", b.Text);
                Assert.Contains("line two", b.Text);
            });
    }

    [Fact]
    public void ClassifyLinesIntoBlocks_UniformFont_YieldsAllBody()
    {
        var lines = new List<LineRecord>
        {
            new() { Page = 1, Text = "All the same size, line one.", FontSize = 12 },
            new() { Page = 1, Text = "All the same size, line two.", FontSize = 12 },
            new() { Page = 1, Text = "All the same size, line three.", FontSize = 12 },
        };

        var blocks = DocumentParser.ClassifyLinesIntoBlocks(lines);

        var block = Assert.Single(blocks);
        Assert.Equal(BlockKind.Body, block.Kind);
    }

    [Fact]
    public void ClassifyLinesIntoBlocks_BoldShortLine_NoSizeTier_IsHeading()
    {
        var lines = new List<LineRecord>
        {
            new() { Page = 1, Text = "WARNING", FontSize = 12, IsBold = true },
            new() { Page = 1, Text = "Do not exceed the limit shown on the gauge.", FontSize = 12 },
        };

        var blocks = DocumentParser.ClassifyLinesIntoBlocks(lines);

        Assert.Collection(blocks,
            b => { Assert.Equal(BlockKind.Heading, b.Kind); Assert.Equal("WARNING", b.Text); },
            b => Assert.Equal(BlockKind.Body, b.Kind));
    }

    [Fact]
    public void ClassifyLinesIntoBlocks_Body_FlushesOnPageChange()
    {
        var lines = new List<LineRecord>
        {
            new() { Page = 1, Text = "Body on page one.", FontSize = 12 },
            new() { Page = 2, Text = "Body on page two.", FontSize = 12 },
        };

        var blocks = DocumentParser.ClassifyLinesIntoBlocks(lines);

        Assert.Collection(blocks,
            b => { Assert.Equal(BlockKind.Body, b.Kind); Assert.Equal(1, b.Page); },
            b => { Assert.Equal(BlockKind.Body, b.Kind); Assert.Equal(2, b.Page); });
    }

    [Fact]
    public void ClassifyLinesIntoBlocks_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(DocumentParser.ClassifyLinesIntoBlocks(new List<LineRecord>()));
    }

    [Fact]
    public void ClassifyLinesIntoBlocks_DenseDiagramPage_SuppressesHeadingOverFire()
    {
        // A graphically dense page (e.g. a cockpit diagram): many short bold labels at assorted
        // sizes, each of which would individually trip a heading heuristic. Collectively they are
        // noise, so the whole page degrades to body rather than fragmenting into one chunk per
        // label — the F18-guide ingest explosion (#68).
        var lines = new List<LineRecord>();
        for (var i = 0; i < 20; i++)
        {
            lines.Add(new LineRecord { Page = 1, Text = $"LABEL {i}", FontSize = 12 + (i % 4), IsBold = true });
        }
        lines.Add(new LineRecord { Page = 1, Text = "Some genuine descriptive body text on the page.", FontSize = 12 });

        var blocks = DocumentParser.ClassifyLinesIntoBlocks(lines);

        Assert.NotEmpty(blocks);
        Assert.All(blocks, b => Assert.Equal(BlockKind.Body, b.Kind));
    }

    [Fact]
    public void ClassifyLinesIntoBlocks_SparseHeadingsOnDensePage_StillDetected()
    {
        // A normal prose page with one heading among many body lines stays well under the
        // density threshold, so the heading is preserved (the backoff must not be over-eager).
        var lines = new List<LineRecord>
        {
            new() { Page = 1, Text = "Chapter Two", FontSize = 20 },
        };
        for (var i = 0; i < 10; i++)
        {
            lines.Add(new LineRecord { Page = 1, Text = $"Ordinary body sentence number {i} of the page.", FontSize = 12 });
        }

        var blocks = DocumentParser.ClassifyLinesIntoBlocks(lines);

        Assert.Contains(blocks, b => b.Kind == BlockKind.Heading && b.Text == "Chapter Two");
    }

    #endregion

    #region Block extraction — real PDF (PdfPig word-extraction path)

    [Fact]
    public void Parse_RealPdfFixture_ProducesPagedBlocksWithBodyText()
    {
        // Exercises the impure ExtractPdfLines + classifier path against an actual text-layer
        // PDF — the half the synthetic LineRecord tests can't reach.
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "RagCorpus", "faa_ac_90-66c.pdf");
        Assert.True(File.Exists(fixture), $"Fixture missing: {fixture}");

        var parsed = DocumentParser.Parse(fixture);

        Assert.NotEmpty(parsed.Blocks);
        // Body text survived word reconstruction (not collapsed to nothing).
        Assert.Contains(parsed.Blocks, b => b.Kind == BlockKind.Body && b.Text.Length > 100);
        // Every block from a paginated source carries its page.
        Assert.All(parsed.Blocks, b => Assert.NotNull(b.Page));
    }

    #endregion
}
