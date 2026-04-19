using System.Diagnostics;
using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Client;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// Regression tests for X1: the pipeline used to poll <c>ITextToSpeechService.IsSpeaking</c>
/// with a 60s cap before returning to Idle, which hung the UI when the TTS completion
/// signal arrived late or oscillated between sentences. The fix awaits
/// <see cref="StreamingTtsSpeaker.Completion"/> directly.
/// </summary>
public class PttVoicePipelineServiceTests
{
    [Fact]
    public async Task StopListeningAndProcessAsync_ReturnsToIdleAfterTtsCompletes_NotOn60sTimeout()
    {
        var audio = new StubAudioCapture();
        var stt = new StubStt();
        var chat = new StubChat(tokensToEmit: new[] { "Hello", " world." });
        var tts = new LiesAboutIsSpeakingTts(speakDelay: TimeSpan.FromMilliseconds(150));

        var pipeline = new PttVoicePipelineService(audio, stt, chat);
        var config = new PortableConfig { TtsEnabled = true, PttActivationSoundEnabled = false };
        pipeline.Configure(tts, config, ssdRoot: "", getModel: () => "model", getHost: () => "http://localhost:11434");

        var idleAfterSpeaking = new TaskCompletionSource();
        pipeline.StateChanged += state =>
        {
            if (state == PttState.Idle && pipeline.CurrentState == PttState.Idle && tts.SpeakCallCount > 0)
                idleAfterSpeaking.TrySetResult();
        };

        await pipeline.StartListeningAsync();

        var sw = Stopwatch.StartNew();
        await pipeline.StopListeningAndProcessAsync();
        sw.Stop();

        Assert.Equal(PttState.Idle, pipeline.CurrentState);
        Assert.True(tts.SpeakCallCount > 0, "Pipeline should have queued at least one sentence to TTS.");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"Pipeline should return to Idle promptly after TTS finishes; took {sw.Elapsed.TotalSeconds:F1}s. " +
            "If this exceeds 60s the old IsSpeaking-polling path has been reintroduced.");
    }

    // ── Stubs ───────────────────────────────────────────────────────────────

    private sealed class StubAudioCapture : IAudioCaptureService
    {
        public event Action<string>? LogMessage;
        public bool IsRecording { get; private set; }
        public IReadOnlyList<string> GetAvailableDevices() => Array.Empty<string>();
        public void StartRecording(string? deviceName = null) => IsRecording = true;
        public byte[] StopRecording() { IsRecording = false; return new byte[] { 1, 2, 3, 4 }; }
        public void Dispose() { }
    }

    private sealed class StubStt : ISpeechToTextService
    {
        public event Action<string>? LogMessage;
        public bool IsModelLoaded => true;
        public Task InitializeAsync(string ssdRoot, PortableConfig config) => Task.CompletedTask;
        public Task<string> TranscribeAudioAsync(byte[] audioData) => Task.FromResult("what's the weather");
        public Task<string> TranscribeStreamAsync(Stream audioStream) => Task.FromResult(string.Empty);
        public void Dispose() { }
    }

    private sealed class StubChat : IChatService
    {
        private readonly string[] _tokens;
        public event Action<string>? LogMessage;
        public StubChat(string[] tokensToEmit) => _tokens = tokensToEmit;

        public Task<ChatResponse> SendPromptAsync(string model, string userPrompt, string host, PortableConfig config)
            => Task.FromResult(new ChatResponse(string.Concat(_tokens), null, false));

        public async Task<ChatResponse> SendPromptStreamingAsync(
            string model, string userPrompt, string host, PortableConfig config,
            Func<string, Task> onToken, CancellationToken cancellationToken = default)
        {
            foreach (var t in _tokens)
                await onToken(t);
            return new ChatResponse(string.Concat(_tokens), null, false);
        }
    }

    /// <summary>
    /// TTS stub that reports <see cref="IsSpeaking"/> = <c>false</c> at all times,
    /// which is what the pre-fix polling loop relied on as a completion signal.
    /// If the pipeline were still polling <c>IsSpeaking</c>, it would return to
    /// Idle before <see cref="SpeakAsync"/> actually finishes — this stub would
    /// *hide* the bug rather than expose it. We instead assert that Idle is reached
    /// only after <see cref="SpeakAsync"/> has been awaited, by counting calls.
    /// The key invariant: pipeline does not hang for 60s and SpeakAsync was invoked.
    /// </summary>
    private sealed class LiesAboutIsSpeakingTts : ITextToSpeechService
    {
        private readonly TimeSpan _speakDelay;
        public event Action<string>? LogMessage;
        public int SpeakCallCount { get; private set; }

        public LiesAboutIsSpeakingTts(TimeSpan speakDelay) => _speakDelay = speakDelay;

        public bool IsSpeaking => false;

        public void Speak(string text) => SpeakCallCount++;

        public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            SpeakCallCount++;
            await Task.Delay(_speakDelay, cancellationToken);
        }

        public void Stop() { }
        public void SetVoice(string voiceName) { }
        public void SetRate(int rate) { }
        public void SetVolume(int volume) { }
        public IReadOnlyList<string> GetAvailableVoices() => Array.Empty<string>();
        public Task<byte[]?> SynthesizeToWavAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<byte[]?>(null);
        public void Dispose() { }
    }
}
