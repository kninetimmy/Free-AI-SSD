using System.Net;
using System.Windows;
using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Companion;

public partial class SettingsWindow : Window
{
    public CompanionConfig Config { get; private set; }

    public SettingsWindow(CompanionConfig current, IReadOnlyList<string> inputDevices)
    {
        InitializeComponent();
        Config = current;
        HostAddressText.Text = current.HostAddress;
        HostPortText.Text = current.HostPort.ToString();
        ApiKeyText.Text = current.ApiKey;
        ApiKeyText.IsEnabled = string.IsNullOrWhiteSpace(current.ApiKey);
        PttBindingText.Text = string.IsNullOrWhiteSpace(current.PttBinding) ? "key:F8" : current.PttBinding;
        InputDeviceCombo.ItemsSource = inputDevices;
        InputDeviceCombo.Text = current.InputDeviceName ?? string.Empty;
        OutputDeviceText.Text = current.OutputDeviceName ?? string.Empty;
        AutoReconnectCheck.IsChecked = current.AutoReconnect;
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

        Config = new CompanionConfig
        {
            HostAddress = host,
            HostPort = port,
            ApiKey = string.IsNullOrWhiteSpace(ApiKeyText.Text) ? Config.ApiKey : ApiKeyText.Text.Trim(),
            PttBinding = PttBindingText.Text.Trim(),
            InputDeviceName = string.IsNullOrWhiteSpace(InputDeviceCombo.Text) ? null : InputDeviceCombo.Text.Trim(),
            OutputDeviceName = string.IsNullOrWhiteSpace(OutputDeviceText.Text) ? null : OutputDeviceText.Text.Trim(),
            AutoReconnect = AutoReconnectCheck.IsChecked ?? true,
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
}
