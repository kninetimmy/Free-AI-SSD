using FreeAiSsd.Shared;
using Whisper.net;
using Whisper.net.Ggml;

namespace FreeAiSsd.Runner.Services;

public sealed class WhisperSpeechToTextService : ISpeechToTextService
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly SemaphoreSlim _transcriptionGate = new(1, 1);

    public event Action<string>? LogMessage;
    public bool IsModelLoaded => _processor is not null;

    public async Task InitializeAsync(string ssdRoot, PortableConfig config)
    {
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

    public async Task<string> TranscribeAudioAsync(byte[] audioData)
    {
        if (_processor is null)
            throw new InvalidOperationException("Whisper model is not loaded. Call InitializeAsync first.");

        using var stream = new MemoryStream(audioData);
        return await TranscribeStreamAsync(stream);
    }

    public async Task<string> TranscribeStreamAsync(Stream audioStream)
    {
        if (_processor is null)
            throw new InvalidOperationException("Whisper model is not loaded. Call InitializeAsync first.");

        await _transcriptionGate.WaitAsync();
        try
        {
            // Convert raw PCM (16kHz, 16-bit, mono) to float samples that Whisper expects.
            // Whisper.net ProcessAsync accepts a WAV stream, so we wrap the PCM in a WAV header.
            using var wavStream = WrapPcmInWav(audioStream);
            var segments = new List<string>();

            await foreach (var segment in _processor.ProcessAsync(wavStream))
            {
                var text = segment.Text?.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    segments.Add(text);
                }
            }

            var result = string.Join(" ", segments);
            LogMessage?.Invoke($"Transcribed: {result}");
            return result;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Transcription failed: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            _transcriptionGate.Release();
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

    public void Dispose()
    {
        ReleaseModel();
        _transcriptionGate.Dispose();
    }
}
