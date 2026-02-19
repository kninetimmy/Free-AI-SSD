using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Principal;
using System.Text.Json;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner;

/// <summary>
/// Main window code-behind for the Runner app — the end-user tool that runs
/// on the destination machine (offline PC). Manages the Ollama lifecycle:
///
/// - SSD root auto-detection (navigates up from windows/runner or runner directory)
/// - Encrypted drive unlock via AES-256-GCM password dialog
/// - Dependency checking and offline prerequisite installation (VC++, .NET)
/// - Ollama process launch with SSD-relative model/config paths
/// - Simple chat interface: sends prompts to Ollama's /api/generate endpoint
/// - Hardware compatibility display (GPU, CPU, OS, dependency status)
/// - Model sizing warnings on first run (dismissible per-machine)
/// - Admin elevation for prerequisite installers that require it
///
/// Architecture note: Like PrepApp, this file mixes UI state and business logic.
/// A service layer would improve testability.
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    /// <summary>HTTP client for Ollama API requests (generate, etc.).</summary>
    private readonly HttpClient _http = new();
    /// <summary>Loaded portable config (null if encrypted and not yet unlocked).</summary>
    private PortableConfig? _config;
    /// <summary>Detected SSD root directory (parent of windows/runner).</summary>
    private string _ssdRoot = string.Empty;
    /// <summary>The running Ollama server process (null when stopped).</summary>
    private Process? _ollama;
    /// <summary>File logger writing to the SSD's logs directory.</summary>
    private SsdLogger? _logger;
    /// <summary>The port Ollama is currently serving on (null when stopped).</summary>
    private int? _currentPort;
    /// <summary>Result of the last dependency check (VC++, .NET runtime presence).</summary>
    private DependencyCheckResult _lastDependencyCheck = new(true, Array.Empty<MissingDependency>());
    /// <summary>True if the SSD has encryption enabled.</summary>
    private bool _isEncryptedDrive;
    /// <summary>True after the user successfully enters the encryption password.</summary>
    private bool _isUnlocked;

    /// <summary>
    /// Initializes the Runner: loads config (or enters encrypted mode),
    /// shows model sizing warnings, detects hardware, and checks dependencies.
    /// </summary>
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

    /// <summary>
    /// Auto-detects the SSD root by navigating up from the Runner's executable directory.
    /// If the drive is encrypted, enters locked mode (config is null until unlock).
    /// Otherwise loads the portable config and populates the model combo box.
    /// </summary>
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

        _logger = new SsdLogger(_ssdRoot, "runner");
        if (SsdEncryption.IsEncryptionEnabled(_ssdRoot))
        {
            _isEncryptedDrive = true;
            _isUnlocked = false;
            _config = null;
            UpdateEncryptionUiState();
            StatusText.Text = "Encrypted drive locked";
            AppendLog("Encrypted drive detected. Click 'Unlock Drive' to continue.");
            return;
        }

        _isEncryptedDrive = false;
        _isUnlocked = true;
        UpdateEncryptionUiState();

        var configPath = Path.Combine(_ssdRoot, "config", "portable-config.json");
        if (!File.Exists(configPath))
        {
            StatusText.Text = "Config not found";
            AppendLog($"Missing config at {configPath}");
            return;
        }

        _config = PortableConfig.Load(configPath);
        PopulateModelCombo();
        StatusText.Text = "Ready (not running)";
        AppendLog($"Loaded config from {configPath}");
    }

    private void PopulateModelCombo()
    {
        var installedModels = _config?.Models
            .Where(m => m.Status == ModelInstallStatus.Installed)
            .Select(m => m.Name)
            .ToList() ?? new List<string>();
        ModelCombo.ItemsSource = installedModels;
        ModelCombo.SelectedIndex = installedModels.Count > 0 ? 0 : -1;
    }

    private void UpdateEncryptionUiState()
    {
        UnlockDriveButton.Visibility = _isEncryptedDrive ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        UnlockDriveButton.IsEnabled = _isEncryptedDrive && !_isUnlocked;
    }

    /// <summary>
    /// Prompts the user for their encryption password and attempts to decrypt
    /// the portable config using AES-256-GCM. On success, loads the config
    /// and enables all Runner functionality. On failure, shows the error.
    /// </summary>
    private bool TryUnlockEncryptedDrive()
    {
        if (!_isEncryptedDrive)
        {
            return true;
        }

        if (_isUnlocked && _config is not null)
        {
            return true;
        }

        var dialog = new UnlockDriveDialog { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            StatusText.Text = "Encrypted drive locked";
            AppendLog("Unlock cancelled.");
            return false;
        }

        if (!SsdEncryption.TryUnlockPortableConfig(_ssdRoot, dialog.Password, out var unlockedConfig, out var error) || unlockedConfig is null)
        {
            StatusText.Text = "Unlock failed";
            AppendLog($"Unlock failed: {error}");
            return false;
        }

        _config = unlockedConfig;
        _isUnlocked = true;
        UpdateEncryptionUiState();
        PopulateModelCombo();
        StatusText.Text = "Unlocked and ready";
        AppendLog("SSD unlocked successfully.");
        _ = SaveEncryptionUnlockStateAsync();
        return true;
    }

    /// <summary>
    /// Starts the Ollama server process. Validates trust attestation, checks
    /// dependencies, finds a free port (starting from config's preferred port),
    /// and launches ollama serve with SSD-relative environment variables.
    /// OLLAMA_MODELS points to the SSD's models directory.
    /// OLLAMA_HOST binds to 127.0.0.1 (localhost only, not network-exposed).
    /// </summary>
    private async void Start_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isEncryptedDrive && !TryUnlockEncryptedDrive())
        {
            return;
        }

        if (_config is null || _ollama is { HasExited: false }) return;

        var trustGate = OllamaPackageTrustPolicy.ValidateExecutionAttestation(_ssdRoot, OllamaPackageTrustPolicy.DefaultWindowsPackage.Url);
        if (!trustGate.IsTrusted)
        {
            StatusText.Text = "Blocked: untrusted Ollama package";
            AppendLog($"Start blocked: {trustGate.Message}");
            return;
        }

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

    /// <summary>
    /// Sends a prompt to the running Ollama instance via its /api/generate endpoint.
    /// Uses the selected model from the combo box and displays the response text.
    /// stream=false for simplicity (waits for complete response).
    /// </summary>
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

    /// <summary>
    /// Shows a one-time warning dialog if installed models exceed the machine's
    /// hardware capabilities (RAM, VRAM). The user can dismiss permanently
    /// by selecting "Don't show again", which persists to runner-first-run.json.
    /// </summary>
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

    /// <summary>
    /// Checks for required system dependencies (VC++ runtime, .NET runtime) and
    /// offers to install them from the SSD's bundled prerequisite installers.
    /// If admin privileges are needed, offers to relaunch with elevation.
    /// Validates installer integrity (SHA-256) before execution.
    /// On first run: shows install dialog; on subsequent runs: only shows if forced.
    /// </summary>
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

    private async Task SaveEncryptionUnlockStateAsync()
    {
        var statePath = Path.Combine(_ssdRoot, SsdLayout.Config, "runner-first-run.json");
        var state = RunnerFirstRunState.Load(statePath);
        state.EncryptionUnlockedAtUtc = DateTime.UtcNow;
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

    /// <summary>
    /// Finds a free port starting from the preferred port, scanning up to 20 ports.
    /// Used to avoid conflicts if the default Ollama port (11434) is already in use.
    /// </summary>
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

    private void UnlockDrive_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        TryUnlockEncryptedDrive();
    }
}
