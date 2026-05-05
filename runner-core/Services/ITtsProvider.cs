namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Holds the currently-active <see cref="ITextToSpeechService"/>. The TTS engine is
/// re-created on config load / drive unlock, so consumers that outlive a single
/// engine instance (e.g. the background HTTP API) read through this provider
/// rather than capturing a direct reference.
///
/// The writer is the UI layer (<c>MainWindow</c>), which swaps the current engine
/// whenever TTS settings change. Readers may be on any thread.
/// </summary>
public interface ITtsProvider
{
    /// <summary>
    /// The currently-active TTS service, or <c>null</c> if TTS is not configured.
    /// </summary>
    ITextToSpeechService? Current { get; }

    /// <summary>
    /// Replaces the active TTS service. Pass <c>null</c> to clear.
    /// </summary>
    void SetCurrent(ITextToSpeechService? ttsService);
}

/// <summary>
/// Default thread-safe implementation backed by a volatile field. Writers (the UI)
/// own the engine's lifetime and dispose it synchronously on swap, so readers must
/// treat retrieved references as short-lived: an in-flight call racing with a swap
/// may observe <see cref="ObjectDisposedException"/>. Callers should handle that
/// case (e.g. return a transient error) rather than assume the reference stays
/// valid for the duration of their call.
/// </summary>
public sealed class TtsProvider : ITtsProvider
{
    private ITextToSpeechService? _current;

    public ITextToSpeechService? Current => Volatile.Read(ref _current);

    public void SetCurrent(ITextToSpeechService? ttsService)
    {
        Volatile.Write(ref _current, ttsService);
    }
}
