using FreeAiSsd.Runner.Services;

namespace FreeAiSsd.Tests;

/// <summary>
/// Verifies that <see cref="StreamingTtsSpeaker"/> mutes inline citation labels before
/// speech (so the synthesizer never reads "[guide.pdf §Engine Start p.12]" aloud) while
/// leaving plain answers untouched. The bracket-aware sentence splitter must also avoid
/// flushing in the middle of a label, since "p.12" contains a period.
/// </summary>
public class StreamingTtsSpeakerTests
{
    private sealed class RecordingTts : ITextToSpeechService
    {
        public readonly List<string> Spoken = new();
        public event Action<string>? LogMessage;
        public bool IsSpeaking => false;
        public void Speak(string text) { }
        public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            lock (Spoken) { Spoken.Add(text); }
            return Task.CompletedTask;
        }
        public void Stop() { }
        public void SetVoice(string voiceName) { }
        public void SetRate(int rate) { }
        public void SetVolume(int volume) { }
        public IReadOnlyList<string> GetAvailableVoices() => Array.Empty<string>();
        public Task<byte[]?> SynthesizeToWavAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
        public void Dispose() => LogMessage = null;
    }

    private static async Task<string> SpeakAllAsync(RecordingTts tts, StreamingTtsSpeaker speaker)
    {
        speaker.Finish();
        await speaker.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        lock (tts.Spoken) { return string.Join(" ", tts.Spoken); }
    }

    [Fact]
    public async Task Speak_StripsInlineCitationLabels()
    {
        var tts = new RecordingTts();
        using var speaker = new StreamingTtsSpeaker(tts);

        speaker.FeedToken("Pull the throttle to IDLE [FA-18C §Engine Start p.12]. ");
        speaker.FeedToken("Then press the fire button [FA-18C §Weapons p.40]. ");

        var spoken = await SpeakAllAsync(tts, speaker);

        Assert.Contains("Pull the throttle to IDLE", spoken);
        Assert.Contains("Then press the fire button", spoken);
        Assert.DoesNotContain("[", spoken);
        Assert.DoesNotContain("§", spoken);
        Assert.DoesNotContain("p.12", spoken);
        Assert.DoesNotContain("p.40", spoken);
    }

    [Fact]
    public async Task Speak_CitationSplitAcrossTokens_IsStripped()
    {
        var tts = new RecordingTts();
        using var speaker = new StreamingTtsSpeaker(tts);

        // Stream the label one fragment at a time, including the period inside "p.5",
        // to prove the bracket-aware buffer never flushes mid-citation.
        foreach (var t in new[] { "Set flaps to full ", "[FA", "-18C ", "§Flaps", " p.", "5]", ". Done." })
        {
            speaker.FeedToken(t);
        }

        var spoken = await SpeakAllAsync(tts, speaker);

        Assert.Contains("Set flaps to full", spoken);
        Assert.Contains("Done", spoken);
        Assert.DoesNotContain("[", spoken);
        Assert.DoesNotContain("§", spoken);
        Assert.DoesNotContain("p.5", spoken);
    }

    [Fact]
    public async Task Speak_PlainText_WithoutCitations_IsUnchanged()
    {
        var tts = new RecordingTts();
        using var speaker = new StreamingTtsSpeaker(tts);

        speaker.FeedToken("Pull throttle to idle. Press the fire button.");

        var spoken = await SpeakAllAsync(tts, speaker);

        Assert.Contains("Pull throttle to idle.", spoken);
        Assert.Contains("Press the fire button.", spoken);
    }
}
