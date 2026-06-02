namespace FreeAiSsd.Shared.Documents;

/// <summary>
/// UI-free helpers that turn ingestion outcomes into the short status strings both
/// hosts surface. Kept out of the WPF layer so the formatting is unit-testable.
/// </summary>
public static class IndexingSummary
{
    /// <summary>
    /// Renders the terminal <see cref="IndexingProgress"/> frame (the one with an empty
    /// <see cref="IndexingProgress.CurrentFile"/>) into a completion summary, e.g.
    /// "Indexed 3 files (412 chunks), 5 chunks dropped, 1 skipped (unsupported/oversize)."
    /// </summary>
    public static string Format(IndexingProgress terminal)
    {
        var indexed = terminal.CompletedFiles;
        var dropped = terminal.FailedChunks;
        var skipped = terminal.SkippedFiles ?? 0;
        var fileWord = indexed == 1 ? "file" : "files";

        var summary = $"Indexed {indexed} {fileWord}";
        if (terminal.EmbeddedChunks > 0)
        {
            var chunkWord = terminal.EmbeddedChunks == 1 ? "chunk" : "chunks";
            summary += $" ({terminal.EmbeddedChunks} {chunkWord})";
        }
        if (dropped > 0)
        {
            // Tolerated partial failure: chunks that failed to embed but stayed under the
            // abort threshold, so the file was still indexed without them.
            var chunkWord = dropped == 1 ? "chunk" : "chunks";
            summary += $", {dropped} {chunkWord} dropped";
        }
        if (skipped > 0)
        {
            summary += $", {skipped} skipped (unsupported/oversize)";
        }

        return summary + ".";
    }

    /// <summary>
    /// Turns an ingestion exception into a user-facing cause. <see cref="DocumentIngestor"/>
    /// throws the bare exception for a single-file failure (so its precise message — e.g.
    /// "model 'nomic-embed-text' not found" — is preserved) and an
    /// <see cref="AggregateException"/> when more than one file fails.
    /// </summary>
    public static string DescribeFailure(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
        {
            var first = agg.InnerExceptions[0].Message;
            return $"{agg.InnerExceptions.Count} files failed: {first}";
        }

        return ex.Message;
    }
}
