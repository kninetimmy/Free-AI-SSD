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
}
