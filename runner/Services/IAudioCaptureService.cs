namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Captures audio from a microphone device for speech-to-text processing.
/// Output format is 16kHz, 16-bit, mono PCM (what Whisper expects).
/// </summary>
public interface IAudioCaptureService : IDisposable
{
    event Action<string>? LogMessage;

    /// <summary>Whether audio is currently being recorded.</summary>
    bool IsRecording { get; }

    /// <summary>
    /// Returns the names of available audio input (microphone) devices.
    /// </summary>
    IReadOnlyList<string> GetAvailableDevices();

    /// <summary>
    /// Starts recording from the specified device (or default if null).
    /// </summary>
    /// <param name="deviceName">Device name, or null for system default.</param>
    void StartRecording(string? deviceName = null);

    /// <summary>
    /// Stops recording and returns the captured audio as 16kHz 16-bit mono PCM bytes.
    /// </summary>
    byte[] StopRecording();
}
