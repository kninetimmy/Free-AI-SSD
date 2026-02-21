using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public sealed class DocumentIngestionSecurityTests : IDisposable
{
    private readonly string _tempRoot;

    public DocumentIngestionSecurityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"rag-sec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    // --- File size validation ---

    [Fact]
    public void FileSizeLimit_DefaultIs50MB()
    {
        var config = new PortableConfig();
        Assert.Equal(50, config.MaxDocumentSizeMB);
    }

    // --- DocumentParser.ValidateBeforeParse ---

    [Fact]
    public void ValidateBeforeParse_RejectsUnsupportedExtension()
    {
        var file = Path.Combine(_tempRoot, "test.exe");
        File.WriteAllText(file, "not an exe really");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DocumentParser.ValidateBeforeParse(file));
        Assert.Contains(".exe", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_RejectsPdfWithWrongMagicBytes()
    {
        var file = Path.Combine(_tempRoot, "fake.pdf");
        File.WriteAllText(file, "This is not a PDF file at all");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DocumentParser.ValidateBeforeParse(file));
        Assert.Contains("PDF magic bytes", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_AcceptsValidPdfHeader()
    {
        var file = Path.Combine(_tempRoot, "real.pdf");
        // Write a minimal PDF-like header (enough to pass magic-byte check).
        var header = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34 }; // %PDF-1.4
        File.WriteAllBytes(file, header);

        // Should not throw on validation (parsing itself may fail, but validation passes).
        var exception = Record.Exception(() => DocumentParser.ValidateBeforeParse(file));
        Assert.Null(exception);
    }

    [Fact]
    public void ValidateBeforeParse_RejectsTextFileWithPdfMagicBytes()
    {
        var file = Path.Combine(_tempRoot, "sneaky.txt");
        File.WriteAllBytes(file, new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00 });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DocumentParser.ValidateBeforeParse(file));
        Assert.Contains("PDF magic bytes", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_RejectsTextFileWithZipMagicBytes()
    {
        var file = Path.Combine(_tempRoot, "sneaky.csv");
        File.WriteAllBytes(file, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DocumentParser.ValidateBeforeParse(file));
        Assert.Contains("ZIP", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_RejectsTextFileWithElfMagicBytes()
    {
        var file = Path.Combine(_tempRoot, "sneaky.md");
        File.WriteAllBytes(file, new byte[] { 0x7F, 0x45, 0x4C, 0x46 });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DocumentParser.ValidateBeforeParse(file));
        Assert.Contains("ELF", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_RejectsTextFileWithPeMagicBytes()
    {
        var file = Path.Combine(_tempRoot, "sneaky.json");
        File.WriteAllBytes(file, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DocumentParser.ValidateBeforeParse(file));
        Assert.Contains("PE/MZ", ex.Message);
    }

    [Fact]
    public void ValidateBeforeParse_AcceptsValidTextFile()
    {
        var file = Path.Combine(_tempRoot, "readme.txt");
        File.WriteAllText(file, "Hello, this is a valid text file.");

        var exception = Record.Exception(() => DocumentParser.ValidateBeforeParse(file));
        Assert.Null(exception);
    }

    [Fact]
    public void ValidateBeforeParse_AcceptsEmptyTextFile()
    {
        var file = Path.Combine(_tempRoot, "empty.txt");
        File.WriteAllText(file, "");

        var exception = Record.Exception(() => DocumentParser.ValidateBeforeParse(file));
        Assert.Null(exception);
    }

    [Fact]
    public void Parse_CallsValidation_RejectsSpoofedPdf()
    {
        var file = Path.Combine(_tempRoot, "bad.pdf");
        File.WriteAllText(file, "Not a real PDF");

        Assert.Throws<InvalidOperationException>(() => DocumentParser.Parse(file));
    }

    // --- PathGuards (symlink / traversal) ---

    [Fact]
    public void PathGuards_RejectsPathOutsideRoot()
    {
        var root = Path.Combine(_tempRoot, "allowed");
        var outsidePath = Path.Combine(_tempRoot, "outside", "evil.txt");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "outside"));
        File.WriteAllText(outsidePath, "data");

        Assert.Throws<InvalidOperationException>(() =>
            PathGuards.EnsureUnderRoot(root, outsidePath));
    }

    [Fact]
    public void PathGuards_AcceptsPathInsideRoot()
    {
        var root = Path.Combine(_tempRoot, "allowed");
        Directory.CreateDirectory(root);
        var inside = Path.Combine(root, "doc.txt");
        File.WriteAllText(inside, "safe data");

        var result = PathGuards.EnsureUnderRoot(root, inside);
        Assert.Equal(Path.GetFullPath(inside), result);
    }
}
