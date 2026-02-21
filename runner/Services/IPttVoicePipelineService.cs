namespace FreeAiSsd.Runner.Services;

/// <summary>
/// The current state of the push-to-talk voice pipeline.
/// </summary>
public enum PttState
{
    Idle,
    Listening,
    Thinking,
    Speaking
}

/// <summary>
/// Orchestrates the full push-to-talk voice pipeline:
/// PTT press → audio capture → Whisper transcription → chat (RAG + LLM) → TTS response.
/// </summary>
public interface IPttVoicePipelineService : IDisposable
{
    event Action<string>? LogMessage;

    /// <summary>Raised when the pipeline transitions between states.</summary>
    event Action<PttState>? StateChanged;

    /// <summary>
    /// Raised when the pipeline produces a transcription from the user's voice.
    /// Useful for displaying what the user said in the main UI.
    /// </summary>
    event Action<string>? TranscriptionReady;

    /// <summary>
    /// Raised when the pipeline receives streamed response text from the LLM.
    /// Useful for displaying the AI response in the main UI.
    /// </summary>
    event Action<string>? ResponseTokenReceived;

    /// <summary>
    /// Raised when the pipeline completes a full response cycle.
    /// Contains the full response text.
    /// </summary>
    event Action<string>? ResponseComplete;

    PttState CurrentState { get; }

    /// <summary>Whether the pipeline is currently processing a query (not idle).</summary>
    bool IsProcessing { get; }

    /// <summary>
    /// Begins audio capture. Called when the PTT button is pressed.
    /// Plays an activation sound if configured.
    /// </summary>
    Task StartListeningAsync();

    /// <summary>
    /// Stops audio capture and runs the full pipeline (transcribe → query → speak).
    /// Called when the PTT button is released.
    /// </summary>
    Task StopListeningAndProcessAsync();

    /// <summary>Cancels any in-progress processing and returns to idle.</summary>
    void Cancel();
}
