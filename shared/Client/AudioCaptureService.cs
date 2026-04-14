using NAudio.Wave;

namespace FreeAiSsd.Shared.Client;

public sealed class AudioCaptureService : IAudioCaptureService
{
    /// <summary>Whisper expects 16kHz, 16-bit, mono PCM.</summary>
    private static readonly WaveFormat TargetFormat = new(16000, 16, 1);

    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private readonly object _lock = new();

    public event Action<string>? LogMessage;
    public bool IsRecording => _waveIn is not null;

    public IReadOnlyList<string> GetAvailableDevices()
    {
        var devices = new List<string>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            devices.Add(caps.ProductName);
        }
        return devices;
    }

    public void StartRecording(string? deviceName = null)
    {
        lock (_lock)
        {
            if (_waveIn is not null)
            {
                LogMessage?.Invoke("Already recording.");
                return;
            }

            int deviceIndex = ResolveDeviceIndex(deviceName);
            _buffer = new MemoryStream();

            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = TargetFormat,
                BufferMilliseconds = 100
            };

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;

            try
            {
                _waveIn.StartRecording();
                LogMessage?.Invoke($"Recording started (device: {(deviceName ?? "default")})");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Failed to start recording: {ex.Message}");
                CleanupRecording();
                throw;
            }
        }
    }

    public byte[] StopRecording()
    {
        lock (_lock)
        {
            if (_waveIn is null || _buffer is null)
            {
                LogMessage?.Invoke("Not recording.");
                return Array.Empty<byte>();
            }

            try
            {
                _waveIn.StopRecording();
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Error stopping recording: {ex.Message}");
            }

            var pcmData = _buffer.ToArray();
            CleanupRecording();
            LogMessage?.Invoke($"Recording stopped. Captured {pcmData.Length} bytes ({pcmData.Length / 32000.0:F1}s)");
            return pcmData;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            _buffer?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            LogMessage?.Invoke($"Recording error: {e.Exception.Message}");
        }
    }

    private int ResolveDeviceIndex(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
            return 0; // System default

        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            if (string.Equals(caps.ProductName, deviceName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        LogMessage?.Invoke($"Microphone '{deviceName}' not found. Using default device.");
        return 0;
    }

    private void CleanupRecording()
    {
        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        _buffer?.Dispose();
        _buffer = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CleanupRecording();
        }
    }
}
