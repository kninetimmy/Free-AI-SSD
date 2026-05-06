namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Cross-platform no-op <see cref="ITtsProvider"/> whose <see cref="Current"/>
/// is always <c>null</c>. <see cref="RunnerLocalApiService"/> already returns
/// 503 from <c>/tts/speak</c> and <c>/tts/stop</c> when the current TTS service
/// is null, so wiring this provider produces the correct behavior for hosts
/// without a real TTS backend (e.g., the Mac runner sidecar in MAC6).
/// </summary>
public sealed class NoOpTtsProvider : ITtsProvider
{
    public ITextToSpeechService? Current => null;

    public void SetCurrent(ITextToSpeechService? ttsService)
    {
        // Intentionally a no-op. The Mac sidecar host treats TTS as unavailable;
        // accepting a service from the UI layer would be a category error here.
    }
}
