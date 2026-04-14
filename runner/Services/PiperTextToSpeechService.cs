using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Text-to-speech using Piper, a fast local neural TTS engine.
/// Piper is optional — the user must download the piper.exe binary and a
/// voice model (.onnx + .onnx.json) into the SSD tools directory.
///
/// Expected layout on the SSD:
///   windows/tools/piper/piper.exe
///   windows/tools/piper/voices/{voiceName}.onnx
///   windows/tools/piper/voices/{voiceName}.onnx.json
///
/// Piper outputs raw 16-bit mono PCM at the sample rate specified in the model JSON.
/// We play it through NAudio, which also allows targeting a specific output device.
/// </summary>
public sealed class PiperTextToSpeechService : ITextToSpeechService
{
    private const int DefaultSampleRate = 22050;

    private readonly string _ssdRoot;
    private readonly object _lock = new();
    private Process? _piperProcess;
    private WaveOutEvent? _waveOut;
    private CancellationTokenSource? _speakCts;
    private string? _selectedVoice;
    private int _rate; // Not directly supported by Piper — reserved for future use
    private int _volume = 100;
    private string? _outputDeviceName;

    public event Action<string>? LogMessage;
    public bool IsSpeaking { get; private set; }

    /// <summary>
    /// Optional output device name. Null = system default.
    /// </summary>
    public string? OutputDeviceName
    {
        get => _outputDeviceName;
        set => _outputDeviceName = value;
    }

    public PiperTextToSpeechService(string ssdRoot)
    {
        _ssdRoot = ssdRoot;
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Stop();

        try
        {
            IsSpeaking = true;
            RunPiperAndPlay(text, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Piper TTS error: {ex.Message}");
        }
        finally
        {
            IsSpeaking = false;
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Stop();

        _speakCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _speakCts.Token;

        try
        {
            IsSpeaking = true;
            await Task.Run(() => RunPiperAndPlay(text, ct), ct);
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop() or external cancellation
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Piper TTS error: {ex.Message}");
        }
        finally
        {
            IsSpeaking = false;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _speakCts?.Cancel();
            _speakCts = null;

            if (_piperProcess is { HasExited: false })
            {
                try { _piperProcess.Kill(); } catch { }
            }
            _piperProcess = null;

            if (_waveOut is not null)
            {
                try { _waveOut.Stop(); } catch { }
                _waveOut.Dispose();
                _waveOut = null;
            }

            IsSpeaking = false;
        }
    }

    public void SetVoice(string voiceName)
    {
        _selectedVoice = voiceName;
        LogMessage?.Invoke($"Piper voice set to: {voiceName}");
    }

    public void SetRate(int rate)
    {
        _rate = Math.Clamp(rate, -10, 10);
        // Piper doesn't have a native rate parameter.
        // A future enhancement could use --length-scale to approximate rate.
    }

    public void SetVolume(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
    }

    public Task<byte[]?> SynthesizeToWavAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult<byte[]?>(null);
        }

        return Task.Run<byte[]?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var piperExe = GetPiperExePath();
            if (!File.Exists(piperExe))
            {
                LogMessage?.Invoke($"Piper executable not found at {piperExe}. Download Piper to enable neural TTS.");
                return null;
            }

            var modelPath = GetModelPath();
            if (modelPath is null)
            {
                LogMessage?.Invoke("No Piper voice model found. Place a .onnx model in windows/tools/piper/voices/.");
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = piperExe,
                Arguments = $"--model \"{modelPath}\" --output_raw",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                LogMessage?.Invoke("Failed to start Piper process.");
                return null;
            }

            process.StandardInput.Write(text);
            process.StandardInput.Close();

            using var pcmStream = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(pcmStream);
            process.WaitForExit(30_000);

            var stderr = process.StandardError.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                LogMessage?.Invoke($"Piper: {stderr.Trim()}");
            }

            var pcm = pcmStream.ToArray();
            if (pcm.Length == 0)
            {
                return null;
            }

