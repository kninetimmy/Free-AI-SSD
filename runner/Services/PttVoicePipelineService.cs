using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Orchestrates the full push-to-talk voice pipeline:
///
///   Idle → [PTT press] → play activation beep → start recording → Listening
///   Listening → [PTT release] → stop recording → Thinking
///   Thinking → transcribe (Whisper) → send to chat (RAG + LLM streaming) → Speaking
///   Speaking → TTS completes → play end beep → Idle
///
/// Integrates with the existing AudioCaptureService, ISpeechToTextService,
/// IChatService, and ITextToSpeechService. Does not own or dispose those services.
/// </summary>
public sealed class PttVoicePipelineService : IPttVoicePipelineService
{
    private readonly IAudioCaptureService _audioCapture;
    private readonly ISpeechToTextService _stt;
    private readonly IChatService _chat;
    private readonly object _stateLock = new();

    // These are set via Configure() because TTS may be re-created after config changes
    private ITextToSpeechService? _tts;
    private PortableConfig? _config;
    private string _ssdRoot = string.Empty;
    private Func<string>? _getModel;
    private Func<string?>? _getHost;

    private PttState _currentState = PttState.Idle;
    private CancellationTokenSource? _processingCts;

    public event Action<string>? LogMessage;
    public event Action<PttState>? StateChanged;
    public event Action<string>? TranscriptionReady;
    public event Action<string>? ResponseTokenReceived;
    public event Action<string>? ResponseComplete;

