using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner;

public partial class MainWindow : System.Windows.Window
{
    private readonly HttpClient _http = new();
    private PortableConfig? _config;
    private string _ssdRoot = string.Empty;
    private Process? _ollama;
    private SsdLogger? _logger;
    private int? _currentPort;

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
    }

    private void LoadConfig()
    {
        _ssdRoot = AppContext.BaseDirectory;
        if (_ssdRoot.TrimEnd(Path.DirectorySeparatorChar).EndsWith("runner", StringComparison.OrdinalIgnoreCase))
        {
            _ssdRoot = Directory.GetParent(_ssdRoot)!.FullName;
        }

        var configPath = Path.Combine(_ssdRoot, "config", "portable-config.json");
        if (!File.Exists(configPath))
        {
            StatusText.Text = "Config not found";
            AppendLog($"Missing config at {configPath}");
            return;
        }

        _config = PortableConfig.Load(configPath);
        ModelCombo.ItemsSource = _config.Models;
        ModelCombo.SelectedIndex = _config.Models.Count > 0 ? 0 : -1;
        _logger = new SsdLogger(_ssdRoot, "runner");
        StatusText.Text = "Ready (not running)";
        AppendLog($"Loaded config from {configPath}");
    }

    private async void Start_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null || _ollama is { HasExited: false }) return;

        var ollamaExe = Path.Combine(_ssdRoot, _config.OllamaRelativePath);
        if (!File.Exists(ollamaExe))
        {
            AppendLog("ollama.exe missing in staged tools folder.");
            return;
        }

        try
        {
            _currentPort = ResolvePort(_config.OllamaPort);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Unable to find a free port";
            AppendLog($"Start failed: {ex.Message}");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ollamaExe,
            Arguments = "serve",
            WorkingDirectory = Path.GetDirectoryName(ollamaExe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.Environment["OLLAMA_MODELS"] = Path.Combine(_ssdRoot, SsdLayout.Models);
        startInfo.Environment["OLLAMA_HOST"] = $"127.0.0.1:{_currentPort.Value}";
        startInfo.Environment["OLLAMA_ORIGINS"] = "*";

        _ollama = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _ollama.OutputDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) AppendLog(args.Data); };
        _ollama.ErrorDataReceived += (_, args) => { if (!string.IsNullOrWhiteSpace(args.Data)) AppendLog(args.Data); };
        _ollama.Exited += (_, _) =>
        {
            AppendLog("Ollama exited.");
            Dispatcher.Invoke(() => StatusText.Text = "Stopped");
            _currentPort = null;
        };

        _ollama.Start();
        _ollama.BeginOutputReadLine();
        _ollama.BeginErrorReadLine();
        StatusText.Text = $"Running on 127.0.0.1:{_currentPort.Value}";
        _logger?.Info($"Started ollama on port {_currentPort.Value}");
        await Task.Delay(1000);
    }

    private void Stop_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_ollama is { HasExited: false })
        {
            _ollama.Kill(entireProcessTree: true);
            _ollama.Dispose();
            _ollama = null;
            _currentPort = null;
            StatusText.Text = "Stopped";
            _logger?.Info("Stopped ollama");
        }
    }

    private async void Send_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null || ModelCombo.SelectedItem is not string model)
        {
            return;
        }

        if (!TryGetCurrentHost(out var host))
        {
            return;
        }

        var request = new
        {
            model,
            prompt = PromptText.Text,
            stream = false
        };

        try
        {
            using var response = await _http.PostAsJsonAsync($"http://{host}/api/generate", request);
            response.EnsureSuccessStatusCode();

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var text = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;
            ResponseText.Text = text;
        }
        catch (Exception ex)
        {
            AppendLog($"Generate failed: {ex.Message}");
        }
    }

    private void OpenBrowser_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!TryGetCurrentHost(out var host))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = $"http://{host}",
            UseShellExecute = true
        });
    }

    private bool TryGetCurrentHost(out string host)
    {
        host = string.Empty;
        if (_currentPort is null || _ollama is null || _ollama.HasExited)
        {
            var message = "Ollama is not running. Click Start Ollama first.";
            StatusText.Text = message;
            AppendLog(message);
            return false;
        }

        host = $"127.0.0.1:{_currentPort.Value}";
        return true;
    }

    private static int ResolvePort(int preferred)
    {
        for (var port = preferred; port < preferred + 20; port++)
        {
            if (NetUtils.IsPortFree(port)) return port;
        }

        throw new InvalidOperationException("No free ports in range.");
    }

    private void AppendLog(string line)
    {
        Dispatcher.Invoke(() =>
        {
            LogText.AppendText(line + Environment.NewLine);
            LogText.ScrollToEnd();
        });
        _logger?.Info(line);
    }
}