            var sampleRate = DetectSampleRate(modelPath);
            return WrapPcmAsWav(pcm, sampleRate, channels: 1, bitsPerSample: 16);
        }, cancellationToken);
    }

    private static byte[] WrapPcmAsWav(byte[] pcm, int sampleRate, short channels, short bitsPerSample)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true);
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return ms.ToArray();
    }

    public IReadOnlyList<string> GetAvailableVoices()
    {
        var voicesDir = Path.Combine(_ssdRoot, "windows", "tools", "piper", "voices");
        if (!Directory.Exists(voicesDir))
            return Array.Empty<string>();

        return Directory.GetFiles(voicesDir, "*.onnx")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetPiperExePath()
    {
        return Path.Combine(_ssdRoot, "windows", "tools", "piper", "piper.exe");
    }

    private string? GetModelPath()
    {
        var voicesDir = Path.Combine(_ssdRoot, "windows", "tools", "piper", "voices");

        if (_selectedVoice is not null)
        {
            var explicit_ = Path.Combine(voicesDir, _selectedVoice + ".onnx");
            if (File.Exists(explicit_))
                return explicit_;
        }

        // Fall back to first available voice
        if (!Directory.Exists(voicesDir)) return null;
        return Directory.GetFiles(voicesDir, "*.onnx").FirstOrDefault();
    }

    private void RunPiperAndPlay(string text, CancellationToken ct)
    {
        var piperExe = GetPiperExePath();
        if (!File.Exists(piperExe))
        {
            LogMessage?.Invoke($"Piper executable not found at {piperExe}. Download Piper to enable neural TTS.");
            return;
        }

        var modelPath = GetModelPath();
        if (modelPath is null)
        {
            LogMessage?.Invoke("No Piper voice model found. Place a .onnx model in windows/tools/piper/voices/.");
            return;
        }

        // Piper outputs raw 16-bit signed PCM at the model's sample rate.
        // We use --output_raw to get the PCM stream on stdout.
        var psi = new ProcessStartInfo
        {
            FileName = piperExe,
            Arguments = $"--model \"{modelPath}\" --output_raw",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var process = Process.Start(psi);
        if (process is null)
        {
            LogMessage?.Invoke("Failed to start Piper process.");
            return;
        }

        lock (_lock) { _piperProcess = process; }

        // Write text to stdin and close it so Piper begins synthesis
        process.StandardInput.Write(text);
        process.StandardInput.Close();

        // Read all PCM output
        using var pcmStream = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(pcmStream);
        process.WaitForExit(30_000);

        // Log any stderr
        var stderr = process.StandardError.ReadToEnd();
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            LogMessage?.Invoke($"Piper: {stderr.Trim()}");
        }

        lock (_lock) { _piperProcess = null; }

        if (ct.IsCancellationRequested || pcmStream.Length == 0) return;

        // Play the PCM buffer through NAudio
        pcmStream.Position = 0;
        var sampleRate = DetectSampleRate(modelPath);
        var waveFormat = new WaveFormat(sampleRate, 16, 1);
        var rawStream = new RawSourceWaveStream(pcmStream, waveFormat);
        var deviceIndex = ResolveOutputDeviceIndex(_outputDeviceName);

        var waveOut = new WaveOutEvent { DeviceNumber = deviceIndex };

        lock (_lock) { _waveOut = waveOut; }

        // Apply volume scaling
        var volumeProvider = new VolumeWaveProvider16(rawStream)
        {
            Volume = _volume / 100f
        };

        using var done = new ManualResetEventSlim(false);
        waveOut.PlaybackStopped += (_, _) => done.Set();
        waveOut.Init(volumeProvider);
        waveOut.Play();

        while (!done.IsSet && !ct.IsCancellationRequested)
        {
            done.Wait(100);
        }

        if (ct.IsCancellationRequested)
        {
            waveOut.Stop();
        }

        lock (_lock) { _waveOut = null; }
        waveOut.Dispose();
    }

    /// <summary>
    /// Attempts to read the sample rate from the Piper model JSON config.
    /// Falls back to 22050 Hz if the JSON file is missing or unreadable.
    /// </summary>
    private static int DetectSampleRate(string modelPath)
    {
        var jsonPath = modelPath + ".json";
        if (!File.Exists(jsonPath)) return DefaultSampleRate;

        try
        {
            var json = File.ReadAllText(jsonPath);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("audio", out var audio) &&
                audio.TryGetProperty("sample_rate", out var sr))
            {
                return sr.GetInt32();
            }
        }
        catch
        {
            // Fall through to default
        }

        return DefaultSampleRate;
    }

    private static int ResolveOutputDeviceIndex(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return -1;

        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            if (string.Equals(caps.ProductName, deviceName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    public void Dispose()
    {
        Stop();
    }
}
