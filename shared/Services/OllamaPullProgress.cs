namespace FreeAiSsd.Shared.Services;

/// <summary>
/// One frame of Ollama's <c>POST /api/pull</c> NDJSON progress stream.
/// Exposes the structured fields directly so callers can render real
/// progress UI (bars, byte counts) without re-parsing strings, and
/// <see cref="ToDisplayString"/> for callers that just want a single
/// human-readable line.
///
/// The Ollama API contract:
///   <c>{"status":"pulling 96c4...","digest":"sha256:...","total":N,"completed":M}</c>
///   for layer-progress frames; <c>{"status":"verifying sha256 digest"}</c>,
///   <c>{"status":"writing manifest"}</c>, <c>{"status":"success"}</c>, etc.
///   for stage-transition frames (no digest/total/completed).
/// </summary>
public sealed record OllamaPullProgress(string Status, string? Digest, long? Total, long? Completed)
{
    /// <summary>
    /// Renders the frame as a single line suitable for the PrepApp's
    /// in-place progress label. Layer frames render as
    /// <c>"pulling abcdef… — 83% (3.9 GB / 4.7 GB)"</c>; stage frames
    /// render as the bare status (<c>"verifying sha256 digest"</c>).
    /// </summary>
    public string ToDisplayString()
    {
        if (Total is > 0 && Completed is >= 0 && Completed.Value <= Total.Value)
        {
            var pct = (double)Completed.Value / Total.Value;
            return $"{Status} — {pct:P0} ({FormatBytes(Completed.Value)} / {FormatBytes(Total.Value)})";
        }
        return Status;
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024L;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / (double)gb:F1} GB";
        if (bytes >= mb) return $"{bytes / (double)mb:F1} MB";
        if (bytes >= kb) return $"{bytes / (double)kb:F1} KB";
        return $"{bytes} B";
    }
}
