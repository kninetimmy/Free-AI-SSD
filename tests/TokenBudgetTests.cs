using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public class TokenBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(8)]
    public void EstimateTokens_WholeMultiplesOfRatio_DividesExactly(int chars)
    {
        Assert.Equal(chars / 4, TokenBudget.EstimateTokens(new string('a', chars)));
    }

    [Fact]
    public void EstimateTokens_RoundsUp_ForPartialToken()
    {
        // 5 chars / 4 chars-per-token = 1.25 → 2 tokens (never under-count).
        Assert.Equal(2, TokenBudget.EstimateTokens(new string('a', 5)));
    }

    [Fact]
    public void CharsForTokens_MultipliesByRatio()
    {
        Assert.Equal(400, TokenBudget.CharsForTokens(100));
        Assert.Equal(0, TokenBudget.CharsForTokens(0));
    }

    [Fact]
    public void CharsForTokens_OfEstimate_NeverUnderRepresentsText()
    {
        // The char budget derived from an estimate must be >= the text length, so a
        // chunk that was measured as fitting can never be silently truncated.
        var text = new string('x', 1234);
        Assert.True(TokenBudget.CharsForTokens(TokenBudget.EstimateTokens(text)) >= text.Length);
    }

    [Fact]
    public void ContextTokenBudget_UnsetWindow_UsesDefaults()
    {
        // window=4096, reservedOutput=1024, overhead=256 → (4096-1024-256)*0.9 = 2534.
        Assert.Equal(2534, TokenBudget.ContextTokenBudget(0, 0));
    }

    [Fact]
    public void ContextTokenBudget_NegativeOutputCap_TreatedAsUnbounded()
    {
        // ModelMaxOutputTokens sentinel of -1 reserves DefaultReservedOutputTokens.
        Assert.Equal(TokenBudget.ContextTokenBudget(0, 0), TokenBudget.ContextTokenBudget(0, -1));
    }

    [Theory]
    [InlineData(2048, 691)]   // (2048-1024-256)*0.9 = 691
    [InlineData(4096, 2534)]  // (4096-1024-256)*0.9 = 2534
    [InlineData(8192, 6220)]  // (8192-1024-256)*0.9 = 6220
    public void ContextTokenBudget_ScalesWithWindow(int window, int expected)
    {
        Assert.Equal(expected, TokenBudget.ContextTokenBudget(window, -1));
    }

    [Fact]
    public void ContextTokenBudget_LargerWindow_YieldsLargerBudget()
    {
        var small = TokenBudget.ContextTokenBudget(2048, -1);
        var medium = TokenBudget.ContextTokenBudget(4096, -1);
        var large = TokenBudget.ContextTokenBudget(8192, -1);
        Assert.True(small < medium && medium < large);
    }

    [Fact]
    public void ContextTokenBudget_CappingOutput_FreesContextRoom()
    {
        // A tighter output cap leaves more of the window for reference context.
        var unbounded = TokenBudget.ContextTokenBudget(4096, -1);
        var capped = TokenBudget.ContextTokenBudget(4096, 256);
        Assert.True(capped > unbounded);
    }

    [Fact]
    public void ContextTokenBudget_TinyWindow_FlooredAtMinimum()
    {
        // A window smaller than the reservations would compute negative; the floor wins.
        Assert.Equal(TokenBudget.MinimumContextTokens, TokenBudget.ContextTokenBudget(1, -1));
    }
}