    public PttState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
    }

    public bool IsProcessing
    {
        get { lock (_stateLock) return _currentState != PttState.Idle; }
    }

    public PttVoicePipelineService(
        IAudioCaptureService audioCapture,
        ISpeechToTextService stt,
        IChatService chat)
    {
        _audioCapture = audioCapture;
        _stt = stt;
        _chat = chat;
    }

    /// <summary>
    /// Configures the pipeline with the current TTS service and config.
    /// Called whenever the config or TTS service changes.
    /// </summary>
    public void Configure(
        ITextToSpeechService? tts,
        PortableConfig config,
        string ssdRoot,
        Func<string> getModel,
        Func<string?> getHost)
    {
        _tts = tts;
        _config = config;
        _ssdRoot = ssdRoot;
        _getModel = getModel;
        _getHost = getHost;
    }

    public async Task StartListeningAsync()
    {
        if (_config is null)
        {
            Log("PTT pipeline not configured.");
            return;
        }

        lock (_stateLock)
        {
            if (_currentState == PttState.Listening)
                return; // Already listening (toggle mode double-press guard)

            if (_currentState != PttState.Idle)
            {
                Log("Still processing previous query.");
                if (_config.PttActivationSoundEnabled)
                    PttSounds.PlayAsync(PttSounds.GetErrorBeep(), _config.TtsOutputDevice);
                SpeakFeedback("Still processing, please wait.");
                return;
            }
        }

        // Ensure Whisper model is loaded
        if (!_stt.IsModelLoaded)
        {
            Log("Loading Whisper model for PTT...");
            try
            {
                await _stt.InitializeAsync(_ssdRoot, _config);
            }
            catch (Exception ex)
            {
                Log($"Failed to load Whisper model: {ex.Message}");
                SpeakFeedback("Voice input unavailable. Whisper model failed to load.");
                return;
            }
        }

        // Play activation sound
        if (_config.PttActivationSoundEnabled)
            PttSounds.PlayAsync(PttSounds.GetActivationBeep(), _config.TtsOutputDevice);

        // Start recording
        try
        {
            _audioCapture.StartRecording(_config.SelectedMicrophoneDevice);
            SetState(PttState.Listening);
            Log("PTT listening...");
        }
        catch (Exception ex)
        {
            Log($"Microphone error: {ex.Message}");
            SpeakFeedback("Microphone error.");
            SetState(PttState.Idle);
        }
    }

    public async Task StopListeningAndProcessAsync()
    {
        if (_config is null) return;

        lock (_stateLock)
        {
            if (_currentState != PttState.Listening) return;
        }

        // Stop recording
        byte[] audioData;
        try
        {
            audioData = _audioCapture.StopRecording();
        }
        catch (Exception ex)
        {
            Log($"Failed to stop recording: {ex.Message}");
            SetState(PttState.Idle);
            return;
        }

        if (audioData.Length == 0)
        {
            Log("No audio captured.");
            SetState(PttState.Idle);
            return;
        }

        // Play deactivation sound
        if (_config.PttActivationSoundEnabled)
            PttSounds.PlayAsync(PttSounds.GetDeactivationBeep(), _config.TtsOutputDevice);

        // Begin processing
        _processingCts?.Cancel();
        _processingCts = new CancellationTokenSource();
        var ct = _processingCts.Token;

        SetState(PttState.Thinking);
        Log("Transcribing...");

        // Transcribe
        string transcription;
        try
        {
            transcription = await _stt.TranscribeAudioAsync(audioData);
        }
        catch (Exception ex)
        {
            Log($"Transcription failed: {ex.Message}");
            SpeakFeedback("Sorry, didn't catch that.");
            SetState(PttState.Idle);
            return;
        }

        if (string.IsNullOrWhiteSpace(transcription))
        {
            Log("No speech detected.");
            SpeakFeedback("Sorry, didn't catch that.");
            SetState(PttState.Idle);
            return;
        }

        Log($"Transcribed: \"{transcription}\"");
        TranscriptionReady?.Invoke(transcription);

        // Get the current model and host
        var model = _getModel?.Invoke();
        var host = _getHost?.Invoke();
        if (string.IsNullOrEmpty(model) || string.IsNullOrEmpty(host))
        {
            Log("Ollama not running or no model selected. Cannot process query.");
            SpeakFeedback("AI is not running. Please start Ollama first.");
            SetState(PttState.Idle);
            return;
        }

        if (ct.IsCancellationRequested)
        {
            SetState(PttState.Idle);
            return;
        }

        // Send to chat pipeline with streaming
        SetState(PttState.Speaking);
        Log("Querying AI...");

        StreamingTtsSpeaker? ttsSpeaker = null;
        if (_tts is not null && _config.TtsEnabled)
        {
            ttsSpeaker = new StreamingTtsSpeaker(_tts);
        }

        try
        {
            var response = await _chat.SendPromptStreamingAsync(
                model, transcription, host, _config,
                token =>
                {
                    ttsSpeaker?.FeedToken(token);
                    ResponseTokenReceived?.Invoke(token);
                    return Task.CompletedTask;
                },
                ct);

            ttsSpeaker?.Finish();
            ResponseComplete?.Invoke(response.ResponseText);
            Log("Response complete.");
        }
        catch (OperationCanceledException)
        {
            ttsSpeaker?.Cancel();
            Log("PTT query cancelled.");
        }
        catch (Exception ex)
        {
            ttsSpeaker?.Cancel();
            Log($"Chat error: {ex.Message}");
            SpeakFeedback("Sorry, there was an error processing your question.");
        }
        finally
        {
            if (ttsSpeaker is not null)
            {
                // Completion resolves when the sentence queue drains and every
                // queued SpeakAsync has returned. SpeakAsync itself returns only
                // after PlaybackStopped fires, so this is the event-driven signal
                // that speech is fully finished. Cancel() in the catch blocks
                // above also completes this task.
                try { await ttsSpeaker.Completion; }
                catch { /* Cancel()/Dispose surface as TaskCanceled; ignore */ }
                ttsSpeaker.Dispose();
            }

            SetState(PttState.Idle);
        }
    }

    public void Cancel()
    {
        _processingCts?.Cancel();

        if (_audioCapture.IsRecording)
        {
            try { _audioCapture.StopRecording(); } catch { }
        }

        _tts?.Stop();
        SetState(PttState.Idle);
        Log("PTT pipeline cancelled.");
    }

    public void Dispose()
    {
        _processingCts?.Cancel();
        _processingCts?.Dispose();
    }

    // ─── Private ────────────────────────────────────────────────────────────

    private void SetState(PttState newState)
    {
        lock (_stateLock) _currentState = newState;
        StateChanged?.Invoke(newState);
    }

    private void SpeakFeedback(string text)
    {
        if (_tts is null || _config is not { TtsEnabled: true }) return;
        try { _ = _tts.SpeakAsync(text); }
        catch { /* non-critical */ }
    }

    private void Log(string message) => LogMessage?.Invoke($"[PTT] {message}");
}
