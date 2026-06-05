using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;

namespace FreeAiSsd.MacRunnerHost;

// Task #106. The Mac sidecar's ITextToSpeechService. Only the WAV round-trip is
// supported: SynthesizeToWavAsync ships the text to the Swift parent's
// AVSpeechSynthesizer (decision #166), which renders it to a WAV and returns the
// bytes — the companion plays them on the VR machine (returnAudio=true path).
//
// Host-side speak-aloud (returnAudio=false → SpeakAsync) is intentionally NOT
// supported on the Mac sidecar: the sidecar is a headless process with no audio
// session, and speaking on the Mac box is not what the VR companion wants. It
// throws so RunnerLocalApiService's fire-and-forget host-TTS branch logs a clean
// failure instead of hanging.
internal sealed class RpcTextToSpeechService : ITextToSpeechService
{
    private const string SpeakAloudUnsupported =
        "Host-side speech playback is not supported on the Mac sidecar; request returnAudio to receive WAV.";

    private readonly VoiceRpcChannel _channel;
    private string? _voiceId;
    private int _rate;
    private int _volume;

    public event Action<string>? LogMessage;

    public RpcTextToSpeechService(VoiceRpcChannel channel, PortableConfig config)
    {
        _channel = channel;
        _voiceId = string.IsNullOrWhiteSpace(config.TtsVoiceName) ? null : config.TtsVoiceName;
        _rate = config.TtsRate;
        _volume = config.TtsVolume;
    }

    public bool IsSpeaking => false;

    public void Speak(string text) => throw new NotSupportedException(SpeakAloudUnsupported);

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(SpeakAloudUnsupported);

    public void Stop()
    {
    }

    public void SetVoice(string voiceName) => _voiceId = string.IsNullOrWhiteSpace(voiceName) ? null : voiceName;

    public void SetRate(int rate) => _rate = rate;

    public void SetVolume(int volume) => _volume = volume;

    public IReadOnlyList<string> GetAvailableVoices() => Array.Empty<string>();

    public async Task<byte[]?> SynthesizeToWavAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _channel.SendTtsRequestAsync(text, _voiceId, _rate, _volume, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(response.Error))
            {
                LogMessage?.Invoke($"TTS RPC failed: {response.Error}");
                return null;
            }
            if (string.IsNullOrEmpty(response.WavBase64))
            {
                return null;
            }
            return Convert.FromBase64String(response.WavBase64);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"TTS RPC failed: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
    }
}
