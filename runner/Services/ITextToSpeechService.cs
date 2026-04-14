namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Provides text-to-speech synthesis using a local offline engine.
/// Implementations include Windows SAPI (System.Speech) and Piper TTS.
/// </summary>
public interface ITextToSpeechService : IDisposable
{
    event Action<string>? LogMessage;

    /// <summary>Whether the engine is currently speaking.</summary>
    bool IsSpeaking { get; }

    /// <summary>
    /// Speaks the given text synchronously, blocking until complete or interrupted.
    /// </summary>
    void Speak(string text);

    /// <summary>
    /// Speaks the given text asynchronously without blocking the caller.
    /// </summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Interrupts any speech currently in progress.
    /// </summary>
    void Stop();

    /// <summary>
    /// Selects a voice by name. Use <see cref="GetAvailableVoices"/> to list options.
    /// </summary>
    void SetVoice(string voiceName);

    /// <summary>
    /// Sets the speech rate. Range: -10 (slowest) to 10 (fastest).
    /// </summary>
    void SetRate(int rate);

    /// <summary>
    /// Sets the speech volume. Range: 0 (silent) to 100 (loudest).
    /// </summary>
    void SetVolume(int volume);

    /// <summary>
    /// Returns the names of voices available on the current engine.
    /// </summary>
    IReadOnlyList<string> GetAvailableVoices();

    /// <summary>
    /// Synthesizes the given text to an in-memory WAV (RIFF/WAVE) byte buffer without
    /// playing the audio through any local output device. Returns null if the engine
    /// cannot synthesize (e.g. Piper binary or voice model missing).
    /// </summary>
    Task<byte[]?> SynthesizeToWavAsync(string text, CancellationToken cancellationToken = default);
}
