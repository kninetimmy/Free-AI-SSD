using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class RrfFusionTests
{
    private static RetrievalResult R(string path, int index, double score = 0, string text = "")
        => new()
        {
            Score = score,
            Chunk = new DocumentChunk { StoredRelativePath = path, ChunkIndex = index, Text = text }
        };

    [Fact]
    public void Fuse_ChunkInBothArms_OutranksSingleArmChunks()
    {
        var dense = new List<RetrievalResult> { R("a", 0), R("b", 1) };
        var lexical = new List<RetrievalResult> { R("b", 1), R("c", 2) };

        var fused = RrfFusion.Fuse(dense, lexical);

        // b: dense rank 1 (1/61) + lexical rank 0 (1/60) — present in both, so top.
        Assert.Equal(3, fused.Count);
        Assert.Equal("b", fused[0].Chunk.StoredRelativePath);
        Assert.Equal(1, fused[0].Chunk.ChunkIndex);
    }

    [Fact]
    public void Fuse_ScoreIsSumOfReciprocalRanks()
    {
        var dense = new List<RetrievalResult> { R("a", 0) };   // rank 0 -> 1/60
        var lexical = new List<RetrievalResult> { R("a", 0) };  // rank 0 -> 1/60

        var fused = RrfFusion.Fuse(dense, lexical);

        Assert.Single(fused);
        Assert.Equal(2.0 / RrfFusion.DefaultK, fused[0].Score, precision: 12);
    }

    [Fact]
    public void Fuse_PreservesSingleArmOrder()
    {
        var dense = new List<RetrievalResult> { R("a", 0), R("b", 0), R("c", 0) };

        var fused = RrfFusion.Fuse(dense, new List<RetrievalResult>());

        Assert.Equal(new[] { "a", "b", "c" }, fused.Select(f => f.Chunk.StoredRelativePath).ToArray());
    }

    [Fact]
    public void Fuse_BothEmpty_ReturnsEmpty()
    {
        Assert.Empty(RrfFusion.Fuse(new List<RetrievalResult>(), new List<RetrievalResult>()));
    }

    [Fact]
    public void Fuse_DenseChunkWins_AsRepresentative_WhenInBothArms()
    {
        var dense = new List<RetrievalResult> { R("a", 0, score: 0.9, text: "dense-copy") };
        var lexical = new List<RetrievalResult> { R("a", 0, score: 0, text: "lexical-copy") };

        var fused = RrfFusion.Fuse(dense, lexical);

        Assert.Single(fused);
        Assert.Equal("dense-copy", fused[0].Chunk.Text);
    }

    [Fact]
    public void Fuse_EqualScores_TieBreakIsDeterministic()
    {
        // a@dense-rank0 and b@lexical-rank0 both score 1/60. Tie-break on path ordinal.
        var dense = new List<RetrievalResult> { R("b", 0) };
        var lexical = new List<RetrievalResult> { R("a", 0) };

        var fused = RrfFusion.Fuse(dense, lexical);

        Assert.Equal("a", fused[0].Chunk.StoredRelativePath);
        Assert.Equal("b", fused[1].Chunk.StoredRelativePath);
    }
}
