namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// Heuristic token accounting for RAG prompt packing. The runner serves arbitrary
/// GGUF models via Ollama whose tokenizers we don't ship, and Ollama exposes no
/// pre-flight token-count endpoint, so exact counts are unattainable — any tokenizer
/// we added would itself be an approximation that still needed a safety margin. We
/// estimate with a conservative chars-per-token ratio and reserve headroom so the
/// packed prompt stays comfortably inside the model's context window. The real win
/// over the old fixed character budget is that the budget now <em>scales with the
/// active model's window</em> rather than being a constant.
/// </summary>
public static class TokenBudget
{
    /// <summary>
    /// Average characters per token for English-ish text. ~4 is the common
    /// rule of thumb across BPE/SentencePiece vocabularies — close enough given we
    /// then apply <see cref="SafetyFactor"/> for slack.
    /// </summary>
    public const double CharsPerToken = 4.0;

    /// <summary>
    /// Assumed context window (tokens) when the user hasn't pinned one
    /// (<c>ModelContextWindow == 0</c> → Ollama omits <c>num_ctx</c> and uses the
    /// model's compiled-in default, which we can't read; 4096 is the current Ollama
    /// default). If this assumption overshoots a model's real window Ollama truncates
    /// the prompt gracefully rather than erroring.
    /// </summary>
    public const int DefaultContextWindowTokens = 4096;

    /// <summary>
    /// Output tokens reserved for the answer when the user hasn't capped output
    /// (<c>ModelMaxOutputTokens &lt;= 0</c> → unbounded <c>num_predict</c>).
    /// </summary>
    public const int DefaultReservedOutputTokens = 1024;

    /// <summary>
    /// Fixed token allowance for the grounding instruction block, the user question,
    /// and the formatting that wraps the reference context.
    /// </summary>
    public const int PromptOverheadTokens = 256;

    /// <summary>
    /// Fraction of the computed window we actually fill, leaving slack for estimation
    /// error (we under-count tokens for code and non-English text).
    /// </summary>
    public const double SafetyFactor = 0.9;

    /// <summary>
    /// Floor on the context budget so a tiny or misconfigured window still admits at
    /// least one chunk attempt rather than collapsing to zero.
    /// </summary>
    public const int MinimumContextTokens = 256;

    /// <summary>Estimated token count for a piece of text.</summary>
    public static int EstimateTokens(string text)
        => (int)Math.Ceiling(text.Length / CharsPerToken);

    /// <summary>
    /// Token budget available for retrieved reference context, given the configured
    /// model window and output reservation. Both arguments accept the
    /// <see cref="PortableConfig"/> sentinels: a window of 0 falls back to
    /// <see cref="DefaultContextWindowTokens"/> and an output cap of &lt;= 0 falls back
    /// to <see cref="DefaultReservedOutputTokens"/>. Floored at
    /// <see cref="MinimumContextTokens"/>.
    /// </summary>
    public static int ContextTokenBudget(int modelContextWindowTokens, int maxOutputTokens)
    {
        var window = modelContextWindowTokens > 0 ? modelContextWindowTokens : DefaultContextWindowTokens;
        var reservedOutput = maxOutputTokens > 0 ? maxOutputTokens : DefaultReservedOutputTokens;
        var available = (int)((window - reservedOutput - PromptOverheadTokens) * SafetyFactor);
        return Math.Max(available, MinimumContextTokens);
    }

    /// <summary>
    /// Converts a token budget to an equivalent character budget for the char-based
    /// packer in <see cref="RagPromptBuilder"/>. With a constant
    /// <see cref="CharsPerToken"/> ratio, packing by this character budget yields the
    /// same inclusion decisions as packing by the token budget directly.
    /// </summary>
    public static int CharsForTokens(int tokens)
        => (int)(tokens * CharsPerToken);
}
