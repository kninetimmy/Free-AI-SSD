using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Principal;
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
    private DependencyCheckResult _lastDependencyCheck = new(true, Array.Empty<MissingDependency>());

    public MainWindow()
    {
        InitializeComponent();
        LoadConfig();
        _ = ShowModelSizingWarningsOnStartupAsync();
        _ = InitializeCompatibilityAsync();
    }

    private async Task InitializeCompatibilityAsync()
    {
        RefreshCompatibilityUi();
        await EnsureDependenciesReadyAsync(forcePrompt: CommandLineHas("--postinstall"), userTriggered: false);
    }

    private void LoadConfig()
    {
        _ssdRoot = AppContext.BaseDirectory;
        var baseTrimmed = _ssdRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (baseTrimmed.EndsWith($"windows{Path.DirectorySeparatorChar}runner", StringComparison.OrdinalIgnoreCase))
        {
            _ssdRoot = Directory.GetParent(Directory.GetParent(baseTrimmed)!.FullName)!.FullName;
        }
        else if (baseTrimmed.EndsWith("runner", StringComparison.OrdinalIgnoreCase))
        {
            // Backward compatibility with old layout (<SSD>/runner).
            _ssdRoot = Directory.GetParent(baseTrimmed)!.FullName;
        }

        var configPath = Path.Combine(_ssdRoot, "config", "portable-config.json");
        if (!File.Exists(configPath))
        {
            StatusText.Text = "Config not found";
            AppendLog($"Missing config at {configPath}");
            return;
        }

        _config = PortableConfig.Load(configPath);
        var installedModels = _config.Models
            .Where(m => m.Status == ModelInstallStatus.Installed)
            .Select(m => m.Name)
            .ToList();
        ModelCombo.ItemsSource = installedModels;
        ModelCombo.SelectedIndex = installedModels.Count > 0 ? 0 : -1;
        _logger = new SsdLogger(_ssdRoot, "runner");
        StatusText.Text = "Ready (not running)";
        AppendLog($"Loaded config from {configPath}");
    }

    private async void Start_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null || _ollama is { HasExited: false }) return;

        if (!await EnsureDependenciesReadyAsync(forcePrompt: false, userTriggered: true))
        {
            return;
        }

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
        startInfo.Environment["OLLAMA_ORIGINS"] = "http://127.0.0.1,http://localhost";

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

    private async void RerunDependencyCheck_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await EnsureDependenciesReadyAsync(forcePrompt: true, userTriggered: true);
    }

    private void OpenPrereqsFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var folder = Path.Combine(_ssdRoot, SsdLayout.Prereqs);
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private async Task ShowModelSizingWarningsOnStartupAsync()
    {
        if (_config is null)
        {
            return;
        }

        var statePath = Path.Combine(_ssdRoot, SsdLayout.Config, "runner-first-run.json");
        var state = RunnerFirstRunState.Load(statePath);
        if (state.SizingWarningDismissed)
        {
            return;
        }

        var ramGb = SystemResources.GetTotalSystemRamGb();
        var vramGb = SystemResources.GetGpuVramGb();
        var warnings = new List<string>();

        foreach (var model in _config.Models.Where(m => m.Status == ModelInstallStatus.Installed))
        {
            var sizing = ModelSizingCatalog.Suggest(model.Name);
            var reasons = new List<string>();

            if (ramGb.HasValue && ramGb.Value < sizing.RecommendedSystemRamGb)
            {
                reasons.Add($"RAM {ramGb.Value} GB < recommended {sizing.RecommendedSystemRamGb} GB");
            }

            if (sizing.RecommendedVramGb.HasValue)
            {
                if (!vramGb.HasValue)
                {
                    reasons.Add($"VRAM unknown; recommends {sizing.RecommendedVramGb.Value} GB (may run on CPU)");
                }
                else if (vramGb.Value < sizing.RecommendedVramGb.Value)
                {
                    reasons.Add($"VRAM {vramGb.Value} GB < recommended {sizing.RecommendedVramGb.Value} GB (may run on CPU)");
                }
            }

            if (reasons.Count > 0)
            {
                warnings.Add($"{model.Name}: {string.Join("; ", reasons)}");
            }
        }

        if (warnings.Count == 0)
        {
            return;
        }

        var message = "This PC may struggle with the following models:"
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, warnings.Select(w => $"- {w}"))
            + Environment.NewLine + Environment.NewLine
            + "Select Yes to continue showing this warning on startup, or No for 'Don't show again on this machine'.";

        var result = System.Windows.MessageBox.Show(
            message,
            "Model sizing warning",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Information,
            System.Windows.MessageBoxResult.Yes);

        if (result == System.Windows.MessageBoxResult.No)
        {
            state.SizingWarningDismissed = true;
            state.LastCheckedUtc = DateTime.UtcNow;
            await state.SaveAsync(statePath);
        }
    }

    private async Task<bool> EnsureDependenciesReadyAsync(bool forcePrompt, bool userTriggered)
    {
        _lastDependencyCheck = DependencyChecker.Check(_ssdRoot);
        RefreshCompatibilityUi();

        if (_lastDependencyCheck.IsSatisfied)
        {
            await SaveFirstRunStateAsync(promptShown: true);
            return true;
        }

        var statePath = Path.Combine(_ssdRoot, SsdLayout.Config, "runner-first-run.json");
        var state = RunnerFirstRunState.Load(statePath);
        if (!forcePrompt && state.DependencyPromptShown)
        {
            AppendLog("Dependencies still missing. Use 'Re-run dependency check' to retry installation.");
            return false;
        }

        var manifestPath = PrereqCatalog.GetManifestPath(_ssdRoot);
        var manifest = PrereqManifest.Load(manifestPath);
        var prereqDir = Path.Combine(_ssdRoot, SsdLayout.Prereqs);
        var bundleIssues = PrereqInstallValidator.ValidateBundleHealth(prereqDir, manifest);
        if (bundleIssues.Count > 0)
        {
            foreach (var issue in bundleIssues)
            {
                AppendLog($"Prerequisite bundle warning: {issue}");
                _logger?.Error($"Prerequisite bundle invalid: {issue}");
            }

            System.Windows.MessageBox.Show(
                "Offline prerequisites are unavailable or incomplete. " + PrereqInstallValidator.RefreshMessage,
                "Prerequisites unavailable",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        var dialog = new DependencyInstallDialog(_lastDependencyCheck.MissingItems, manifest.Prerequisites) { Owner = this };
        var result = dialog.ShowDialog();

        if (result != true)
        {
            if (!userTriggered)
            {
                System.Windows.Application.Current.Shutdown();
            }

            return false;
        }

        if (dialog.Action == DependencyDialogAction.Skip)
        {
            AppendLog("User chose to skip prerequisite install.");
            await SaveFirstRunStateAsync(promptShown: true);
            return false;
        }

        if (dialog.Action != DependencyDialogAction.Install || dialog.SelectedEntries.Count == 0)
        {
            return false;
        }

        if (dialog.SelectedEntries.Any(e => e.RequiresAdmin) && !IsRunningAsAdministrator())
        {
            var elevate = System.Windows.MessageBox.Show(
                "Administrator permissions required. Relaunch as Administrator?",
                "Admin required",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (elevate == System.Windows.MessageBoxResult.Yes)
            {
                RelaunchAsAdmin("--postinstall");
            }

            return false;
        }

        var selectedIds = dialog.SelectedEntries.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requestedMissing = _lastDependencyCheck.MissingItems
            .Where(x => selectedIds.Contains(x.Id))
            .ToList();

        var installPlan = PrereqInstallValidator.BuildValidatedInstallPlan(
            _ssdRoot,
            requestedMissing,
            manifest,
            AppendLog,
            warning =>
            {
                AppendLog($"Warning: {warning}");
                _logger?.Info($"Prereq warning: {warning}");
            },
            out var validationErrors);

        if (validationErrors.Count > 0)
        {
            foreach (var error in validationErrors)
            {
                AppendLog($"Prerequisite install blocked: {error}");
                _logger?.Error($"Prereq install blocked: {error}");
            }

            System.Windows.MessageBox.Show(
                "Prerequisite installation blocked due to validation failure. "
                + PrereqInstallValidator.RefreshMessage,
                "Prerequisite validation failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        foreach (var item in installPlan)
        {
            AppendLog($"Installing {item.Definition.DisplayName}...");
            _logger?.Info($"Installing prerequisite: {item.Definition.Id}");

            var installer = Process.Start(new ProcessStartInfo
            {
                FileName = item.InstallerPath,
                Arguments = item.SilentArgs,
                UseShellExecute = true
            });

            if (installer is null)
            {
                AppendLog($"Failed to launch installer: {item.Definition.DisplayName}");
                _logger?.Error($"Failed to launch installer for prerequisite: {item.Definition.Id}");
                continue;
            }

            await installer.WaitForExitAsync();
            AppendLog($"Installer exit code for {item.Definition.DisplayName}: {installer.ExitCode}");
            _logger?.Info($"Installer exit code for prerequisite {item.Definition.Id}: {installer.ExitCode}");
        }

        _lastDependencyCheck = DependencyChecker.Check(_ssdRoot);
        RefreshCompatibilityUi();
        await SaveFirstRunStateAsync(promptShown: true);

        if (!_lastDependencyCheck.IsSatisfied)
        {
            AppendLog("Dependencies remain missing after install attempt.");
            return false;
        }

        return true;
    }

    private void RefreshCompatibilityUi()
    {
        var snapshot = SystemCompatibilityDetector.Detect();
        CompatibilityGpuText.Text = $"GPU: {snapshot.BestGpuSummary}";
        CompatibilityCpuText.Text = $"CPU Architecture: {snapshot.CpuArchitecture}";
        CompatibilityOsText.Text = $"OS: {snapshot.OsVersion}";
        CompatibilityDepsText.Text = _lastDependencyCheck.IsSatisfied
            ? "Dependency status: OK"
            : $"Dependency status: Missing ({string.Join(", ", _lastDependencyCheck.MissingItems.Select(m => m.DisplayName))})";
    }

    private async Task SaveFirstRunStateAsync(bool promptShown)
    {
        var statePath = Path.Combine(_ssdRoot, SsdLayout.Config, "runner-first-run.json");
        var state = RunnerFirstRunState.Load(statePath);
        state.DependencyPromptShown = promptShown;
        state.LastCheckedUtc = DateTime.UtcNow;
        await state.SaveAsync(statePath);
    }

    private void RelaunchAsAdmin(string args)
    {
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            AppendLog("Unable to relaunch as admin: executable path unavailable.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            Verb = "runas",
            UseShellExecute = true
        });

        System.Windows.Application.Current.Shutdown();
    }

    private static bool CommandLineHas(string flag)
    {
        return Environment.GetCommandLineArgs().Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRunningAsAdministrator()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
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
            if (FreeAiSsd.Shared.NetUtils.IsPortFree(port)) return port;
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
