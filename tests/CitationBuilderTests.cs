using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class CitationBuilderTests
{
    private static DocumentChunk MakeChunk(string fileName, int? page = null, string section = "")
    {
        return new DocumentChunk
        {
            LibraryId = "lib1",
            SourceFileName = fileName,
            StoredRelativePath = $"files/{fileName}",
            Text = "sample text",
            TextLength = 11,
            Sha256 = "abc",
            Page = page,
            Section = section
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

    [Fact]
    public void Build_WithSectionAndPage_FormatsWithSectionAndPage()
    {
        var chunk = MakeChunk("guide.pdf", page: 412, section: "Engine Start");
        Assert.Equal("[guide.pdf §Engine Start p.412]", CitationBuilder.Build(chunk));
    }

    [Fact]
    public void Build_WithSectionNoPage_FormatsWithSection()
    {
        var chunk = MakeChunk("guide.md", section: "Introduction");
        Assert.Equal("[guide.md §Introduction]", CitationBuilder.Build(chunk));
    }

    [Fact]
    public void Build_EmptySection_RendersExactlyLikeBeforeStage2()
    {
        Assert.Equal("[report.pdf p.5]", CitationBuilder.Build(MakeChunk("report.pdf", page: 5)));
        Assert.Equal("[notes.txt]", CitationBuilder.Build(MakeChunk("notes.txt")));
    }

    [Fact]
    public void BuildDistinct_SameFileAndPageDifferentSections_RetainsAll()
    {
        // The point of section metadata: two chunks on the same page no longer collapse
        // to one citation when they belong to different sections.
        var chunks = new List<DocumentChunk>
        {
            MakeChunk("guide.pdf", page: 5, section: "Engine Start"),
            MakeChunk("guide.pdf", page: 5, section: "Engine Shutdown"),
        };

        var citations = CitationBuilder.BuildDistinct(chunks);

        Assert.Equal(2, citations.Count);
        Assert.Contains("[guide.pdf §Engine Start p.5]", citations);
        Assert.Contains("[guide.pdf §Engine Shutdown p.5]", citations);
    }
}
