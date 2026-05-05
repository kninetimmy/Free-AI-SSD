using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

public abstract record TranscriptionResult
{
    public sealed record Success(string Text) : TranscriptionResult;
    public sealed record Failure(string ErrorMessage) : TranscriptionResult;
}

/// <summary>
/// Provides speech-to-text transcription using a local Whisper model.
/// </summary>
public interface ISpeechToTextService : IDisposable
{
    event Action<string>? LogMessage;

    /// <summary>Whether the Whisper model is loaded and ready for transcription.</summary>
    bool IsModelLoaded { get; }

    /// <summary>
    /// Loads the Whisper model from the SSD. Must be called before transcription.
    /// </summary>
    /// <param name="ssdRoot">SSD root directory containing the model files.</param>
    /// <param name="config">Portable config with the selected model size.</param>
    Task InitializeAsync(string ssdRoot, PortableConfig config);

    /// <summary>
    /// Transcribes a PCM audio buffer (16kHz, 16-bit, mono) to text.
    /// </summary>
    /// <param name="audioData">Raw PCM audio bytes.</param>
    Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData);

    /// <summary>
    /// Transcribes a PCM audio buffer (16kHz, 16-bit, mono) to text with cancellation support.
    /// </summary>
    Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData, CancellationToken cancellationToken);

    /// <summary>
    /// Transcribes audio from a stream (16kHz, 16-bit, mono PCM).
    /// </summary>
    Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream);

    /// <summary>
    /// Transcribes audio from a stream (16kHz, 16-bit, mono PCM) with cancellation support.
    /// </summary>
    Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream, CancellationToken cancellationToken);
}
