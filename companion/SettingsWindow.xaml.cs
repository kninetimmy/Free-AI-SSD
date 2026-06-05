using System.Net;
using System.Windows;
using FreeAiSsd.Shared.Client;
using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Companion;

public partial class SettingsWindow : Window
{
    private readonly IAudioCaptureService? _audio;
    private bool _testingMic;
    private bool _clearKey;

    public CompanionConfig Config { get; private set; }

    public SettingsWindow(CompanionConfig current, IReadOnlyList<string> inputDevices)
        : this(current, inputDevices, null)
    {
    }

    public SettingsWindow(CompanionConfig current, IReadOnlyList<string> inputDevices, IAudioCaptureService? audio)
    {
        InitializeComponent();
        Config = current;
        _audio = audio;
        HostAddressText.Text = current.HostAddress;
        HostPortText.Text = current.HostPort.ToString();
        // codex
        ApiKeyHint.Text = string.IsNullOrWhiteSpace(current.ApiKey)
            ? "No key set. Leave blank for none."
            : "Key is set. Leave blank to keep it, click 'Clear key' to remove it, or enter a new one.";
        ServerRequiresApiKeyCheck.IsChecked = !current.ServerRequiresApiKey;
        PttBindingText.Text = string.IsNullOrWhiteSpace(current.PttBinding) ? CompanionDefaults.DefaultPttBinding : current.PttBinding;
        InputDeviceCombo.ItemsSource = inputDevices;
        InputDeviceCombo.Text = current.InputDeviceName ?? string.Empty;
        OutputDeviceText.Text = current.OutputDeviceName ?? string.Empty;
        AutoReconnectCheck.IsChecked = current.AutoReconnect;
        PttActivationSoundCheck.IsChecked = current.PttActivationSoundEnabled;
        PttOverlayCheck.IsChecked = current.PttOverlayEnabled;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HostPortText.Text, out var port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Host port must be 1-65535.");
            return;
        }

        var host = HostAddressText.Text.Trim();
        if (!string.IsNullOrWhiteSpace(host) && !IPAddress.TryParse(host, out _) && !Uri.CheckHostName(host).Equals(UriHostNameType.Dns))
        {
            MessageBox.Show("Host must be IPv4 or resolvable hostname.");
            return;
        }

        // codex
        var newKey = CompanionConfig.ResolveApiKeyForSave(Config.ApiKey, ApiKeyBox.Password, _clearKey);
        Config = new CompanionConfig
        {
            HostAddress = host,
            HostPort = port,
            ApiKey = newKey,
            ServerRequiresApiKey = !(ServerRequiresApiKeyCheck.IsChecked ?? false),
            PttBinding = PttBindingText.Text.Trim(),
            InputDeviceName = string.IsNullOrWhiteSpace(InputDeviceCombo.Text) ? null : InputDeviceCombo.Text.Trim(),
            OutputDeviceName = string.IsNullOrWhiteSpace(OutputDeviceText.Text) ? null : OutputDeviceText.Text.Trim(),
            AutoReconnect = AutoReconnectCheck.IsChecked ?? true,
            PttActivationSoundEnabled = PttActivationSoundCheck.IsChecked ?? true,
            PttOverlayEnabled = PttOverlayCheck.IsChecked ?? true,
            SchemaVersion = 1
        };

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        _clearKey = true;
        ApiKeyBox.Clear();
        ApiKeyHint.Text = "Key will be cleared on save.";
    }

    // codex
    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            ApiKeyHint.Text = "Entered key will be saved.";
            return;
        }

        if (_clearKey)
        {
            ApiKeyHint.Text = "Key will be cleared on save.";
            return;
        }

        ApiKeyHint.Text = string.IsNullOrWhiteSpace(Config.ApiKey)
            ? "No key set. Leave blank for none."
            : "Key is set. Leave blank to keep it, click 'Clear key' to remove it, or enter a new one.";
    }

    private async void TestMic_Click(object sender, RoutedEventArgs e)
    {
        if (_testingMic) return;
        if (_audio is null)
        {
            MicLevelText.Text = "Mic test unavailable (no capture service).";
            return;
        }

        _testingMic = true;
        MicLevelText.Text = "Recording...";
        MicLevelBar.Value = 0;

        try
        {
            var device = string.IsNullOrWhiteSpace(InputDeviceCombo.Text) ? null : InputDeviceCombo.Text.Trim();
            _audio.StartRecording(device);
            await Task.Delay(1000);
            var pcm = _audio.StopRecording();

            var (peak, peakDb) = ComputePeak(pcm);
            MicLevelBar.Value = Math.Clamp(peak * 100.0, 0, 100);
            MicLevelText.Text = pcm.Length == 0
                ? "No audio captured."
                : $"Peak: {peak * 100.0:F1}%  ({peakDb:F1} dBFS)";
        }
        catch (Exception ex)
        {
            MicLevelText.Text = $"Mic test failed: {ex.Message}";
        }
        finally
        {
            _testingMic = false;
        }
    }

    // Assumes 16-bit signed little-endian PCM (what IAudioCaptureService returns).
    private static (double normalizedPeak, double peakDb) ComputePeak(byte[] pcm)
    {
        if (pcm is null || pcm.Length < 2) return (0.0, double.NegativeInfinity);

        short peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var absSample = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (absSample > peak) peak = (short)absSample;
        }

        var normalized = peak / (double)short.MaxValue;
        var db = normalized <= 0 ? double.NegativeInfinity : 20.0 * Math.Log10(normalized);
        return (normalized, db);
    }
}
