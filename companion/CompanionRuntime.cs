using System.Drawing;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Client;
using FreeAiSsd.Shared.Models;
using NAudio.Wave;

namespace FreeAiSsd.Companion;

internal sealed class CompanionRuntime : IDisposable
{
    // /voice/query may include upload + STT + chat + optional TTS generation and can take longer than normal API calls.
    private static readonly TimeSpan VoiceQueryTimeout = TimeSpan.FromSeconds(120);
    private static readonly long MaxVoiceResponseBytes = ((long)new PortableConfig().NetworkMaxAudioUploadMB + 2) * 1024L * 1024L;

    private readonly IAudioCaptureService _audio;
    private readonly IHotasInputService _hotas;
    private readonly CompanionLog _log;
    private readonly NotifyIcon _tray;
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _configPath;
    private CompanionConfig _config = new();
    private KeyboardPttHotkey? _hotkey;

    public CompanionRuntime(IAudioCaptureService audio, IHotasInputService hotas, CompanionLog log)
    {
        _audio = audio;
        _hotas = hotas;
        _log = log;
        _configPath = Path.Combine(AppContext.BaseDirectory, "companion-config.json");
        _tray = new NotifyIcon
        {
            Visible = true,
            Icon = SystemIcons.Application,
            Text = "FreeAiSsd Companion - Idle",
            ContextMenuStrip = BuildMenu()
        };

        _hotas.PttButtonPressed += OnPttPressed;
        _hotas.PttButtonReleased += OnPttReleased;
    }

    public void Start()
    {
        try
        {
            _config = CompanionConfig.Load(_configPath);
        }
        catch (Exception ex)
        {
            _log.Write($"Failed to load config: {ex.Message}");
            _config = new CompanionConfig();
        }

        if (!_config.IsComplete())
        {
            OpenSettings();
        }

        InitializeBindings();
        _ = Task.Run(HealthLoopAsync);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Settings", null, (_, _) => OpenSettings());
        menu.Items.Add("Reconnect", null, async (_, _) => await ProbeHealthAsync());
        menu.Items.Add("Quit", null, (_, _) => System.Windows.Application.Current.Shutdown());
        return menu;
    }

    private void InitializeBindings()
    {
        _hotas.Stop();
        _hotkey?.Dispose();
        _hotkey = null;

        if (_config.PttBinding.StartsWith("key:", StringComparison.OrdinalIgnoreCase))
        {
            _hotkey = new KeyboardPttHotkey(_config.PttBinding, OnPttPressed, OnPttReleased, _log);
            _hotkey.Start();
            _log.Write($"Using keyboard fallback binding: {_config.PttBinding}");
            return;
        }

        ParseHotasBinding(_config.PttBinding, out var deviceName, out var buttonIndex);
        _hotas.Start(deviceName, buttonIndex);
    }

