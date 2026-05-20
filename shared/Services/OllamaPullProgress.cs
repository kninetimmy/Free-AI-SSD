using System.Text.RegularExpressions;

namespace FreeAiSsd.Shared.Services;

/// <summary>
/// One frame of Ollama's <c>POST /api/pull</c> NDJSON progress stream.
/// Exposes the structured fields directly so callers can render real
/// progress UI (bars, byte counts) without re-parsing strings, and
/// <see cref="ToDisplayString()"/> for callers that just want a single
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
    // Matches Ollama's "pulling <hex>" layer-progress prefix. The hash
    // shown is the blob digest's leading hex chars (typically 12); we
    // accept ≥6 to stay robust against upstream rendering changes. The
    // raw hash is meaningless to end users — task #49 had a user
    // reporting a 6.9 GB "e73cc17c7181" download as "undisclosed", so
    // we rewrite it with the parent model name + a layer counter when
    // the caller supplies them.
    private static readonly Regex PullingHashPrefix = new(
        @"^pulling\s+(?<hash>[0-9a-f]{6,})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Renders the frame as a single line suitable for the PrepApp's
    /// in-place progress label. Layer frames render as
    /// <c>"pulling abcdef… — 83% (3.9 GB / 4.7 GB)"</c>; stage frames
    /// render as the bare status (<c>"verifying sha256 digest"</c>).
    /// </summary>
    public string ToDisplayString() => ToDisplayString(parentModel: null, layerIndex: null, layerCount: null);

    /// <summary>
    /// Same shape as <see cref="ToDisplayString()"/>, but with optional
    /// context that turns Ollama's opaque blob-hash layer labels into
    /// something a user can recognise.
    ///
    /// <para><b>Layer frames</b> — when <paramref name="parentModel"/> is
    /// supplied and the status matches <c>"pulling &lt;hex&gt;"</c>, the
    /// hex is replaced with the parent model name. If
    /// <paramref name="layerIndex"/> and <paramref name="layerCount"/> are
    /// both supplied, a <c>"layer N of M"</c> counter is appended so the
    /// user sees that a multi-blob model is progressing through its
    /// layers rather than starting an "extra" mystery download.</para>
    ///
    /// <para><b>Stage frames</b> (verifying / writing manifest / success)
    /// are prefixed with the parent model when supplied so a tail-of-pull
    /// status line still names which model is being finalised.</para>
    /// </summary>
    public string ToDisplayString(string? parentModel, int? layerIndex, int? layerCount)
    {
        var hasParent = !string.IsNullOrWhiteSpace(parentModel);

        if (Total is > 0 && Completed is >= 0 && Completed.Value <= Total.Value)
        {
            var statusLabel = RewriteLayerStatus(Status, parentModel, layerIndex, layerCount);
            var pct = (double)Completed.Value / Total.Value;
            return $"{statusLabel} — {pct:P0} ({FormatBytes(Completed.Value)} / {FormatBytes(Total.Value)})";
        }

        if (hasParent)
        {
            // Stage frames don't carry a digest; prefix with the parent
            // model so the user knows what's finalising. Leaves "pulling
            // manifest" / "verifying sha256 digest" etc. fully readable.
            return $"{parentModel} — {Status}";
        }

        return Status;
    }

    private static string RewriteLayerStatus(string status, string? parentModel, int? layerIndex, int? layerCount)
    {
        if (string.IsNullOrWhiteSpace(parentModel))
        {
            return status;
        }

        var match = PullingHashPrefix.Match(status);
        if (!match.Success)
        {
            // Not the "pulling <hex>" shape — leave the status verbatim
            // and just lean on the prefix to give context.
            return $"{parentModel} — {status}";
        }

        // "layer 1 of 1" is noise for single-layer models — only show
        // the counter once a second distinct layer has appeared.
        var counter = layerIndex is > 0 && layerCount is > 1
            ? $" (layer {layerIndex} of {layerCount})"
            : string.Empty;

        return $"pulling {parentModel}{counter}";
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
