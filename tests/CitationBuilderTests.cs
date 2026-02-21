using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class CitationBuilderTests
{
    private static DocumentChunk MakeChunk(string fileName, int? page = null)
    {
        return new DocumentChunk
        {
            LibraryId = "lib1",
            SourceFileName = fileName,
            StoredRelativePath = $"files/{fileName}",
            Text = "sample text",
            TextLength = 11,
            Sha256 = "abc",
            Page = page
        };
    }

    [Fact]
    public void Build_WithPage_FormatsCitationWithPage()
    {
        var chunk = MakeChunk("report.pdf", page: 5);
        var citation = CitationBuilder.Build(chunk);
        Assert.Equal("[report.pdf p.5]", citation);
    }

    [Fact]
    public void Build_WithoutPage_FormatsCitationWithoutPage()
    {
        var chunk = MakeChunk("notes.txt");
        var citation = CitationBuilder.Build(chunk);
        Assert.Equal("[notes.txt]", citation);
    }

    [Fact]
    public void Build_WithPageOne_IncludesPageNumber()
    {
        var chunk = MakeChunk("manual.pdf", page: 1);
        var citation = CitationBuilder.Build(chunk);
        Assert.Equal("[manual.pdf p.1]", citation);
    }

    [Fact]
    public void BuildDistinct_MultipleChunksSameFile_DeduplicatesCitations()
    {
        var chunks = new List<DocumentChunk>
        {
            MakeChunk("notes.txt"),
            MakeChunk("notes.txt"),
            MakeChunk("notes.txt")
        };

        var citations = CitationBuilder.BuildDistinct(chunks);
        Assert.Single(citations);
        Assert.Equal("[notes.txt]", citations[0]);
    }

    [Fact]
    public void BuildDistinct_DifferentDocuments_ReturnsAllCitations()
    {
        var chunks = new List<DocumentChunk>
        {
            MakeChunk("a.txt"),
            MakeChunk("b.txt"),
            MakeChunk("c.txt")
        };

        var citations = CitationBuilder.BuildDistinct(chunks);
        Assert.Equal(3, citations.Count);
        Assert.Contains("[a.txt]", citations);
        Assert.Contains("[b.txt]", citations);
        Assert.Contains("[c.txt]", citations);
    }

    [Fact]
    public void BuildDistinct_SameFileDifferentPages_RetainsAll()
    {
        var chunks = new List<DocumentChunk>
        {
            MakeChunk("report.pdf", page: 1),
            MakeChunk("report.pdf", page: 2),
            MakeChunk("report.pdf", page: 3)
        };

        var citations = CitationBuilder.BuildDistinct(chunks);
        Assert.Equal(3, citations.Count);
        Assert.Contains("[report.pdf p.1]", citations);
        Assert.Contains("[report.pdf p.2]", citations);
        Assert.Contains("[report.pdf p.3]", citations);
    }

    [Fact]
    public void BuildDistinct_DuplicatePageReferences_Deduplicated()
    {
        var chunks = new List<DocumentChunk>
        {
            MakeChunk("report.pdf", page: 1),
            MakeChunk("report.pdf", page: 1),
            MakeChunk("report.pdf", page: 2)
        };

        var citations = CitationBuilder.BuildDistinct(chunks);
        Assert.Equal(2, citations.Count);
    }

    [Fact]
    public void BuildDistinct_EmptyInput_ReturnsEmptyList()
    {
        var citations = CitationBuilder.BuildDistinct(new List<DocumentChunk>());
        Assert.Empty(citations);
    }
}
