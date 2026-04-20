using System.Drawing;
using System.IO;
using System.Net.Http;
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
    private PttOverlayWindow? _overlay;
    private bool _healthLoopStarted;
    // codex
    private volatile bool _liveModeEnabled;

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

        if (!_config.IsComplete())
        {
            _tray.ShowBalloonTip(3000, "Setup required", "Open Settings from the tray to complete setup.", ToolTipIcon.Warning);
            // codex
            StopLive();
            SetState("Needs Setup");
            return;
        }

        StartLive();
    }

    private void StartLive()
    {
        // codex
        _liveModeEnabled = true;
        InitializeBindings();
        ApplyOverlayVisibility();
        if (!_healthLoopStarted)
        {
            _healthLoopStarted = true;
            _ = Task.Run(HealthLoopAsync);
        }
    }

    // codex
    private void StopLive()
    {
        _liveModeEnabled = false;
        _hotas.Stop();
        _hotkey?.Dispose();
        _hotkey = null;
        if (_audio.IsRecording)
        {
            try
            {
                _audio.StopRecording();
            }
            catch (Exception ex)
            {
                _log.Write($"Failed to stop recording during live teardown: {ex.Message}");
            }
        }

        HideOverlay();
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
            if (!_hotkey.Start())
            {
                SetState("PTT unavailable");
                _tray.ShowBalloonTip(3000, "PTT error", "Keyboard hook failed to install. PTT will not work.", ToolTipIcon.Warning);
            }
            else
            {
                _log.Write($"Using keyboard fallback binding: {_config.PttBinding}");
            }
            return;
        }

        PttBindingParser.ParseHotas(_config.PttBinding, out var deviceName, out var buttonIndex);
        if (deviceName is null)
        {
            _log.Write($"Invalid PTT binding: '{_config.PttBinding}'. Expected format: device|button.");
            SetState("Bad Binding");
            _tray.ShowBalloonTip(3000, "PTT error", $"Invalid PTT binding: '{_config.PttBinding}'.", ToolTipIcon.Warning);
            return;
        }

        _hotas.Start(deviceName, buttonIndex);
    }

    private async Task HealthLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                // codex
                if (_liveModeEnabled && _config.IsComplete())
                {
                    await ProbeHealthAsync();
                }
            }
            catch (Exception ex)
            {
                _log.Write($"Health probe error: {ex.Message}");
            }

            // codex
            var delay = !_liveModeEnabled || !_config.IsComplete()
                ? TimeSpan.FromSeconds(1)
                : (_config.AutoReconnect ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(5));
            await Task.Delay(delay, _cts.Token)
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
                if (_config.PttActivationSoundEnabled)
                {
                    PttSounds.PlayAsync(PttSounds.GetActivationBeep(), _config.OutputDeviceName);
                }

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

            if (_config.PttActivationSoundEnabled)
            {
                PttSounds.PlayAsync(PttSounds.GetDeactivationBeep(), _config.OutputDeviceName);
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

            if (!string.IsNullOrWhiteSpace(response.AudioBase64))
            {
                if (!string.IsNullOrWhiteSpace(response.AudioMime) &&
                    !string.Equals(response.AudioMime, "audio/wav", StringComparison.OrdinalIgnoreCase))
                {
                    _log.Write($"voice/query returned unsupported audio mime '{response.AudioMime}'; skipping playback.");
                }
                else
                {
                    SetState("Speaking");
                    PlayTts(Convert.FromBase64String(response.AudioBase64), response.AudioMime);
                }
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
        form.Add(new StringContent("true"), "returnAudio");

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
        var window = new SettingsWindow(_config, _audio.GetAvailableDevices(), _audio);
        if (window.ShowDialog() == true)
        {
            _config = window.Config;
            _config.Save(_configPath);
            if (_config.IsComplete())
            {
                StartLive();
                _ = ProbeHealthAsync();
            }
            else
            {
                // codex
                StopLive();
                SetState("Needs Setup");
                _tray.ShowBalloonTip(3000, "Setup incomplete", "Configuration is incomplete. Open Settings to finish.", ToolTipIcon.Warning);
            }
        }
    }

    private void SetState(string state)
    {
        _tray.Text = $"FreeAiSsd Companion - {state}";

        var overlay = _overlay;
        if (overlay is not null)
        {
            var mapped = state switch
            {
                "Listening" => CompanionPttState.Listening,
                "Thinking" => CompanionPttState.Thinking,
                "Speaking" => CompanionPttState.Speaking,
                _ => CompanionPttState.Idle,
            };

            try
            {
                overlay.UpdateState(mapped);
            }
            catch (Exception ex)
            {
                _log.Write($"Overlay update failed: {ex.Message}");
            }
        }
    }

    private void ApplyOverlayVisibility()
    {
        if (_config.PttOverlayEnabled)
        {
            ShowOverlay();
        }
        else
        {
            HideOverlay();
        }
    }

    private void ShowOverlay()
    {
        if (_overlay is not null)
        {
            return;
        }

        // WPF window creation can throw if no interactive display is attached
        // (tray-only mode on a headless session). Treat overlay as best-effort.
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                _log.Write("Overlay skipped: no WPF dispatcher available.");
                return;
            }

            dispatcher.Invoke(() =>
            {
                try
                {
                    _overlay = new PttOverlayWindow();
                    _overlay.Show();
                }
                catch (Exception ex)
                {
                    _overlay = null;
                    _log.Write($"Overlay window creation failed (tray-only mode?): {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            _log.Write($"Overlay dispatch failed: {ex.Message}");
        }
    }

    private void HideOverlay()
    {
        var overlay = _overlay;
        if (overlay is null)
        {
            return;
        }

        _overlay = null;

        try
        {
            overlay.Dispatcher.Invoke(() =>
            {
                try
                {
                    overlay.Close();
                }
                catch
                {
                    // ignore close failures
                }
            });
        }
        catch
        {
            // ignore dispatch failures
        }
    }

    private static JsonSerializerOptions JsonOptions() => new() { PropertyNameCaseInsensitive = true };

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
        HideOverlay();
        _tray.Visible = false;
        _tray.Dispose();
        _http.Dispose();
    }

    private sealed record VoiceQueryResponse(string Transcription, string? ResponseText, string? AudioBase64, string? AudioMime);
}
