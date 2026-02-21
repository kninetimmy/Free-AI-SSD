using System.Speech.Synthesis;
using NAudio.Wave;

namespace FreeAiSsd.Runner.Services;

/// <summary>
/// Text-to-speech using the built-in Windows SAPI engine (System.Speech.Synthesis).
/// Zero external dependencies — available on every Windows machine.
///
/// When <see cref="OutputDeviceName"/> is set, audio is rendered to a WAV stream
/// and played through NAudio so the user can target a specific output device.
/// Otherwise the system default audio output is used directly via SAPI.
/// </summary>
public sealed class SystemTextToSpeechService : ITextToSpeechService
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _speakCts;
    private WaveOutEvent? _waveOut;

    /// <summary>
    /// Optional output device name. Null = system default (SAPI direct).
    /// Set before calling Speak/SpeakAsync.
    /// </summary>
    public string? OutputDeviceName { get; set; }

    public event Action<string>? LogMessage;
    public bool IsSpeaking { get; private set; }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Stop();

        try
        {
            IsSpeaking = true;

            if (OutputDeviceName is not null)
            {
                SpeakToDevice(text);
            }
            else
            {
                _synth.SetOutputToDefaultAudioDevice();
                _synth.Speak(text);
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"TTS speak error: {ex.Message}");
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

            if (OutputDeviceName is not null)
            {
                await Task.Run(() => SpeakToDevice(text, ct), ct);
            }
            else
            {
                _synth.SetOutputToDefaultAudioDevice();

                var tcs = new TaskCompletionSource();

                void OnComplete(object? sender, SpeakCompletedEventArgs e)
                {
                    _synth.SpeakCompleted -= OnComplete;
                    if (e.Cancelled)
                        tcs.TrySetCanceled();
                    else if (e.Error is not null)
                        tcs.TrySetException(e.Error);
                    else
                        tcs.TrySetResult();
                }

                _synth.SpeakCompleted += OnComplete;

                using var reg = ct.Register(() =>
                {
                    _synth.SpeakAsyncCancelAll();
                });

                _synth.SpeakAsync(text);

                await tcs.Task;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop() or external cancellation
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"TTS speak error: {ex.Message}");
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

            try
            {
                _synth.SpeakAsyncCancelAll();
            }
            catch
            {
                // Ignore — synth may not be speaking
            }

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
        try
        {
            _synth.SelectVoice(voiceName);
            LogMessage?.Invoke($"TTS voice set to: {voiceName}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Failed to set voice '{voiceName}': {ex.Message}");
        }
    }

    public void SetRate(int rate)
    {
        _synth.Rate = Math.Clamp(rate, -10, 10);
    }

    public void SetVolume(int volume)
    {
        _synth.Volume = Math.Clamp(volume, 0, 100);
    }

    public IReadOnlyList<string> GetAvailableVoices()
    {
        return _synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo.Name)
            .ToList();
    }

    /// <summary>
    /// Returns the names of available audio output devices (for the TtsOutputDevice config).
    /// </summary>
    public static IReadOnlyList<string> GetAvailableOutputDevices()
    {
        var devices = new List<string>();
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            devices.Add(caps.ProductName);
        }
        return devices;
    }

    /// <summary>
    /// Renders speech to a WAV memory stream then plays through NAudio on the
    /// specified output device. This allows targeting non-default audio devices.
    /// </summary>
    private void SpeakToDevice(string text, CancellationToken ct = default)
    {
        using var wavStream = new MemoryStream();
        _synth.SetOutputToWaveStream(wavStream);
        _synth.Speak(text);
        wavStream.Position = 0;

        if (ct.IsCancellationRequested) return;

        int deviceIndex = ResolveOutputDeviceIndex(OutputDeviceName);
        using var waveStream = new RawSourceWaveStream(wavStream, new WaveFormat(22050, 16, 1));
        var waveOut = new WaveOutEvent { DeviceNumber = deviceIndex };

        lock (_lock)
        {
            _waveOut = waveOut;
        }

        using var done = new ManualResetEventSlim(false);
        waveOut.PlaybackStopped += (_, _) => done.Set();
        waveOut.Init(waveStream);
        waveOut.Play();

        // Wait for playback or cancellation
        while (!done.IsSet && !ct.IsCancellationRequested)
        {
            done.Wait(100);
        }

        if (ct.IsCancellationRequested)
        {
            waveOut.Stop();
        }

        lock (_lock)
        {
            _waveOut = null;
        }

        waveOut.Dispose();
    }

    private static int ResolveOutputDeviceIndex(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return -1; // WaveOut default

        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            if (string.Equals(caps.ProductName, deviceName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1; // Fall back to default
    }

    public void Dispose()
    {
        Stop();
        _synth.Dispose();
    }
}