    private async Task HealthLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await ProbeHealthAsync();
            }
            catch (Exception ex)
            {
                _log.Write($"Health probe error: {ex.Message}");
            }

            await Task.Delay(_config.AutoReconnect ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(5), _cts.Token)
                .ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task ProbeHealthAsync()
    {
        if (!TryBuildBaseUri(out var baseUri, out var error))
        {
            _tray.ShowBalloonTip(2500, "Companion config", error, ToolTipIcon.Warning);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/api/health"));
        using var res = await _http.SendAsync(req, _cts.Token);
        if (!res.IsSuccessStatusCode)
        {
            _tray.ShowBalloonTip(2500, "Runner unreachable", $"Health check failed: {(int)res.StatusCode}", ToolTipIcon.Warning);
        }
    }

    private void OnPttPressed()
    {
        try
        {
            if (!_audio.IsRecording)
            {
                SetState("Listening");
                _audio.StartRecording(_config.InputDeviceName);
            }
        }
        catch (Exception ex)
        {
            _log.Write($"PTT press failed: {ex.Message}");
            SetState("Idle");
        }
    }

    private async void OnPttReleased()
    {
        byte[] pcm;
        try
        {
            if (!_audio.IsRecording)
            {
                return;
            }

            SetState("Thinking");
            pcm = _audio.StopRecording();
            if (pcm.Length == 0)
            {
                SetState("Idle");
                return;
            }
        }
        catch (Exception ex)
        {
            _log.Write($"PTT release failed: {ex.Message}");
            SetState("Idle");
            return;
        }

        try
        {
            if (!TryBuildBaseUri(out var baseUri, out var error))
            {
                _log.Write(error);
                SetState("Idle");
                return;
            }

            using var req = BuildVoiceRequest(new Uri(baseUri, "/api/voice/query"), pcm);
            using var timeoutCts = new CancellationTokenSource(VoiceQueryTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, timeoutCts.Token);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);

            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(linked.Token);
                _log.Write($"voice/query failed {(int)res.StatusCode}: {err}");
                SetState("Idle");
                return;
            }

            await using var stream = await res.Content.ReadAsStreamAsync(linked.Token);
            var payload = await ReadBoundedAsync(stream, MaxVoiceResponseBytes, linked.Token);

            var response = JsonSerializer.Deserialize<VoiceQueryResponse>(payload, JsonOptions());
            if (response is null)
            {
                throw new InvalidOperationException("voice/query returned empty JSON.");
            }

            // TODO(phase-b): Runner /api/voice/query currently returns transcription/responseText/ttsTriggeredOnHost and does not include audio bytes.
            // Intended split-PC contract is to return audioBase64/audioMime so Companion can play locally on the VR machine.
            if (!string.IsNullOrWhiteSpace(response.AudioBase64))
            {
                SetState("Speaking");
                PlayTts(Convert.FromBase64String(response.AudioBase64), response.AudioMime);
            }

            _log.Write($"Transcript: {response.Transcription}");
            SetState("Idle");
        }
        catch (Exception ex)
        {
            _log.Write($"voice/query exception: {ex.Message}");
            SetState("Idle");
        }
    }

    private HttpRequestMessage BuildVoiceRequest(Uri uri, byte[] pcm)
    {
        var wav = ToWavBytes(pcm, 16000, 1, 16);
        var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", "ptt.wav");
        form.Add(new StringContent("true"), "autoSendToChat");
        form.Add(new StringContent("true"), "speakResponse");

        var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = form
        };

        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey.Trim());
            req.Headers.Add("X-API-Key", _config.ApiKey.Trim());
        }

        return req;
    }

    private static byte[] ToWavBytes(byte[] pcm, int sampleRate, short channels, short bitsPerSample)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.ASCII, true);
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcm.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return ms.ToArray();
    }

    private void PlayTts(byte[] audioBytes, string? mime)
    {
        using var ms = new MemoryStream(audioBytes);
        using var reader = new WaveFileReader(ms);
        using var wo = new WaveOutEvent();
        if (!string.IsNullOrWhiteSpace(_config.OutputDeviceName))
        {
            for (var i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                if (caps.ProductName.Contains(_config.OutputDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    wo.DeviceNumber = i;
                    break;
                }
            }
        }

        wo.Init(reader);
        wo.Play();
        while (wo.PlaybackState == PlaybackState.Playing)
        {
            Thread.Sleep(20);
        }
    }

    private bool TryBuildBaseUri(out Uri baseUri, out string error)
    {
        error = string.Empty;
        baseUri = null!;
        var host = _config.HostAddress?.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "HostAddress is required.";
            return false;
        }

        var candidate = $"http://{host}:{_config.HostPort}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = $"Invalid host URL: {candidate}";
            return false;
        }

        baseUri = uri;
        return true;
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_config, _audio.GetAvailableDevices());
        if (window.ShowDialog() == true)
        {
            _config = window.Config;
            _config.Save(_configPath);
            InitializeBindings();
            _ = ProbeHealthAsync();
        }
    }

    private void SetState(string state)
    {
        _tray.Text = $"FreeAiSsd Companion - {state}";
    }

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };

    private static void ParseHotasBinding(string binding, out string? deviceName, out int buttonIndex)
    {
        deviceName = null;
        buttonIndex = 0;
        var segments = (binding ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2 && int.TryParse(segments[1], out var idx))
        {
            deviceName = segments[0];
            buttonIndex = idx;
        }
    }


    private static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new InvalidOperationException("voice/query response exceeded size limit.");
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _hotas.Stop();
        _hotas.Dispose();
        _audio.Dispose();
        _hotkey?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _http.Dispose();
    }

    private sealed record VoiceQueryResponse(string Transcription, string? ResponseText, string? AudioBase64, string? AudioMime);
}
