using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Threading;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.UI.Theme;
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
    private readonly IDcsBindingsImportService _dcsImportService;
    private readonly ISpeechToTextService _sttService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IRunnerLocalApiService _localApiService;
    private readonly ITtsProvider _ttsProvider;
    private ITextToSpeechService? _ttsService;

    private PortableConfig? _config;
    private readonly string _ssdRoot;
    private readonly SsdLogger? _logger;
    private DependencyCheckResult _lastDependencyCheck = new(true, Array.Empty<MissingDependency>());
    private bool _isEncryptedDrive;
    private bool _isUnlocked;
    private DocumentLibraryManifest? _activeLibrary;
    private CancellationTokenSource? _streamingCts;
    private StreamingTtsSpeaker? _ttsSpeaker;
    private bool _isVoiceRecording;

    // Push-to-Talk (HOTAS) state
    private readonly IHotasInputService _hotasService;
    private readonly PttVoicePipelineService _pttPipeline;
    private PttOverlayWindow? _pttOverlay;
    private bool _pttToggleActive; // for toggle mode: tracks whether currently recording
    private bool _pttDetecting;    // true while "press any button" detection is active

    // Bindings import wizard state
    private DcsInstallation? _dcsInstallation;
    private IReadOnlyList<DcsAircraftInfo> _scannedAircraft = Array.Empty<DcsAircraftInfo>();
    private List<DcsAircraftImportItem> _aircraftItems = new();
    private CancellationTokenSource? _importCts;

    public MainWindow(
        string ssdRoot,
        SsdLogger logger,
        IOllamaLifecycleService ollamaService,
        IModelManagementService modelService,
        IDocumentOperationsService docService,
        IChatService chatService,
        IDcsBindingsImportService dcsImportService,
        ISpeechToTextService sttService,
        IAudioCaptureService audioCaptureService,
        IHotasInputService hotasService,
        PttVoicePipelineService pttPipeline,
        IRunnerLocalApiService localApiService,
        ITtsProvider ttsProvider)
    {
        InitializeComponent();

        _ssdRoot = ssdRoot;
        _logger = logger;
        _ollamaService = ollamaService;
        _modelService = modelService;
        _docService = docService;
        _chatService = chatService;
        _dcsImportService = dcsImportService;
        _sttService = sttService;
        _audioCaptureService = audioCaptureService;
        _hotasService = hotasService;
        _pttPipeline = pttPipeline;
        _localApiService = localApiService;
        _ttsProvider = ttsProvider;

        // Wire service events to UI
        _ollamaService.LogMessage += msg => AppendLog(msg);
        _ollamaService.ProcessExited += () => Dispatcher.Invoke(async () =>
        {
            await _localApiService.StopAsync();
            StatusText.Text = "Stopped";
            OllamaStatusLed.State = LedState.Idle;
        });
        _modelService.LogMessage += msg => AppendLog(msg);
        _docService.LogMessage += msg => AppendLog(msg);
        _chatService.LogMessage += msg => AppendLog(msg);
        _dcsImportService.LogMessage += msg => AppendLog(msg);
        _sttService.LogMessage += msg => AppendLog(msg);
        _audioCaptureService.LogMessage += msg => AppendLog(msg);
        _hotasService.LogMessage += msg => AppendLog(msg);
        _pttPipeline.LogMessage += msg => AppendLog(msg);
        _localApiService.LogMessage += msg => AppendLog(msg);

        // PTT pipeline events: update overlay and main UI
        _pttPipeline.StateChanged += state => Dispatcher.Invoke(() =>
        {
            _pttOverlay?.UpdateState(state);
            PttStatusText.Text = state switch
            {
                PttState.Idle => "Ready",
                PttState.Listening => "Listening...",
                PttState.Thinking => "Transcribing / querying AI...",
                PttState.Speaking => "Speaking response...",
                _ => ""
            };
        });
        _pttPipeline.TranscriptionReady += text => Dispatcher.Invoke(() => PromptText.Text = text);
        _pttPipeline.ResponseTokenReceived += token => Dispatcher.Invoke(() => ResponseText.AppendText(token));
        _pttPipeline.ResponseComplete += text => Dispatcher.Invoke(() => ResponseText.Text = text);

        // HOTAS button events: drive the PTT pipeline
        _hotasService.PttButtonPressed += () => Dispatcher.Invoke(() => OnPttButtonPressed());
        _hotasService.PttButtonReleased += () => Dispatcher.Invoke(() => OnPttButtonReleased());

        Closed += async (_, _) =>
        {
            CleanupPtt();
            await _localApiService.StopAsync();
            _sttService.Dispose();
        };

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
        InitializeTts();
        InitializePtt();
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
        InitializeTts();
        InitializePtt();
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
            OllamaStatusLed.State = LedState.Error;
            AppendLog($"Start blocked: {trust.Message}");
            return;
        }

        OllamaStatusLed.State = LedState.Busy;
        if (!await EnsureDependenciesReadyAsync(forcePrompt: false, userTriggered: true))
        {
            OllamaStatusLed.State = LedState.Idle;
            return;
        }

        var result = _ollamaService.Start(_config, _ssdRoot);
        if (!result.Success)
        {
            StatusText.Text = result.ErrorMessage ?? "Start failed";
            OllamaStatusLed.State = LedState.Error;
            AppendLog(result.ErrorMessage ?? "Start failed");
            return;
        }

        StatusText.Text = $"Running on {_ollamaService.CurrentHost}";
        OllamaStatusLed.State = LedState.Ok;
        await StartLocalApiIfEnabledAsync();
        await Task.Delay(1000);
    }

    private void Stop_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_ollamaService.IsRunning)
        {
            _ = _localApiService.StopAsync();
            _ollamaService.Stop();
            StatusText.Text = "Stopped";
            OllamaStatusLed.State = LedState.Idle;
        }
    }

    private async Task StartLocalApiIfEnabledAsync()
    {
        if (_config is null || _ollamaService.CurrentHost is null)
        {
            return;
        }

        if (!_config.NetworkModeEnabled)
        {
            await _localApiService.StopAsync();
            return;
        }

        try
        {
            await _localApiService.StartAsync(_config, _ollamaService.CurrentHost);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to start Network API: {ex.Message}");
            _logger?.Error($"Network API start failure: {ex}");
        }
    }

    private async void Send_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null || ModelCombo.SelectedItem is not string model) return;
        if (!TryGetCurrentHost(out var host)) return;

        // Interrupt any ongoing TTS from the previous response
        StopTts();

        SourcesList.ItemsSource = null;

        if (_config.UseStreamingChat)
        {
            await SendStreamingAsync(model, host);
        }
        else
        {
            SendButton.IsEnabled = false;
            try
            {
                var response = await _chatService.SendPromptAsync(model, PromptText.Text, host, _config);
                ResponseText.Text = response.ResponseText;
                if (response.Sources is not null)
                {
                    SourcesList.ItemsSource = response.Sources;
                }

                // Speak the complete response
                SpeakResponseAsync(response.ResponseText);
            }
            finally
            {
                SendButton.IsEnabled = true;
            }
        }
    }

    private async Task SendStreamingAsync(string model, string host)
    {
        _streamingCts?.Cancel();
        _streamingCts = new CancellationTokenSource();
        var ct = _streamingCts.Token;

        SendButton.IsEnabled = false;
        StopButton.Visibility = System.Windows.Visibility.Visible;
        StreamingIndicator.Visibility = System.Windows.Visibility.Visible;
        ResponseText.Text = string.Empty;

        // Start a sentence-buffered TTS speaker for this streaming response
        var ttsSpeaker = BeginStreamingTts();

        try
        {
            var response = await _chatService.SendPromptStreamingAsync(
                model, PromptText.Text, host, _config!,
                token =>
                {
                    Dispatcher.Invoke(() => ResponseText.AppendText(token));
                    ttsSpeaker?.FeedToken(token);
                },
                ct);

            // Signal end of stream so any buffered trailing text is spoken
            ttsSpeaker?.Finish();

            // Store the final assembled text (handles cancellation partial text)
            ResponseText.Text = response.ResponseText;
            if (response.Sources is not null)
            {
                SourcesList.ItemsSource = response.Sources;
            }
        }
        catch (Exception ex)
        {
            ttsSpeaker?.Cancel();
            AppendLog($"Streaming error: {ex.Message}");
            // Fall back to non-streaming
            if (string.IsNullOrEmpty(ResponseText.Text))
            {
                AppendLog("Falling back to non-streaming mode.");
                try
                {
                    var fallback = await _chatService.SendPromptAsync(model, PromptText.Text, host, _config!);
                    ResponseText.Text = fallback.ResponseText;
                    if (fallback.Sources is not null)
                    {
                        SourcesList.ItemsSource = fallback.Sources;
                    }

                    SpeakResponseAsync(fallback.ResponseText);
                }
                catch (Exception fallbackEx)
                {
                    AppendLog($"Fallback also failed: {fallbackEx.Message}");
                }
            }
        }
        finally
        {
            SendButton.IsEnabled = true;
            StopButton.Visibility = System.Windows.Visibility.Collapsed;
            StreamingIndicator.Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    private void Stop_Generation_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _streamingCts?.Cancel();
        StopTts();
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
            UpdateLibraryActionButtons();
            return;
        }

        AppendLog("Refreshing library list...");
        var info = _docService.GetLibraryDisplayInfo(_config);
        LibraryCombo.ItemsSource = info.Options;
        LibraryCombo.SelectedIndex = info.SelectedIndex;
        _activeLibrary = info.ActiveLibrary;
        AppendLog($"Library list refreshed ({Math.Max(info.Options.Count - 1, 0)} libraries).");
        if (_activeLibrary is not null)
        {
            AppendLog($"Selected library: {_activeLibrary.Name} ({_activeLibrary.Id})");
        }

        LibraryFilesList.ItemsSource = _activeLibrary?.Files ?? new List<DocumentFileEntry>();
        IndexingStatusText.Text = _activeLibrary?.LastIndexedUtc is null
            ? "No indexing run yet."
            : $"Last indexed: {_activeLibrary.LastIndexedUtc:u}";
        UpdateLibraryActionButtons();
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
        UpdateLibraryActionButtons();
        return _activeLibrary is not null;
    }

    private void LibraryCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _ = EnsureActiveLibraryAsync();
    }

    private async void CreateLibrary_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null) return;

        var requestedName = NewLibraryNameText.Text ?? string.Empty;
        try
        {
            var created = await _docService.CreateLibraryAsync(_config, _ssdRoot, requestedName);
            RefreshLibraryUi();
            await _docService.SetActiveLibraryAsync(_config, _ssdRoot, created.Id);
            RefreshLibraryUi();
            IndexingStatusText.Text = $"Created and selected library: {created.Name}";
            AppendLog($"Created and selected library: {created.Name}");
        }
        catch (ArgumentException ex)
        {
            AppendLog(ex.Message);
            IndexingStatusText.Text = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            AppendLog(ex.Message);
            IndexingStatusText.Text = ex.Message;
        }
    }

    private void UpdateLibraryActionButtons()
    {
        var enabled = _activeLibrary is not null;
        AddFilesButton.IsEnabled = enabled;
        AddFolderButton.IsEnabled = enabled;
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

    // ─────────────────────────────────────────────────────────────────────────
    // Text-to-Speech output
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates and configures the TTS service based on the current config.
    /// Called after config load and any subsequent config reload/unlock.
    /// </summary>
    private void InitializeTts()
    {
        _ttsService?.Dispose();
        _ttsService = null;
        _ttsProvider.SetCurrent(null);

        if (_config is null) return;

        try
        {
            if (string.Equals(_config.TtsEngine, "piper", StringComparison.OrdinalIgnoreCase))
            {
                var piper = new PiperTextToSpeechService(_ssdRoot);
                piper.OutputDeviceName = _config.TtsOutputDevice;
                _ttsService = piper;
            }
            else
            {
                var system = new SystemTextToSpeechService();
                system.OutputDeviceName = _config.TtsOutputDevice;
                _ttsService = system;
            }

            _ttsService.LogMessage += msg => AppendLog(msg);
            _ttsService.SetRate(_config.TtsRate);
            _ttsService.SetVolume(_config.TtsVolume);

            if (!string.IsNullOrWhiteSpace(_config.TtsVoiceName))
            {
                _ttsService.SetVoice(_config.TtsVoiceName);
            }

            if (_config.TtsEnabled)
            {
                AppendLog($"TTS enabled (engine: {_config.TtsEngine})");
            }

            _ttsProvider.SetCurrent(_ttsService);
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to initialize TTS: {ex.Message}");
            try { _ttsService?.Dispose(); } catch { /* swallow secondary disposal errors */ }
            _ttsService = null;
            _ttsProvider.SetCurrent(null);
        }
    }

    /// <summary>
    /// Starts a <see cref="StreamingTtsSpeaker"/> for sentence-by-sentence speech
    /// during a streaming LLM response. Returns null if TTS is disabled.
    /// </summary>
    private StreamingTtsSpeaker? BeginStreamingTts()
    {
        _ttsSpeaker?.Cancel();
        _ttsSpeaker?.Dispose();
        _ttsSpeaker = null;

        if (_ttsService is null || _config is not { TtsEnabled: true })
            return null;

        _ttsSpeaker = new StreamingTtsSpeaker(_ttsService);
        return _ttsSpeaker;
    }

    /// <summary>
    /// Speaks a complete (non-streaming) response asynchronously if TTS is enabled.
    /// Fire-and-forget — errors are logged.
    /// </summary>
    private void SpeakResponseAsync(string responseText)
    {
        if (_ttsService is null || _config is not { TtsEnabled: true }) return;
        if (string.IsNullOrWhiteSpace(responseText)) return;

        _ = _ttsService.SpeakAsync(responseText);
    }

    /// <summary>
    /// Immediately interrupts any TTS that is currently speaking.
    /// Called when the user starts a new query or clicks stop.
    /// </summary>
    private void StopTts()
    {
        _ttsSpeaker?.Cancel();
        _ttsSpeaker?.Dispose();
        _ttsSpeaker = null;

        _ttsService?.Stop();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Voice input (Speech-to-Text)
    // ─────────────────────────────────────────────────────────────────────────

    private async void Voice_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null) return;

        // Interrupt TTS when user starts speaking — they're beginning a new interaction
        if (!_isVoiceRecording)
        {
            StopTts();
        }

        if (_isVoiceRecording)
        {
            await StopVoiceRecordingAsync();
        }
        else
        {
            await StartVoiceRecordingAsync();
        }
    }

    private async Task StartVoiceRecordingAsync()
    {
        if (_config is null) return;

        // Ensure the Whisper model is loaded
        if (!_sttService.IsModelLoaded)
        {
            SetVoiceStatus("Loading Whisper model...");
            try
            {
                await _sttService.InitializeAsync(_ssdRoot, _config);
            }
            catch (Exception ex)
            {
                SetVoiceStatus(null);
                AppendLog($"Voice input unavailable: {ex.Message}");
                return;
            }
        }

        try
        {
            _audioCaptureService.StartRecording(_config.SelectedMicrophoneDevice);
            _isVoiceRecording = true;
            VoiceButton.Content = "⏹ Stop";
            SetVoiceStatus("Listening...");
        }
        catch (Exception ex)
        {
            SetVoiceStatus(null);
            AppendLog($"Microphone error: {ex.Message}");
        }
    }

    private async Task StopVoiceRecordingAsync()
    {
        _isVoiceRecording = false;
        VoiceButton.Content = "🎤 Voice";
        VoiceButton.IsEnabled = false;
        SetVoiceStatus("Transcribing...");

        byte[] audioData;
        try
        {
            audioData = _audioCaptureService.StopRecording();
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to stop recording: {ex.Message}");
            VoiceButton.IsEnabled = true;
            SetVoiceStatus(null);
            return;
        }

        if (audioData.Length == 0)
        {
            AppendLog("No audio captured.");
            VoiceButton.IsEnabled = true;
            SetVoiceStatus(null);
            return;
        }

        try
        {
            var text = await _sttService.TranscribeAudioAsync(audioData);

            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog("No speech detected in recording.");
                VoiceButton.IsEnabled = true;
                SetVoiceStatus("Ready");
                return;
            }

            PromptText.Text = text;

            if (_config!.AutoSendVoiceInput)
            {
                SetVoiceStatus("Sending...");
                Send_Click(this, new System.Windows.RoutedEventArgs());
            }

            SetVoiceStatus("Ready");
        }
        catch (Exception ex)
        {
            AppendLog($"Transcription failed: {ex.Message}");
            SetVoiceStatus(null);
        }
        finally
        {
            VoiceButton.IsEnabled = true;
        }
    }

    private void SetVoiceStatus(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            VoiceStatusText.Visibility = System.Windows.Visibility.Collapsed;
        }
        else
        {
            VoiceStatusText.Text = $"🎤 {text}";
            VoiceStatusText.Visibility = System.Windows.Visibility.Visible;
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

    // ─────────────────────────────────────────────────────────────────────────
    // Bindings Import wizard
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Step 0 → 1: User chose DCS. Detect folder and show detection result.</summary>
    private void BindingsDcsGame_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _dcsInstallation = _dcsImportService.DetectDcsInstallation();

        if (_dcsInstallation is not null)
        {
            var variantLabel = _dcsInstallation.Variant == DcsSavedGamesVariant.DCSOpenBeta
                ? "Open Beta"
                : "Stable";
            DcsFolderStatusText.Text =
                $"✔ Found DCS {variantLabel}\n{_dcsInstallation.SavedGamesPath}";
            DcsFolderStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
            BindingsStep1NextButton.IsEnabled = true;
        }
        else
        {
            DcsFolderStatusText.Text =
                "DCS saved games folder not found in the standard location.\n" +
                "Use 'Browse manually' to select it.";
            DcsFolderStatusText.Foreground = System.Windows.Media.Brushes.DarkRed;
            BindingsStep1NextButton.IsEnabled = false;
        }

        ShowBindingsStep(1);
    }

    /// <summary>Step 1: Let the user pick the DCS folder manually.</summary>
    private void BindingsBrowseFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select your DCS saved games folder (e.g. Saved Games\\DCS)"
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return;

        try
        {
            _dcsInstallation = _dcsImportService.WithManualPath(dialog.SelectedPath);
            DcsFolderStatusText.Text = $"✔ Using: {dialog.SelectedPath}";
            DcsFolderStatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
            BindingsStep1NextButton.IsEnabled = true;
        }
        catch (ArgumentException ex)
        {
            AppendLog($"Invalid folder: {ex.Message}");
        }
    }

    /// <summary>Step 1 → 2: Scan for aircraft.</summary>
    private void BindingsStep1Next_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_dcsInstallation is null) return;

        _scannedAircraft = _dcsImportService.ScanAircraft(_dcsInstallation);

        if (_scannedAircraft.Count == 0)
        {
            AppendLog("No aircraft binding folders found in the selected DCS path.");
            DcsFolderStatusText.Text +=
                "\n\nNo aircraft found. Check that Config/Input exists in this folder.";
            DcsFolderStatusText.Foreground = System.Windows.Media.Brushes.DarkRed;
            return;
        }

        // Determine which are already imported (only if a library is active)
        IReadOnlySet<string> alreadyImported = new HashSet<string>();
        if (_activeLibrary is not null)
            alreadyImported = _dcsImportService.GetAlreadyImportedFolderNames(
                _scannedAircraft, _ssdRoot, _activeLibrary.Id);

        _aircraftItems = _scannedAircraft.Select(a => new DcsAircraftImportItem
        {
            Aircraft = a,
            IsSelected = a.HasBindings,
            AlreadyImported = alreadyImported.Contains(a.FolderName),
        }).ToList();

        AircraftList.ItemsSource = _aircraftItems;

        var withBindings = _scannedAircraft.Count(a => a.HasBindings);
        AircraftScanSummaryText.Text =
            $"{_scannedAircraft.Count} aircraft found, {withBindings} with custom bindings.";

        ShowBindingsStep(2);
    }

    /// <summary>Step 1 ← Back to step 0.</summary>
    private void BindingsStep1Back_Click(object sender, System.Windows.RoutedEventArgs e) =>
        ShowBindingsStep(0);

    /// <summary>Step 2: Select All aircraft that have bindings.</summary>
    private void BindingsSelectAll_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        foreach (var item in _aircraftItems.Where(i => i.CanImport))
            item.IsSelected = true;
        AircraftList.Items.Refresh();
    }

    /// <summary>Step 2: Deselect all aircraft.</summary>
    private void BindingsDeselectAll_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        foreach (var item in _aircraftItems)
            item.IsSelected = false;
        AircraftList.Items.Refresh();
    }

    /// <summary>Step 2 ← Back to step 1.</summary>
    private void BindingsStep2Back_Click(object sender, System.Windows.RoutedEventArgs e) =>
        ShowBindingsStep(1);

    /// <summary>Step 2 → 3: Run the import.</summary>
    private async void BindingsImport_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var selected = _aircraftItems.Where(i => i.IsSelected).Select(i => i.Aircraft).ToList();
        if (selected.Count == 0)
        {
            AppendLog("No aircraft selected. Check at least one aircraft to import.");
            return;
        }

        if (_activeLibrary is null)
        {
            AppendLog("No document library selected. Create or select a library in the Reference Documents section first.");
            System.Windows.MessageBox.Show(
                "Please create or select a document library before importing bindings.",
                "No library selected",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        await RunImportAsync(selected);
    }

    /// <summary>Step 3: Re-run import with the same selection (overwrites existing files).</summary>
    private async void BindingsImportAgain_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var selected = _aircraftItems.Where(i => i.IsSelected).Select(i => i.Aircraft).ToList();
        if (selected.Count == 0) return;
        if (_activeLibrary is null) return;

        await RunImportAsync(selected);
    }

    /// <summary>Step 3: Start over from step 0.</summary>
    private void BindingsStartOver_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _importCts?.Cancel();
        _dcsInstallation = null;
        _scannedAircraft = Array.Empty<DcsAircraftInfo>();
        _aircraftItems = new();
        AircraftList.ItemsSource = null;
        ShowBindingsStep(0);
    }

    private async Task RunImportAsync(List<DcsAircraftInfo> selected)
    {
        _importCts?.Cancel();
        _importCts = new CancellationTokenSource();
        var ct = _importCts.Token;

        ShowBindingsStep(3);
        BindingsResultText.Text = string.Empty;
        BindingsErrorText.Visibility = System.Windows.Visibility.Collapsed;
        BindingsProgressText.Text = $"Importing {selected.Count} aircraft…";
        BindingsImportButton.IsEnabled = false;

        DcsBatchSummary summary;
        try
        {
            summary = await _dcsImportService.ImportBindingsAsync(
                selected,
                _ssdRoot,
                _activeLibrary!.Id,
                friendlyName => Dispatcher.Invoke(() =>
                    BindingsProgressText.Text = $"Processing: {friendlyName}…"),
                ct);
        }
        catch (OperationCanceledException)
        {
            BindingsProgressText.Text = "Import cancelled.";
            BindingsImportButton.IsEnabled = true;
            return;
        }
        catch (Exception ex)
        {
            BindingsProgressText.Text = "Import failed.";
            BindingsResultText.Text = string.Empty;
            BindingsErrorText.Text = $"Error: {ex.Message}";
            BindingsErrorText.Visibility = System.Windows.Visibility.Visible;
            BindingsImportButton.IsEnabled = true;
            AppendLog($"Bindings import error: {ex.Message}");
            return;
        }

        BindingsProgressText.Text = string.Empty;
        BindingsResultText.Text = summary.Failed == 0
            ? $"Imported {summary.Succeeded}/{selected.Count} aircraft successfully."
            : $"Imported {summary.Succeeded}/{selected.Count} aircraft. {summary.Failed} failed.";

        if (summary.Failed > 0)
        {
            var failedNames = summary.Results
                .Where(r => !r.Success)
                .Select(r => $"  • {r.AircraftFriendlyName}: {r.FailureReason}");
            BindingsErrorText.Text = string.Join("\n", failedNames);
            BindingsErrorText.Visibility = System.Windows.Visibility.Visible;
        }

        BindingsImportButton.IsEnabled = true;
        RefreshLibraryUi();
    }

    /// <summary>Shows one wizard step panel and collapses the others.</summary>
    private void ShowBindingsStep(int step)
    {
        BindingsStep0.Visibility = step == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        BindingsStep1.Visibility = step == 1
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        BindingsStep2.Visibility = step == 2
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        BindingsStep3.Visibility = step == 3
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Push-to-Talk (HOTAS Voice Input)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configures the PTT pipeline and HOTAS input service based on the current config.
    /// Also populates the PTT config UI controls and starts the overlay if enabled.
    /// Called after config load or unlock.
    /// </summary>
    private void InitializePtt()
    {
        if (_config is null) return;

        // Configure the pipeline with current services
        _pttPipeline.Configure(
            _ttsService,
            _config,
            _ssdRoot,
            getModel: () => ModelCombo.SelectedItem as string ?? "",
            getHost: () => _ollamaService.CurrentHost);

        // Populate UI from config
        PttEnabledCheck.IsChecked = _config.PttEnabled;
        PttSoundCheck.IsChecked = _config.PttActivationSoundEnabled;
        PttOverlayCheck.IsChecked = _config.PttOverlayEnabled;
        PttModeHold.IsChecked = _config.PttMode != "toggle";
        PttModeToggle.IsChecked = _config.PttMode == "toggle";

        if (_config.PttButtonIndex >= 0 && _config.PttDeviceName is not null)
        {
            PttButtonLabel.Text = $"Button {_config.PttButtonIndex} on {_config.PttDeviceName}";
        }

        RefreshPttDeviceList();

        // Start HOTAS polling if PTT is enabled and configured
        if (_config.PttEnabled && _config.PttDeviceName is not null)
        {
            StartPtt();
        }

        PttStatusText.Text = _config.PttEnabled ? "Ready" : "Disabled";
    }

    private void StartPtt()
    {
        if (_config is null) return;

        _hotasService.Stop();

        if (_config.PttDeviceName is not null)
        {
            _hotasService.Start(_config.PttDeviceName, _config.PttButtonIndex);
        }

        // Show overlay if enabled
        if (_config.PttOverlayEnabled)
        {
            ShowPttOverlay();
        }
    }

    private void StopPtt()
    {
        _hotasService.Stop();
        _pttPipeline.Cancel();
        _pttToggleActive = false;
        HidePttOverlay();
    }

    private void CleanupPtt()
    {
        _hotasService.Stop();
        _pttPipeline.Cancel();
        _hotasService.Dispose();
        _pttPipeline.Dispose();
        HidePttOverlay();
    }

    private void ShowPttOverlay()
    {
        if (_pttOverlay is not null) return;

        _pttOverlay = new PttOverlayWindow();
        _pttOverlay.SetPosition(_config?.PttOverlayX ?? 20, _config?.PttOverlayY ?? 20);
        _pttOverlay.PositionChanged += (x, y) =>
        {
            if (_config is null) return;
            _config.PttOverlayX = x;
            _config.PttOverlayY = y;
            SaveConfigAsync();
        };
        _pttOverlay.UpdateState(_pttPipeline.CurrentState);
        _pttOverlay.Show();
    }

    private void HidePttOverlay()
    {
        _pttOverlay?.Close();
        _pttOverlay = null;
    }

    /// <summary>
    /// Called on the UI thread when the configured HOTAS PTT button is pressed.
    /// </summary>
    private void OnPttButtonPressed()
    {
        if (_config is null || !_config.PttEnabled) return;

        if (_config.PttMode == "toggle")
        {
            // Toggle mode: press toggles recording on/off
            if (_pttToggleActive)
            {
                _pttToggleActive = false;
                _ = _pttPipeline.StopListeningAndProcessAsync();
            }
            else
            {
                _pttToggleActive = true;
                ResponseText.Text = string.Empty;
                _ = _pttPipeline.StartListeningAsync();
            }
        }
        else
        {
            // Push-to-talk mode: press starts recording
            ResponseText.Text = string.Empty;
            _ = _pttPipeline.StartListeningAsync();
        }
    }

    /// <summary>
    /// Called on the UI thread when the configured HOTAS PTT button is released.
    /// </summary>
    private void OnPttButtonReleased()
    {
        if (_config is null || !_config.PttEnabled) return;

        // Only relevant in push-to-talk mode (toggle is handled entirely in OnPttButtonPressed)
        if (_config.PttMode != "toggle")
        {
            _ = _pttPipeline.StopListeningAndProcessAsync();
        }
    }

    // ── PTT Config UI Event Handlers ────────────────────────────────────────

    private void PttEnabled_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null) return;
        _config.PttEnabled = PttEnabledCheck.IsChecked == true;

        if (_config.PttEnabled && _config.PttDeviceName is not null)
        {
            StartPtt();
            PttStatusText.Text = "Ready";
        }
        else
        {
            StopPtt();
            PttStatusText.Text = _config.PttEnabled ? "Configure a button to start" : "Disabled";
        }

        SaveConfigAsync();
    }

    private void PttSound_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null) return;
        _config.PttActivationSoundEnabled = PttSoundCheck.IsChecked == true;
        SaveConfigAsync();
    }

    private void PttOverlay_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null) return;
        _config.PttOverlayEnabled = PttOverlayCheck.IsChecked == true;

        if (_config.PttOverlayEnabled && _config.PttEnabled)
            ShowPttOverlay();
        else
            HidePttOverlay();

        SaveConfigAsync();
    }

    private void PttDeviceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Selection is applied when the user clicks "Detect button"
    }

    private void PttRefreshDevices_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        RefreshPttDeviceList();
    }

    private void RefreshPttDeviceList()
    {
        var devices = _hotasService.GetConnectedDevices();
        PttDeviceCombo.ItemsSource = devices.Select(d => $"{d.DeviceName} ({d.ButtonCount} buttons)").ToList();

        // Select the configured device if present
        if (_config?.PttDeviceName is not null)
        {
            for (int i = 0; i < devices.Count; i++)
            {
                if (string.Equals(devices[i].DeviceName, _config.PttDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    PttDeviceCombo.SelectedIndex = i;
                    break;
                }
            }
        }
    }

    private void PttDetectButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_pttDetecting)
        {
            // Cancel detection
            _hotasService.EndButtonDetection();
            _pttDetecting = false;
            PttDetectButton.Content = "Detect button";
            PttButtonLabel.Text = _config?.PttDeviceName is not null
                ? $"Button {_config.PttButtonIndex} on {_config.PttDeviceName}"
                : "(none)";
            return;
        }

        _pttDetecting = true;
        PttDetectButton.Content = "Cancel";
        PttButtonLabel.Text = "Press any HOTAS button...";

        _hotasService.BeginButtonDetection((deviceName, buttonIndex) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (!_pttDetecting) return;

                _hotasService.EndButtonDetection();
                _pttDetecting = false;
                PttDetectButton.Content = "Detect button";
                PttButtonLabel.Text = $"Button {buttonIndex} on {deviceName}";

                if (_config is not null)
                {
                    _config.PttDeviceName = deviceName;
                    _config.PttButtonIndex = buttonIndex;
                    SaveConfigAsync();

                    // Restart HOTAS input with the new button if PTT is enabled
                    if (_config.PttEnabled)
                    {
                        StartPtt();
                        PttStatusText.Text = "Ready";
                    }
                }

                AppendLog($"PTT button assigned: Button {buttonIndex} on {deviceName}");
            });
        });
    }

    private void PttTestBeep_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        PttSounds.PlayAsync(PttSounds.GetActivationBeep(), _config?.TtsOutputDevice);
    }

    private void PttMode_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null) return;
        _config.PttMode = PttModeToggle.IsChecked == true ? "toggle" : "push_to_talk";
        _pttToggleActive = false;
        SaveConfigAsync();
    }

    /// <summary>
    /// Fire-and-forget config save. Used by PTT config changes to persist immediately.
    /// Surfaces the Network-Mode + encryption fail-closed guard to the user via a
    /// MessageBox instead of silently swallowing the InvalidOperationException.
    /// </summary>
    private void SaveConfigAsync()
    {
        if (_config is null) return;
        var configPath = Path.Combine(_ssdRoot, "config", "portable-config.json");
        _ = _config.SaveAsync(configPath).ContinueWith(t =>
        {
            if (t.Exception is null) return;
            var ex = t.Exception.GetBaseException();
            Dispatcher.Invoke(() =>
            {
                if (ex is InvalidOperationException &&
                    ex.Message == PortableConfig.NetworkModeEncryptionRequiredMessage)
                {
                    System.Windows.MessageBox.Show(
                        ex.Message,
                        "Config save blocked",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }
                else
                {
                    AppendLog($"Failed to save config: {ex.Message}");
                }
            });
        }, TaskScheduler.Default);
    }

    // TODO (phase-a-default-hardening, Task 1 UI confirmation):
    // There is currently no Runner UI control that toggles NetworkModeEnabled or edits
    // NetworkBindAddress — users edit portable-config.json directly. When such a UI is
    // added, call IDialogService.Confirm with the following text before persisting a
    // non-loopback bind address, and revert to "127.0.0.1" if the user cancels:
    //
    //   "Network Mode will expose the Runner API on <addr>:<port>. There is no TLS."
    //   " Anyone on this network who has your API key can use this machine's AI and"
    //   " audio. Only enable on a trusted LAN. Continue?"
    //
    // The runtime-side warning (logged + AppendLog) in RunnerLocalApiService.StartAsync
    // fires whenever the effective bind address is not loopback.
}
