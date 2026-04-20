using FreeAiSsd.Shared;
using Whisper.net;
using Whisper.net.Ggml;

namespace FreeAiSsd.Runner.Services;

public sealed class WhisperSpeechToTextService : ISpeechToTextService
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;

    // Single service-level gate that serializes Initialize, Transcribe, and Dispose.
    // The singleton is shared by MainWindow (voice button), PttVoicePipelineService
    // (HOTAS PTT), and RunnerLocalApiService (network API); without this gate
    // concurrent first-use calls could overwrite _factory/_processor mid-load or
    // a failing initializer's catch could tear down a model another caller just
    // finished building.
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();
    private volatile bool _disposed;

    public event Action<string>? LogMessage;
    public bool IsModelLoaded => _processor is not null;

    public async Task InitializeAsync(string ssdRoot, PortableConfig config)
    {
        ThrowIfDisposed();

        await _lifecycleGate.WaitAsync(_shutdownCts.Token);
        try
        {
            ThrowIfDisposed();
            ReleaseModel();

            var modelPath = WhisperModelManager.GetModelPath(ssdRoot, config.WhisperModelSize);
            if (!File.Exists(modelPath))
            {
                LogMessage?.Invoke($"Whisper model not found at {modelPath}. Downloading...");
                try
                {
                    await WhisperModelManager.EnsureModelDownloadedAsync(ssdRoot, config.WhisperModelSize,
                        progress => LogMessage?.Invoke(progress));
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"Failed to download Whisper model: {ex.Message}");
                    throw;
                }
            }

            try
            {
                _factory = WhisperFactory.FromPath(modelPath);
                _processor = _factory.CreateBuilder()
                    .WithLanguage("en")
                    .Build();
                LogMessage?.Invoke($"Whisper model loaded: {config.WhisperModelSize}");
            }
            catch (Exception ex)
            {
                ReleaseModel();
                if (ex is OutOfMemoryException)
                {
                    LogMessage?.Invoke("Out of memory loading Whisper model. Try a smaller model size.");
                }
                else
                {
                    LogMessage?.Invoke($"Failed to load Whisper model: {ex.Message}");
                }
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData)
        => TranscribeAudioAsync(audioData, CancellationToken.None);

    public async Task<TranscriptionResult> TranscribeAudioAsync(byte[] audioData, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(audioData);
        return await TranscribeStreamAsync(stream, cancellationToken);
    }

    public Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream)
        => TranscribeStreamAsync(audioStream, CancellationToken.None);

    public async Task<TranscriptionResult> TranscribeStreamAsync(Stream audioStream, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdownCts.Token);

        await _lifecycleGate.WaitAsync(linkedCts.Token);
        try
        {
            ThrowIfDisposed();
            if (_processor is null)
                throw new InvalidOperationException("Whisper model is not loaded. Call InitializeAsync first.");

            // Convert raw PCM (16kHz, 16-bit, mono) to float samples that Whisper expects.
            // Whisper.net ProcessAsync accepts a WAV stream, so we wrap the PCM in a WAV header.
            using var wavStream = WrapPcmInWav(audioStream);
            var segments = new List<string>();

            await foreach (var segment in _processor.ProcessAsync(wavStream).WithCancellation(linkedCts.Token))
            {
                var text = segment.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    segments.Add(text);
                }
            }

            var result = string.Join(" ", segments);
            LogMessage?.Invoke($"Transcribed: {result}");
            return new TranscriptionResult.Success(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Transcription failed: {ex.Message}");
            return new TranscriptionResult.Failure($"Transcription failed: {ex.Message}");
        }
        finally
        {
            // If Dispose timed out waiting for this call to drain and proceeded
            // with teardown, the gate may already be disposed. Swallow the noise —
            // the caller still gets the original cancellation/result.
            try { _lifecycleGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Wraps raw 16kHz 16-bit mono PCM data in a minimal WAV header
    /// so that Whisper.net can process it.
    /// </summary>
    private static MemoryStream WrapPcmInWav(Stream pcmStream)
    {
        using var reader = new BinaryReader(pcmStream, System.Text.Encoding.UTF8, leaveOpen: true);
        var pcmData = reader.ReadBytes((int)(pcmStream.Length - pcmStream.Position));
        return WrapPcmBytesInWav(pcmData);
    }

    internal static MemoryStream WrapPcmBytesInWav(byte[] pcmData)
    {
        const int sampleRate = 16000;
        const short bitsPerSample = 16;
        const short channels = 1;
        short blockAlign = (short)(channels * bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;

        var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);

        // RIFF header
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length); // ChunkSize
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt sub-chunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);              // SubChunk1Size (PCM)
        writer.Write((short)1);        // AudioFormat (PCM)
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        // data sub-chunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);

        ms.Position = 0;
        return ms;
    }

    private void ReleaseModel()
    {
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(WhisperSpeechToTextService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Signal any in-flight transcription (via ProcessAsync's cancellation token)
        // and any queued Init/Transcribe waiters to unblock.
        try { _shutdownCts.Cancel(); }
        catch (ObjectDisposedException) { }

        // Wait for any in-flight work to drain. Bounded so a wedged Whisper
        // call cannot block app shutdown indefinitely.
        var acquired = false;
        try { acquired = _lifecycleGate.Wait(TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { }

        try
        {
            ReleaseModel();
        }
        finally
        {
            if (acquired)
            {
                try { _lifecycleGate.Release(); } catch (ObjectDisposedException) { }
            }
            _lifecycleGate.Dispose();
            _shutdownCts.Dispose();
        }
    }
}
