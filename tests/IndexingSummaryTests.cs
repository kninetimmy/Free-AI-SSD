using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public sealed class IndexingSummaryTests
{
    [Fact]
    public void Format_NothingSkipped_SingleFile_UsesSingular()
    {
        var summary = IndexingSummary.Format(new IndexingProgress
        {
            CompletedFiles = 1,
            EmbeddedChunks = 0,
            SkippedFiles = 0
        });

        Assert.Equal("Indexed 1 file.", summary);
    }

    [Fact]
    public void Format_WithChunks_IncludesChunkCount()
    {
        var summary = IndexingSummary.Format(new IndexingProgress
        {
            CompletedFiles = 3,
            EmbeddedChunks = 412,
            SkippedFiles = 0
        });

        Assert.Equal("Indexed 3 files (412 chunks).", summary);
    }

    [Fact]
    public void Format_WithSkipped_AppendsSkippedClause()
    {
        var summary = IndexingSummary.Format(new IndexingProgress
        {
            CompletedFiles = 3,
            EmbeddedChunks = 120,
            SkippedFiles = 2
        });

        Assert.Equal("Indexed 3 files (120 chunks), 2 skipped (unsupported/oversize).", summary);
    }

    [Fact]
    public void Format_NullSkipped_TreatedAsZero()
    {
        var summary = IndexingSummary.Format(new IndexingProgress
        {
            CompletedFiles = 2,
            EmbeddedChunks = 0,
            SkippedFiles = null
        });

        Assert.Equal("Indexed 2 files.", summary);
    }

    [Fact]
    public void DescribeFailure_SingleException_ReturnsItsMessage()
    {
        var ex = new InvalidOperationException("model 'nomic-embed-text' not found");

        Assert.Equal("model 'nomic-embed-text' not found", IndexingSummary.DescribeFailure(ex));
    }

    [Fact]
    public void DescribeFailure_AggregateException_CountsAndSurfacesFirstCause()
    {
        var agg = new AggregateException(
            "Document ingestion completed with 2 file failure(s).",
            new InvalidOperationException("first failure cause"),
            new InvalidOperationException("second failure cause"));

        Assert.Equal("2 files failed: first failure cause", IndexingSummary.DescribeFailure(agg));
    }
}
