using System.Text.RegularExpressions;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// MAC31: pure-function helpers for cleaning <c>ollama pull</c> stdout
/// before it reaches the PrepApp UI.
///
/// Ollama's pull TUI uses ANSI cursor-rewrite escapes
/// (<c>\x1b[?25l</c>, <c>\x1b[?25h</c>, <c>\x1b[2K</c>, <c>\x1b[1G</c>,
/// <c>\x1b[A</c>) plus carriage returns to overwrite a single progress
/// line in place when run in a real terminal. <see cref="StreamReader.ReadLineAsync"/>
/// only splits on <c>\n</c>, so a buffered burst of progress ticks comes
/// across as one line carrying embedded <c>\r</c>-separated updates and
/// literal escape codes. Without this filter the PrepApp log pane fills
/// with garbage that looks like a restart loop (the v1.3.10 mac field-test
/// symptom).
///
/// The filter has two responsibilities:
///   1. Strip ANSI CSI sequences and collapse <c>\r</c>-separated updates
///      to the latest segment, so what reaches the UI is human-readable.
///   2. Detect Ollama's progress-line shape (<c>pulling &lt;hash&gt;... NN%</c>)
///      so the caller can route those lines to a single "in-place"
///      progress channel instead of spamming the log.
/// </summary>
public static class OllamaPullProgressFilter
{
    // CSI sequences: ESC [ <optional ?> <params> <final byte>.
    // Ollama uses both private-mode (?25h, ?25l) and standard (2K, 1G, A) forms.
    private static readonly Regex AnsiCsiRegex = new(
        @"\x1b\[[?]?[\d;]*[A-Za-z]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Ollama progress shape: "pulling <12+ hex chars>... NN%"
    // Conservative — falls through to the log channel on shape drift so
    // future Ollama format changes fail open to verbose logging rather
    // than silent loss.
    private static readonly Regex ProgressLineRegex = new(
        @"^pulling\s+[a-f0-9]{6,}\.{3,}\s+\d+%(\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Strips ANSI CSI escape sequences and reduces a multi-update
    /// buffered line to the latest <c>\r</c>-separated segment.
    /// Returns the cleaned line trimmed of surrounding whitespace.
    /// </summary>
    public static string Clean(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var noAnsi = AnsiCsiRegex.Replace(raw, string.Empty);

        // Most TUI progress libraries emit \r-separated rewrites within
        // a single buffered chunk. Take the last non-empty segment so
        // we surface the latest reported state, not the one from the
        // start of the chunk.
        var lastCr = noAnsi.LastIndexOf('\r');
        if (lastCr >= 0)
        {
            noAnsi = noAnsi[(lastCr + 1)..];
        }

        return noAnsi.Trim();
    }

    /// <summary>
    /// Returns true if <paramref name="cleaned"/> looks like Ollama's
    /// per-tick progress display (e.g. <c>pulling abc123def... 43%</c>).
    /// Caller is expected to have run <see cref="Clean"/> first.
    /// </summary>
    public static bool IsProgressLine(string cleaned)
        => !string.IsNullOrEmpty(cleaned) && ProgressLineRegex.IsMatch(cleaned);
}
