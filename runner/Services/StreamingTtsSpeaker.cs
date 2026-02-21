using System.Text;
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
        var remaining = _buffer.ToString().Trim();
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

        // Find the last sentence-ending punctuation mark
        int lastEnd = -1;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (IsSentenceEnd(text, i))
            {
                lastEnd = i;
                break;
            }
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

        if (sentences.Length > 0)
        {
            _sentenceQueue.Writer.TryWrite(sentences);
        }
    }

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
