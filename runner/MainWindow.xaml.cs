using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Runner.Services;
using Forms = System.Windows.Forms;

namespace FreeAiSsd.Runner;

/// <summary>
/// Thin UI shell for the Runner app. Delegates business logic to:
/// <see cref="IOllamaLifecycleService"/>, <see cref="IModelManagementService"/>,
/// <see cref="IDocumentOperationsService"/>, <see cref="IChatService"/>.
///
/// This class is responsible for:
/// - Wiring up services and subscribing to their events
/// - Handling UI updates (status text, combo boxes, list boxes)
/// - Showing dialogs (encryption unlock, dependency install, file pickers)
/// - Delegating button clicks to the appropriate service
///
/// Dependency checking remains here because it orchestrates multiple UI dialogs
/// and already delegates non-UI work to shared services (DependencyChecker,
/// PrereqInstallValidator).
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    private readonly IOllamaLifecycleService _ollamaService;
    private readonly IModelManagementService _modelService;
    private readonly IDocumentOperationsService _docService;
    private readonly IChatService _chatService;

    private PortableConfig? _config;
    private string _ssdRoot = string.Empty;
    private SsdLogger? _logger;
    private DependencyCheckResult _lastDependencyCheck = new(true, Array.Empty<MissingDependency>());
    private bool _isEncryptedDrive;
    private bool _isUnlocked;
    private DocumentLibraryManifest? _activeLibrary;

    public MainWindow()
    {
        InitializeComponent();

        // Detect SSD root
        _ssdRoot = AppContext.BaseDirectory;
        var baseTrimmed = _ssdRoot.TrimEnd(Path.DirectorySeparatorChar);
        if (baseTrimmed.EndsWith($"windows{Path.DirectorySeparatorChar}runner", StringComparison.OrdinalIgnoreCase))
        {
            _ssdRoot = Directory.GetParent(Directory.GetParent(baseTrimmed)!.FullName)!.FullName;
        }
        else if (baseTrimmed.EndsWith("runner", StringComparison.OrdinalIgnoreCase))
        {
            _ssdRoot = Directory.GetParent(baseTrimmed)!.FullName;
        }

        // Create shared dependencies
        _logger = new SsdLogger(_ssdRoot, "runner");
        var http = new HttpClient();
        var libraryManager = new DocumentLibraryManager(_ssdRoot);
        var documentIngestor = new DocumentIngestor(libraryManager, new EmbeddingClient(http), _logger);

        // Create services
        _ollamaService = new OllamaLifecycleService(_logger);
        _modelService = new ModelManagementService(http);
        _docService = new DocumentOperationsService(libraryManager, documentIngestor);
        _chatService = new ChatService(http, libraryManager, _logger);

        // Wire service events to UI
        _ollamaService.LogMessage += msg => AppendLog(msg);
        _ollamaService.ProcessExited += () => Dispatcher.Invoke(() => StatusText.Text = "Stopped");
        _modelService.LogMessage += msg => AppendLog(msg);
        _docService.LogMessage += msg => AppendLog(msg);
        _chatService.LogMessage += msg => AppendLog(msg);

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
    /// Checks for encryption, loads portable config, and populates the model combo.
    /// SSD root detection happens in the constructor.
    /// Uses the "fail closed" write-guard check so that a corrupt or missing
    /// encryption state file still triggers the unlock prompt rather than
    /// silently falling through to "Config not found".
    /// </summary>
    private void LoadConfig()
    {
        var isExplicitlyEncrypted = SsdEncryption.IsEncryptionEnabled(_ssdRoot);
        var isEffectivelyEncrypted = SsdEncryption.IsEffectivelyEncryptedForWriteGuard(_ssdRoot);
        _logger?.Info($"Encryption state check: explicitly={isExplicitlyEncrypted}, effectively={isEffectivelyEncrypted}");

        if (isEffectivelyEncrypted)
        {
            _isEncryptedDrive = true;
            _isUnlocked = false;
            _config = null;
            UpdateEncryptionUiState();

            if (isExplicitlyEncrypted)
            {
                StatusText.Text = "Encrypted drive locked";
                AppendLog("Encrypted drive detected. Click 'Unlock Drive' to continue.");
            }
            else
            {
                StatusText.Text = "Encryption state unclear — unlock required";
                AppendLog("Encryption state could not be read. Please unlock your SSD or reset encryption settings.");
                _logger?.Warn("Encryption state file is missing or corrupt but drive appears encrypted (fail-closed). Prompting for unlock.");
            }

            RefreshLibraryUi();
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
        RefreshLibraryUi();
        StatusText.Text = "Ready (not running)";
        AppendLog($"Loaded config from {configPath}");
    }

    private void PopulateModelCombo()
    {
        var installedModels = _config is not null
            ? _modelService.GetInstalledModelNames(_config)
            : new List<string>();
        ModelCombo.ItemsSource = installedModels;
        ModelCombo.SelectedIndex = installedModels.Count > 0 ? 0 : -1;
    }

    private void UpdateEncryptionUiState()
    {
        UnlockDriveButton.Visibility = _isEncryptedDrive ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        UnlockDriveButton.IsEnabled = _isEncryptedDrive && !_isUnlocked;
    }

    private bool TryUnlockEncryptedDrive()
    {
        if (!_isEncryptedDrive) return true;
        if (_isUnlocked && _config is not null) return true;

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
        RefreshLibraryUi();
        StatusText.Text = "Unlocked and ready";
        AppendLog("SSD unlocked successfully.");
        _ = SaveEncryptionUnlockStateAsync();
        return true;
    }

    private async void Start_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isEncryptedDrive && !TryUnlockEncryptedDrive()) return;
        if (_config is null || _ollamaService.IsRunning) return;

        var trust = _ollamaService.ValidateTrust(_ssdRoot);
        if (!trust.IsTrusted)
        {
            StatusText.Text = "Blocked: untrusted Ollama package";
            AppendLog($"Start blocked: {trust.Message}");
            return;
        }

        if (!await EnsureDependenciesReadyAsync(forcePrompt: false, userTriggered: true)) return;

        var result = _ollamaService.Start(_config, _ssdRoot);
        if (!result.Success)
        {
            StatusText.Text = result.ErrorMessage ?? "Start failed";
            AppendLog(result.ErrorMessage ?? "Start failed");
            return;
        }

        StatusText.Text = $"Running on {_ollamaService.CurrentHost}";
        await Task.Delay(1000);
    }

    private void Stop_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_ollamaService.IsRunning)
        {
            _ollamaService.Stop();
            StatusText.Text = "Stopped";
        }
    }

    private async void Send_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null || ModelCombo.SelectedItem is not string model) return;
        if (!TryGetCurrentHost(out var host)) return;

        SourcesList.ItemsSource = null;
        var response = await _chatService.SendPromptAsync(model, PromptText.Text, host, _config);
        ResponseText.Text = response.ResponseText;
        if (response.Sources is not null)
        {
            SourcesList.ItemsSource = response.Sources;
        }
    }

    private void OpenBrowser_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!TryGetCurrentHost(out var host)) return;
        Process.Start(new ProcessStartInfo { FileName = $"http://{host}", UseShellExecute = true });
    }

    private async void RerunDependencyCheck_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await EnsureDependenciesReadyAsync(forcePrompt: true, userTriggered: true);
    }

    private void OpenPrereqsFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var folder = Path.Combine(_ssdRoot, SsdLayout.Prereqs);
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    /// <summary>
    /// Shows a one-time warning dialog if installed models exceed the machine's
    /// hardware capabilities. Delegates sizing computation to IModelManagementService.
    /// </summary>
    private async Task ShowModelSizingWarningsOnStartupAsync()
    {
        if (_config is null) return;
        if (_modelService.IsSizingWarningDismissed(_ssdRoot)) return;

        var warnings = _modelService.GetModelSizingWarnings(_config);
        if (warnings.Count == 0) return;

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
            await _modelService.DismissSizingWarningAsync(_ssdRoot);
        }
    }

    /// <summary>
    /// Checks for required system dependencies and offers to install them.
    /// This remains in the UI layer because it orchestrates multiple dialogs
    /// and admin elevation. Non-UI work delegates to shared services.
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
        if (!_ollamaService.IsRunning || _ollamaService.CurrentHost is null)
        {
            var message = "Ollama is not running. Click Start Ollama first.";
            StatusText.Text = message;
            AppendLog(message);
            return false;
        }

        host = _ollamaService.CurrentHost;
        return true;
    }

    private void RefreshLibraryUi()
    {
        if (_config is null)
        {
            LibraryCombo.ItemsSource = new[] { "None" };
            LibraryCombo.SelectedIndex = 0;
            return;
        }

        var info = _docService.GetLibraryDisplayInfo(_config);
        LibraryCombo.ItemsSource = info.Options;
        LibraryCombo.SelectedIndex = info.SelectedIndex;
        _activeLibrary = info.ActiveLibrary;

        LibraryFilesList.ItemsSource = _activeLibrary?.Files ?? new List<DocumentFileEntry>();
        IndexingStatusText.Text = _activeLibrary?.LastIndexedUtc is null
            ? "No indexing run yet."
            : $"Last indexed: {_activeLibrary.LastIndexedUtc:u}";
    }

    private async Task<bool> EnsureActiveLibraryAsync()
    {
        var selectedId = _docService.GetLibraryIdByIndex(LibraryCombo.SelectedIndex);
        if (_config is null)
        {
            _activeLibrary = null;
            return false;
        }

        _activeLibrary = await _docService.SetActiveLibraryAsync(_config, _ssdRoot, selectedId);
        LibraryFilesList.ItemsSource = _activeLibrary?.Files ?? new List<DocumentFileEntry>();
        return _activeLibrary is not null;
    }

    private void LibraryCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _ = EnsureActiveLibraryAsync();
    }

    private async void CreateLibrary_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null) return;

        var name = string.IsNullOrWhiteSpace(NewLibraryNameText.Text) ? "Library" : NewLibraryNameText.Text.Trim();
        await _docService.CreateLibraryAsync(_config, _ssdRoot, name);
        RefreshLibraryUi();
    }

    private async void AddFiles_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!await EnsureActiveLibraryAsync() || _activeLibrary is null || _config is null)
        {
            AppendLog("Select a document library first.");
            return;
        }

        if (!TryGetCurrentHost(out var host)) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Supported|*.pdf;*.txt;*.md;*.json;*.csv|All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            IndexingStatusText.Text = "Indexing...";
            await _docService.IngestFilesAsync(_activeLibrary, dlg.FileNames, host, _config, p =>
            {
                Dispatcher.Invoke(() => IndexingStatusText.Text = $"Indexing {p.CompletedFiles}/{p.TotalFiles}: {p.CurrentFile}");
            });
            RefreshLibraryUi();
        }
        catch (Exception ex)
        {
            AppendLog($"Indexing failed: {ex.Message}");
            IndexingStatusText.Text = "Indexing failed. Missing embedding model?";
        }
    }

    private async void AddFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!await EnsureActiveLibraryAsync() || _activeLibrary is null)
        {
            AppendLog("Select a document library first.");
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;

        var added = await _docService.AddWatchedFolderAsync(_activeLibrary, dialog.SelectedPath);
        if (added)
        {
            IndexingStatusText.Text = $"Added sweep folder: {dialog.SelectedPath}";
        }
    }

    private async void SweepFolders_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!await EnsureActiveLibraryAsync() || _activeLibrary is null || _config is null)
        {
            AppendLog("Select a document library first.");
            return;
        }

        if (!TryGetCurrentHost(out var host)) return;

        try
        {
            await _docService.SweepFoldersAsync(_activeLibrary, host, _config, p =>
            {
                Dispatcher.Invoke(() => IndexingStatusText.Text = $"Sweep {p.CompletedFiles}/{p.TotalFiles}: {p.CurrentFile}");
            });
            RefreshLibraryUi();
        }
        catch (Exception ex)
        {
            AppendLog($"Sweep failed: {ex.Message}");
        }
    }

    private async void RebuildIndex_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!await EnsureActiveLibraryAsync() || _activeLibrary is null || _config is null)
        {
            AppendLog("Select a document library first.");
            return;
        }

        if (!TryGetCurrentHost(out var host)) return;

        try
        {
            await _docService.RebuildIndexAsync(_activeLibrary, host, _config, p =>
            {
                Dispatcher.Invoke(() => IndexingStatusText.Text = $"Rebuild {p.CompletedFiles}/{p.TotalFiles}: {p.CurrentFile}");
            });
            RefreshLibraryUi();
        }
        catch (Exception ex)
        {
            AppendLog($"Rebuild failed: {ex.Message}");
        }
    }

    private async void PullEmbeddingModel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null) return;

        if (!TryGetCurrentHost(out var host))
        {
            AppendLog("Start Ollama before pulling embedding model.");
            return;
        }

        var success = await _modelService.PullEmbeddingModelAsync(host, _config.EmbeddingModelName);
        IndexingStatusText.Text = success
            ? $"Embedding model ready: {_config.EmbeddingModelName}"
            : "Unable to pull embedding model while offline. Connect temporarily and retry.";
    }

    private async void RemoveFile_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!await EnsureActiveLibraryAsync() || _activeLibrary is null) return;

        if (LibraryFilesList.SelectedItem is DocumentFileEntry file)
        {
            await _docService.RemoveFileAsync(_activeLibrary, file.StoredRelativePath);
            RefreshLibraryUi();
        }
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
