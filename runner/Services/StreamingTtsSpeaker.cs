using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Buffers streaming LLM tokens, splits them into sentences at punctuation
/// boundaries, and speaks each sentence through an <see cref="ITextToSpeechService"/>
/// as soon as it completes. This gives the user immediate audio feedback while
/// the rest of the response is still generating.
///
/// Usage:
///   1. Create an instance per streaming response.
///   2. Call <see cref="FeedToken"/> from the streaming callback for every token.
///   3. Call <see cref="Finish"/> when the stream ends so any trailing text is spoken.
///   4. Call <see cref="Cancel"/> if the user interrupts (starts a new query, etc.).
///   5. Await <see cref="Completion"/> to know when all queued speech has finished.
/// </summary>
public sealed class StreamingTtsSpeaker : IDisposable
{
    private static readonly char[] SentenceEnders = { '.', '!', '?', '\n' };

    /// <summary>
    /// Matches an inline citation label so it can be muted before speech. Labels are produced
    /// by <c>CitationBuilder</c> as <c>[file §Section p.N]</c> (section and page optional), so a
    /// bracketed span is treated as a citation when it carries a section marker, a "p.N" page
    /// ref, or a known source-file extension. The leading whitespace is consumed too so removing
    /// the label doesn't leave a double space. No-op when inline citations are off (none present).
    /// </summary>
    private static readonly Regex CitationSpanRx = new(
        @"\s*\[[^\]]*(?:§|\bp\.\s*\d|\.(?:pdf|txt|md|markdown|csv|json)\b)[^\]]*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ITextToSpeechService _tts;
    private readonly Channel<string> _sentenceQueue = Channel.CreateUnbounded<string>();
    private readonly StringBuilder _buffer = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _speakLoop;

    public StreamingTtsSpeaker(ITextToSpeechService tts)
    {
        _tts = tts;
        _speakLoop = Task.Run(SpeakLoopAsync);
    }

    /// <summary>
    /// A task that completes when all queued sentences have been spoken or
    /// the speaker has been cancelled/disposed.
    /// </summary>
    public Task Completion => _speakLoop;

    /// <summary>
    /// Feed an incremental token from the streaming LLM response.
    /// Whenever a sentence-ending punctuation mark is found, the buffered
    /// sentence is flushed into the speech queue.
    /// </summary>
    public void FeedToken(string token)
    {
        if (_cts.IsCancellationRequested) return;

        _buffer.Append(token);

        // Check if the buffer now ends with sentence-ending punctuation
        // (possibly followed by whitespace or quotes).
        FlushCompleteSentences();
    }

    /// <summary>
    /// Signal that no more tokens will arrive. Flushes any remaining buffered
    /// text as a final sentence and closes the speech queue.
    /// </summary>
    public void Finish()
    {
        var remaining = StripCitations(_buffer.ToString()).Trim();
        _buffer.Clear();

        if (remaining.Length > 0)
        {
            _sentenceQueue.Writer.TryWrite(remaining);
        }

        _sentenceQueue.Writer.TryComplete();
    }

    /// <summary>
    /// Immediately stops speech and cancels any pending sentences.
    /// </summary>
    public void Cancel()
    {
        _cts.Cancel();
        _sentenceQueue.Writer.TryComplete();
        _tts.Stop();
    }

    private void FlushCompleteSentences()
    {
        var text = _buffer.ToString();

        // Find the last sentence-ender that is NOT inside a citation bracket. The label
        // format embeds periods (e.g. "p.12"), so a naive scan would split a citation in
        // half; tracking bracket depth keeps each "[...]" label intact until it is whole
        // and a real sentence boundary follows, at which point StripCitations removes it.
        // With no brackets present (citations off) depth stays 0 and this matches the
        // previous "last sentence-ender wins" behaviour exactly.
        int lastEnd = -1;
        int depth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[') depth++;
            else if (c == ']') { if (depth > 0) depth--; }
            else if (depth == 0 && IsSentenceEnd(text, i)) lastEnd = i;
        }

        if (lastEnd < 0) return;

        // Extract everything up to and including the sentence-ender.
        // Skip past any trailing whitespace/quotes to include them with the sentence.
        int cutIndex = lastEnd + 1;
        while (cutIndex < text.Length && (char.IsWhiteSpace(text[cutIndex]) || text[cutIndex] == '"' || text[cutIndex] == '\''))
        {
            cutIndex++;
        }

        var sentences = text[..cutIndex].Trim();
        var remainder = text[cutIndex..];

        _buffer.Clear();
        _buffer.Append(remainder);

        var speakable = StripCitations(sentences).Trim();
        if (speakable.Length > 0)
        {
            _sentenceQueue.Writer.TryWrite(speakable);
        }
    }

    /// <summary>
    /// Removes inline citation labels (and the space preceding them) so the synthesizer
    /// never reads "[guide.pdf §Engine Start p.12]" aloud. A no-op when no labels are present.
    /// </summary>
    private static string StripCitations(string text) =>
        CitationSpanRx.Replace(text, string.Empty);

    /// <summary>
    /// Returns true if the character at <paramref name="index"/> is a sentence-ending
    /// punctuation mark. Avoids false positives on common abbreviations and decimals.
    /// </summary>
    private static bool IsSentenceEnd(string text, int index)
    {
        var ch = text[index];
        if (ch == '!' || ch == '?' || ch == '\n')
            return true;

        if (ch != '.')
            return false;

        // Skip periods that look like decimal numbers (e.g., "3.14")
        if (index > 0 && char.IsDigit(text[index - 1]) &&
            index + 1 < text.Length && char.IsDigit(text[index + 1]))
            return false;

        // Skip periods followed by a letter with no space (abbreviations like "e.g.")
        if (index + 1 < text.Length && char.IsLetter(text[index + 1]))
            return false;

        return true;
    }

    private async Task SpeakLoopAsync()
    {
        try
        {
            await foreach (var sentence in _sentenceQueue.Reader.ReadAllAsync(_cts.Token))
            {
                if (_cts.IsCancellationRequested) break;
                await _tts.SpeakAsync(sentence, _cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Cancel()
        }
    }

    public void Dispose()
    {
        Cancel();
        _cts.Dispose();
    }
}
