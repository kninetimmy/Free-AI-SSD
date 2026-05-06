using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Cross-platform no-op <see cref="ISpeechToTextService"/>. Used by hosts that
/// do not ship a Whisper backend (e.g., the Mac runner sidecar in MAC6) so the
/// LAN API surface still constructs cleanly. <c>/api/stt/transcribe</c> and
/// <c>/api/voice/query</c> are gated by <see cref="PortableConfig.NetworkAllowRemoteStt"/>
/// and <see cref="PortableConfig.NetworkAllowRemoteVoiceQuery"/>; when those flags
/// are off this service is never invoked. When a user explicitly enables the
/// flags on a host wired with this no-op, the route surfaces a 503/500 instead
/// of pretending to transcribe.
/// </summary>
public sealed class NoOpSpeechToTextService : ISpeechToTextService
{
    private const string UnavailableMessage = "STT is not available on this platform.";

    public event Action<string>? LogMessage;

    public bool IsModelLoaded => false;

    public Task InitializeAsync(string ssdRoot, PortableConfig config)
    {
        LogMessage?.Invoke(UnavailableMessage);
        throw new InvalidOperationException(UnavailableMessage);
    }

    public Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData)
        => TranscribeAudioAsync(audioData, CancellationToken.None);

    public Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData, CancellationToken cancellationToken)
        => Task.FromResult<TranscriptionResult>(new TranscriptionResult.Failure(UnavailableMessage));

    public Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream)
        => Task.FromResult<TranscriptionResult>(new TranscriptionResult.Failure(UnavailableMessage));

    public Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream, CancellationToken cancellationToken)
        => Task.FromResult<TranscriptionResult>(new TranscriptionResult.Failure(UnavailableMessage));

    public void Dispose()
    {
    }
}
