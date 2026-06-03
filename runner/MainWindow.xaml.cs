using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Services;
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
    private readonly IConfigStore _configStore;
    private ITextToSpeechService? _ttsService;

    private PortableConfig? _config;
    private readonly string _ssdRoot;
    private readonly SsdLogger? _logger;
    private DependencyCheckResult _lastDependencyCheck = new(true, Array.Empty<MissingDependency>());
    private bool _isEncryptedDrive;
    private bool _isUnlocked;
    private DocumentLibraryManifest? _activeLibrary;
    // Suppresses the LibraryCombo SelectionChanged handler while RefreshLibraryUi
    // programmatically sets SelectedIndex. Without this, the create-library path
    // fired a reentrant SetActiveLibraryAsync/config-save mid-refresh — an
    // unguarded async-void re-entry that was the crash window for #3.
    private bool _suppressLibrarySelectionChanged;
    // #4 indexing transparency: wall-clock for the time-remaining estimate.
    private readonly System.Diagnostics.Stopwatch _indexingStopwatch = new();
    // #5 CPU/GPU transparency: the Ollama acceleration backend embeddings run
    // on (NVIDIA CUDA auto, Intel Vulkan, CPU, …). Computed once — GetGpuVendor
    // can hit WMI — and shown as the progress-panel tooltip.
    private string? _embeddingBackendLabel;
    // Cancels the in-flight ingest/sweep/rebuild. Created in RunIndexingAsync, tripped by
    // the Cancel button, disposed when the run ends. Null when no indexing is running.
    private CancellationTokenSource? _indexingCts;
    private CancellationTokenSource? _streamingCts;
    private StreamingTtsSpeaker? _ttsSpeaker;
    private bool _isVoiceRecording;

    // On-screen log throttling. Service log events (notably Ollama's per-request stdout) can
    // arrive thousands of lines a second during a large ingest. Appending each one to the
    // non-virtualized log TextBox with a per-line ScrollToEnd saturated the dispatcher and froze
    // the runner mid-ingest (#68). Lines are now queued and drained by a single coalesced flush,
    // and the buffer is capped so the TextBox layout cost stays bounded.
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new();
    private int _logFlushScheduled;
    private const int MaxLogChars = 256 * 1024;

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

    // Profile pill toggle state
    private bool _suppressPillEvents;

    // Top-level tab selection — Mac-parity initiative #44 stage 1.
    // See RunnerTab.cs for why this is a Grid-based "tab" rather than
    // a WPF TabControl. Default landing tab is Chat.
    private RunnerTab _currentTab = RunnerTab.Chat;

    // FTUE state
    private int _ftueStepIndex;
    private System.Windows.FrameworkElement[] _ftueTargets = Array.Empty<System.Windows.FrameworkElement>();
    private (string label, string title, string body)[] _ftueSteps = Array.Empty<(string, string, string)>();
    private bool _ftueCompletedCached;

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
        ITtsProvider ttsProvider,
        IConfigStore configStore)
    {
        InitializeComponent();

        _ssdRoot = ssdRoot;
        _logger = logger;
        _ollamaService = ollamaService;
        _modelService = modelService;
        _docService = docService;
        _chatService = chatService;
        _dcsImportService = dcsImportService;
        _configStore = configStore;
        _sttService = sttService;
        _audioCaptureService = audioCaptureService;
        _hotasService = hotasService;
        _pttPipeline = pttPipeline;
        _localApiService = localApiService;
        _ttsProvider = ttsProvider;

        // Wire service events to UI
        _ollamaService.LogMessage += msg => AppendLog(msg);
        _ollamaService.ProcessExited += () => Dispatcher.InvokeAsync(async () =>
        {
            await _localApiService.StopAsync();
            StatusText.Text = "Stopped";
            OllamaStatusLed.State = LedState.Idle;
            UpdateOllamaOfflineEmptyState();
            UpdateOllamaRunStopButtonStyles();
        });
        _modelService.LogMessage += msg => AppendLog(msg);
        _docService.LogMessage += msg => AppendLog(msg);
        _chatService.LogMessage += msg => AppendLog(msg);
        // C1: paint cold-load progress in the streaming indicator while
        // ChatService waits for Ollama's first token. Heartbeats fire every
        // ChatService.HeartbeatIntervalSeconds and stop once a token arrives.
        _chatService.FirstTokenPending += seconds => Dispatcher.Invoke(() =>
        {
            // Only paint while the Generating indicator is visible — outside
            // a streaming send the event is irrelevant.
            if (StreamingIndicator.Visibility == System.Windows.Visibility.Visible)
            {
                StreamingIndicator.Text = $"● Loading model… {seconds}s";
            }
        });
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

        // Shutdown is fully synchronous in OnClosing — see comments there.

        LoadConfig();
        _ = ShowModelSizingWarningsOnStartupAsync();
        _ = InitializeCompatibilityAsync();

        Loaded += OnWindowLoaded;
        SizeChanged += OnWindowSizeChanged;
        UpdateOllamaOfflineEmptyState();
        UpdateOllamaRunStopButtonStyles();
    }

    private async void OnWindowLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Read the tiny first-run JSON off the UI thread so a slow / contended
        // SSD can't stall the window's first paint.
        var statePath = Path.Combine(_ssdRoot, SsdLayout.Config, "runner-first-run.json");
        RunnerFirstRunState state;
        try
        {
            state = await Task.Run(() => RunnerFirstRunState.Load(statePath));
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Failed to load runner-first-run state: {ex.Message}");
            state = new RunnerFirstRunState();
        }
        _ftueCompletedCached = state.FtueCompleted;
        if (!_ftueCompletedCached)
        {
            StartFtue();
        }
    }

    private void OnWindowSizeChanged(object sender, System.Windows.SizeChangedEventArgs e)
    {
        if (FtueOverlay.Visibility == System.Windows.Visibility.Visible)
        {
            PositionSpotlight();
        }
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
        InitializeVoiceUi();
        InitializeModelParametersUi();
        RefreshProfileVisibility();
        StatusText.Text = "Ready (not running)";
        AppendLog($"Loaded config from {configPath}");
    }

    private void RefreshProfileVisibility()
    {
        var isFlightSim = _config?.ActiveProfile == UserProfile.FlightSim;
        var vis = isFlightSim ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        BindingsImportCard.Visibility = vis;
        PttCard.Visibility = vis;

        // Voice tab carries universal Speech-output (TTS) and Speech-input
        // (STT) cards (Mac-parity #44 stage 4) so the button stays visible
        // in both profiles; only the PTT card inside it is Sim-only. The
        // Sim tab is still HOTAS-only, so its button still gates on profile.
        TabSimButton.Visibility = vis;
        if (!isFlightSim && _currentTab == RunnerTab.Sim)
        {
            GoToTab(RunnerTab.Chat);
        }

        _suppressPillEvents = true;
        ProfileFlightSimPill.IsChecked = isFlightSim;
        ProfileGeneralPill.IsChecked = !isFlightSim;
        _suppressPillEvents = false;
    }

    private async Task ShowProfileSelectionAsync(bool isRequired)
    {
        if (_config is null) return;

        var dialog = new ProfileSelectionDialog(isRequired) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedProfile is null) return;

        var profile = dialog.SelectedProfile.Value;
        _config.ActiveProfile = profile;
        ProfileDefaults.Apply(_config, profile);

        await _configStore.SaveAsync(_ssdRoot, _config, CancellationToken.None);

        RefreshProfileVisibility();
        AppendLog($"Profile set to {profile}.");

        if (!isRequired)
            NotifyRestartRequired();
    }

    private void NotifyRestartRequired()
    {
        System.Windows.MessageBox.Show(
            "Profile saved. Voice and PTT settings will take effect after restarting the app.",
            "Restart required",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private async void ProfilePill_Checked(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressPillEvents || _config is null) return;

        var profile = sender == ProfileFlightSimPill ? UserProfile.FlightSim : UserProfile.GeneralAssistant;
        if (_config.ActiveProfile == profile) return;

        _config.ActiveProfile = profile;
        ProfileDefaults.Apply(_config, profile);

        await _configStore.SaveAsync(_ssdRoot, _config, CancellationToken.None);

        RefreshProfileVisibility();
        AppendLog($"Profile set to {profile}.");
        NotifyRestartRequired();
    }

    private void PopulateModelCombo()
    {
        var installedModels = _config is not null
            ? _modelService.GetInstalledModelNames(_config)
            : new List<string>();
        ModelCombo.ItemsSource = installedModels;
        ModelCombo.SelectedIndex = installedModels.Count > 0 ? 0 : -1;
        UpdateNoModelsEmptyState();
    }

    private void UpdateEncryptionUiState()
    {
        UnlockDriveButton.Visibility = _isEncryptedDrive ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        UnlockDriveButton.IsEnabled = _isEncryptedDrive && !_isUnlocked;
    }

    private async Task<bool> TryUnlockEncryptedDriveAsync()
    {
        if (!_isEncryptedDrive) return true;
        if (_isUnlocked && _config is not null) return true;

        UnlockDriveButton.IsEnabled = false;
        try
        {
            var dialog = new UnlockDriveDialog { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                StatusText.Text = "Encrypted drive locked";
                AppendLog("Unlock cancelled.");
                return false;
            }

            if (!SsdEncryption.TryUnlockPortableConfigWithMaterial(
                    _ssdRoot, dialog.Password, out var unlockedConfig, out var unlockMaterial, out var error)
                || unlockedConfig is null || unlockMaterial is null)
            {
                StatusText.Text = "Unlock failed";
                AppendLog($"Unlock failed: {error}");
                return false;
            }

            _configStore.UnlockSession(unlockMaterial);

            var migration = await SsdEncryption.TryMigratePlaintextAsync(_ssdRoot, unlockMaterial, _logger);
            if (migration.WasPlaintextNewer && migration.MergedConfig is not null)
            {
                _config = migration.MergedConfig;
                AppendLog("[Migration] Newer unencrypted settings found — merged into encrypted configuration.");
                System.Windows.MessageBox.Show(
                    "Newer settings were found in an unencrypted file from your last session. They've been merged into your encrypted configuration and the unencrypted file has been removed.",
                    "Settings Recovery",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            else
            {
                _config = unlockedConfig;
            }

            _isUnlocked = true;
            PopulateModelCombo();
            RefreshLibraryUi();
            InitializeTts();
            InitializePtt();
            InitializeVoiceUi();
            StatusText.Text = "Unlocked and ready";
            AppendLog("SSD unlocked successfully.");
            _ = SaveEncryptionUnlockStateAsync();
            return true;
        }
        finally
        {
            // Re-enables button on cancel/failure; keeps it disabled on success
            // because _isUnlocked is true and UpdateEncryptionUiState sets IsEnabled = false.
            UpdateEncryptionUiState();
        }
    }

    private async void Start_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isEncryptedDrive && !await TryUnlockEncryptedDriveAsync()) return;
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
        UpdateOllamaOfflineEmptyState();
        UpdateOllamaRunStopButtonStyles();
        await StartLocalApiIfEnabledAsync();
        await Task.Delay(1000);
    }

    private async void Stop_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_ollamaService.IsRunning) return;

        // Run the blocking Kill+Dispose off the UI thread. Process.Dispose
        // waits for the stdout/stderr reader pumps to drain, and those pumps
        // can emit a final LogMessage that needs to marshal back to this
        // dispatcher. Holding the UI thread here would deadlock.
        try
        {
            await _localApiService.StopAsync();
            await Task.Run(() => _ollamaService.Stop());
        }
        catch (Exception ex)
        {
            AppendLog($"Stop failed: {ex.Message}");
        }

        StatusText.Text = "Stopped";
        OllamaStatusLed.State = LedState.Idle;
        UpdateOllamaOfflineEmptyState();
        UpdateOllamaRunStopButtonStyles();
    }

    // Swap Start/Stop style emphasis so only the applicable action wears the
    // loud magenta CTA treatment. Stopped: Start=Magenta, Stop=Ghost.
    // Running: Start=Ghost, Stop=Magenta.
    private void UpdateOllamaRunStopButtonStyles()
    {
        if (StartOllamaButton is null || StopOllamaButton is null) return;
        var running = _ollamaService.IsRunning;
        var magenta = (System.Windows.Style)FindResource("TactileMagentaButton");
        var ghost = (System.Windows.Style)FindResource("GhostSecondaryButton");
        StartOllamaButton.Style = running ? ghost : magenta;
        StopOllamaButton.Style = running ? magenta : ghost;
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

    /// <summary>
    /// Mutually-exclusive outcome states for the Chat-tab banner pair.
    /// Stage-2 Mac-parity §B1 — one signal per Send.
    /// </summary>
    private enum ChatOutcome { None, Error, RagWarning }

    /// <summary>
    /// Single chokepoint that updates the Chat-tab Sources group.
    /// Reveals the headlined block only when the most recent answer
    /// cited at least one chunk; otherwise collapses it. Stage-2
    /// Mac-parity §B1: sources are an as-needed signal, not a
    /// permanent panel.
    /// </summary>
    private void ShowSources(System.Collections.Generic.List<string>? sources)
    {
        if (sources is { Count: > 0 })
        {
            SourcesList.ItemsSource = sources;
            SourcesGroup.Visibility = System.Windows.Visibility.Visible;
        }
        else
        {
            SourcesList.ItemsSource = null;
            SourcesGroup.Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Single chokepoint that updates the Chat-tab outcome banners.
    /// Guarantees structural mutual exclusion — every Send path resets to
    /// <see cref="ChatOutcome.None"/> before re-sending, then promotes to
    /// Error or RagWarning if that's the result. Log entries still fire
    /// from the original call sites so the scrollback in LogText is
    /// unchanged.
    /// </summary>
    private void SetChatOutcome(ChatOutcome outcome, string? message = null)
    {
        switch (outcome)
        {
            case ChatOutcome.Error:
                ChatErrorBannerText.Text = message ?? string.Empty;
                ChatErrorBanner.Visibility = System.Windows.Visibility.Visible;
                RagWarningBanner.Visibility = System.Windows.Visibility.Collapsed;
                break;
            case ChatOutcome.RagWarning:
                RagWarningBannerText.Text = message ?? string.Empty;
                RagWarningBanner.Visibility = System.Windows.Visibility.Visible;
                ChatErrorBanner.Visibility = System.Windows.Visibility.Collapsed;
                break;
            case ChatOutcome.None:
            default:
                ChatErrorBanner.Visibility = System.Windows.Visibility.Collapsed;
                RagWarningBanner.Visibility = System.Windows.Visibility.Collapsed;
                break;
        }
    }

    private async void Send_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null || ModelCombo.SelectedItem is not string model) return;
        if (!TryGetCurrentHost(out var host)) return;

        // Interrupt any ongoing TTS from the previous response
        StopTts();

        ShowSources(null);
        SetChatOutcome(ChatOutcome.None);

        if (_config.UseStreamingChat)
        {
            await SendStreamingAsync(model, host);
        }
        else
        {
            SendButton.IsEnabled = false;
            try
            {
                var result = await _chatService.SendPromptAsync(model, PromptText.Text, host, _config);
                switch (result)
                {
                    case ChatResult.Success s:
                        ResponseText.Text = s.Response.ResponseText;
                        ShowSources(s.Response.Sources);
                        SpeakResponseAsync(s.Response.ResponseText);
                        break;
                    case ChatResult.RagRetrievalFailed r:
                        ResponseText.Text = r.Response.ResponseText;
                        var ragMsg = $"Answered without document context — {r.RagError}";
                        AppendLog($"Warning: {ragMsg}");
                        SetChatOutcome(ChatOutcome.RagWarning, ragMsg);
                        ShowSources(r.Response.Sources);
                        SpeakResponseAsync(r.Response.ResponseText);
                        break;
                    case ChatResult.Failure f:
                        AppendLog($"Error: {f.ErrorMessage}");
                        SetChatOutcome(ChatOutcome.Error, f.ErrorMessage);
                        break;
                }
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
        // C1: reset to the default "Generating" label; FirstTokenPending may
        // overwrite it with "Loading model… NNs" until tokens flow.
        StreamingIndicator.Text = "● Generating...";
        StreamingIndicator.Visibility = System.Windows.Visibility.Visible;
        ResponseText.Text = string.Empty;

        // Start a sentence-buffered TTS speaker for this streaming response
        var ttsSpeaker = BeginStreamingTts();

        try
        {
            var firstTokenSeen = false;
            var streamResult = await _chatService.SendPromptStreamingAsync(
                model, PromptText.Text, host, _config!,
                async token =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!firstTokenSeen)
                        {
                            // C1: tokens flowing — clear "Loading model…" label.
                            firstTokenSeen = true;
                            StreamingIndicator.Text = "● Generating...";
                        }
                        ResponseText.AppendText(token);
                    });
                    ttsSpeaker?.FeedToken(token);
                },
                ct);

            ttsSpeaker?.Finish();

            switch (streamResult)
            {
                case ChatResult.Success s:
                    ResponseText.Text = s.Response.ResponseText;
                    ShowSources(s.Response.Sources);
                    break;
                case ChatResult.RagRetrievalFailed r:
                    ResponseText.Text = r.Response.ResponseText;
                    var ragMsg = $"Answered without document context — {r.RagError}";
                    AppendLog($"Warning: {ragMsg}");
                    SetChatOutcome(ChatOutcome.RagWarning, ragMsg);
                    ShowSources(r.Response.Sources);
                    break;
                case ChatResult.Failure f:
                    ttsSpeaker?.Cancel();
                    AppendLog($"Error: {f.ErrorMessage}");
                    if (string.IsNullOrEmpty(ResponseText.Text))
                    {
                        AppendLog("Falling back to non-streaming mode.");
                        try
                        {
                            var fallback = await _chatService.SendPromptAsync(model, PromptText.Text, host, _config!);
                            switch (fallback)
                            {
                                case ChatResult.Success fs:
                                    ResponseText.Text = fs.Response.ResponseText;
                                    ShowSources(fs.Response.Sources);
                                    SpeakResponseAsync(fs.Response.ResponseText);
                                    break;
                                case ChatResult.RagRetrievalFailed fr:
                                    ResponseText.Text = fr.Response.ResponseText;
                                    var fragMsg = $"Answered without document context — {fr.RagError}";
                                    AppendLog($"Warning: {fragMsg}");
                                    SetChatOutcome(ChatOutcome.RagWarning, fragMsg);
                                    ShowSources(fr.Response.Sources);
                                    SpeakResponseAsync(fr.Response.ResponseText);
                                    break;
                                case ChatResult.Failure ff:
                                    AppendLog($"Fallback also failed: {ff.ErrorMessage}");
                                    SetChatOutcome(ChatOutcome.Error, ff.ErrorMessage);
                                    break;
                            }
                        }
                        catch (Exception fallbackEx)
                        {
                            AppendLog($"Fallback also failed: {fallbackEx.Message}");
                            SetChatOutcome(ChatOutcome.Error, fallbackEx.Message);
                        }
                    }
                    else
                    {
                        // Streaming gave a partial response then failed — surface the
                        // failure on the banner so the user knows the answer is truncated.
                        SetChatOutcome(ChatOutcome.Error, f.ErrorMessage);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            ttsSpeaker?.Cancel();
            AppendLog($"Streaming error: {ex.Message}");
            SetChatOutcome(ChatOutcome.Error, ex.Message);
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

    private async void InstallMissing_Click(object sender, System.Windows.RoutedEventArgs e)
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

        SetDependenciesOutcome(_lastDependencyCheck);
    }

    private enum DependenciesOutcome { Ok, Missing }

    /// <summary>
    /// Single chokepoint that updates the System-tab Dependencies card.
    /// Guarantees structural mutual exclusion between the Success and Danger
    /// banners and keeps the missing-items list + Install button visibility
    /// in lockstep with the check result. Mirrors the SetChatOutcome pattern
    /// from the Chat-tab stage-2 work so a future agent can't silently let
    /// the two banners co-exist or surface the Install button when nothing
    /// is missing.
    /// </summary>
    private void SetDependenciesOutcome(DependencyCheckResult check)
    {
        var outcome = check.IsSatisfied ? DependenciesOutcome.Ok : DependenciesOutcome.Missing;
        switch (outcome)
        {
            case DependenciesOutcome.Ok:
                DependenciesSuccessBanner.Visibility = System.Windows.Visibility.Visible;
                DependenciesDangerBanner.Visibility = System.Windows.Visibility.Collapsed;
                DependenciesMissingList.ItemsSource = null;
                DependenciesMissingList.Visibility = System.Windows.Visibility.Collapsed;
                InstallMissingButton.Visibility = System.Windows.Visibility.Collapsed;
                break;
            case DependenciesOutcome.Missing:
            default:
                var count = check.MissingItems.Count;
                var noun = count == 1 ? "component" : "components";
                DependenciesDangerBannerText.Text = $"{count} required {noun} missing — install to enable offline use.";
                DependenciesDangerBanner.Visibility = System.Windows.Visibility.Visible;
                DependenciesSuccessBanner.Visibility = System.Windows.Visibility.Collapsed;
                DependenciesMissingList.ItemsSource = check.MissingItems;
                DependenciesMissingList.Visibility = System.Windows.Visibility.Visible;
                InstallMissingButton.Visibility = System.Windows.Visibility.Visible;
                break;
        }
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
        // Suppress the SelectionChanged handler while we set the index
        // programmatically — the active library is already resolved here, so
        // the handler's reentrant SetActiveLibraryAsync is redundant (and was
        // the unguarded async-void crash window for #3).
        _suppressLibrarySelectionChanged = true;
        try
        {
            LibraryCombo.ItemsSource = info.Options;
            LibraryCombo.SelectedIndex = info.SelectedIndex;
        }
        finally
        {
            _suppressLibrarySelectionChanged = false;
        }
        _activeLibrary = info.ActiveLibrary;
        AppendLog($"Library list refreshed ({Math.Max(info.Options.Count - 1, 0)} libraries).");
        if (_activeLibrary is not null)
        {
            AppendLog($"Selected library: {_activeLibrary.Name} ({_activeLibrary.Id})");
        }

        LibraryFilesList.ItemsSource = _activeLibrary?.Files ?? new List<DocumentFileEntry>();
        UpdateWatchedFolders();
        IndexingStatusText.Text = _activeLibrary?.LastIndexedUtc is null
            ? "No indexing run yet."
            : $"Last indexed: {_activeLibrary.LastIndexedUtc:u}";
        UpdateLibraryActionButtons();
        UpdateNoLibraryEmptyState();
        UpdateEmbeddingModelHint();
    }

    // Workstream D1: render the active library's watched folders read-only,
    // hiding the section entirely when there are none.
    private void UpdateWatchedFolders()
    {
        var folders = _activeLibrary?.WatchedFolders ?? new List<string>();
        WatchedFoldersList.ItemsSource = folders;
        WatchedFoldersSection.Visibility = folders.Count > 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private async Task<bool> EnsureActiveLibraryAsync()
    {
        if (_config is null)
        {
            _activeLibrary = null;
            return false;
        }

        // This runs fire-and-forget from LibraryCombo_SelectionChanged, so an
        // uncaught throw here would be an unhandled async-void exception (#3).
        // Degrade to a visible message and "no active library" instead.
        try
        {
            var selectedId = _docService.GetLibraryIdByIndex(LibraryCombo.SelectedIndex);
            _activeLibrary = await _docService.SetActiveLibraryAsync(_config, _ssdRoot, selectedId);
            LibraryFilesList.ItemsSource = _activeLibrary?.Files ?? new List<DocumentFileEntry>();
            UpdateWatchedFolders();
            UpdateLibraryActionButtons();
            UpdateNoLibraryEmptyState();
            return _activeLibrary is not null;
        }
        catch (Exception ex)
        {
            AppendLog($"Selecting library failed: {ex.Message}");
            IndexingStatusText.Text = $"Selecting library failed: {ex.Message}";
            return false;
        }
    }

    private void LibraryCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // Ignore the index changes RefreshLibraryUi makes programmatically; only
        // a real user pick should drive a SetActiveLibraryAsync round-trip.
        if (_suppressLibrarySelectionChanged) return;
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
        catch (Exception ex)
        {
            // Any other failure (IO, SQLite, etc.) must degrade to a visible
            // message, not an unhandled async-void exception that kills the app
            // (#3). The global handler is the backstop; this keeps the failure
            // local and actionable.
            AppendLog($"Create library failed: {ex.Message}");
            IndexingStatusText.Text = $"Create library failed: {ex.Message}";
        }
    }

    private void UpdateLibraryActionButtons()
    {
        var enabled = _activeLibrary is not null;
        AddFilesButton.IsEnabled = enabled;
        AddFolderButton.IsEnabled = enabled;
        RenameLibraryButton.IsEnabled = enabled;
        DeleteLibraryButton.IsEnabled = enabled;
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

        var library = _activeLibrary!;
        var config = _config!;
        var fileNames = dlg.FileNames;
        IndexingStatusText.Text = "Indexing...";
        await RunIndexingAsync("Indexing", (progress, ct) =>
            _docService.IngestFilesAsync(library, fileNames, host, config, progress, ct));
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
            // D1: surface the newly added folder in the read-only watched-folders list.
            UpdateWatchedFolders();
        }
    }

    // D2: each watched-folder row carries its path as DataContext; stop watching it.
    private async void RemoveWatchedFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_activeLibrary is null) return;
        if (sender is not System.Windows.FrameworkElement { DataContext: string folder } ||
            string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var removed = await _docService.RemoveWatchedFolderAsync(_activeLibrary, folder);
        if (removed)
        {
            IndexingStatusText.Text = $"Removed watched folder: {folder}";
            UpdateWatchedFolders();
        }
    }

    // D2: rename the active library via a small modal input dialog.
    private async void RenameLibrary_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null || _activeLibrary is null)
        {
            AppendLog("Select a document library first.");
            return;
        }

        var dialog = new RenameLibraryDialog(_activeLibrary.Name) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var renamed = await _docService.RenameLibraryAsync(_activeLibrary.Id, dialog.NewName);
            RefreshLibraryUi();
            IndexingStatusText.Text = $"Renamed library to: {renamed.Name}";
            AppendLog($"Renamed library to: {renamed.Name}");
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

    // D2: delete the active library (folder + index) after confirmation.
    private async void DeleteLibrary_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null || _activeLibrary is null)
        {
            AppendLog("Select a document library first.");
            return;
        }

        var name = _activeLibrary.Name;
        var id = _activeLibrary.Id;
        var result = System.Windows.MessageBox.Show(
            $"Delete '{name}' and all its indexed files? This cannot be undone.",
            "Delete library",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await _docService.DeleteLibraryAsync(_config, _ssdRoot, id);
            RefreshLibraryUi();
            IndexingStatusText.Text = $"Deleted library: {name}";
            AppendLog($"Deleted library: {name}");
        }
        catch (Exception ex)
        {
            AppendLog($"Delete failed: {ex.Message}");
            IndexingStatusText.Text = $"Delete failed: {ex.Message}";
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

        var library = _activeLibrary!;
        var config = _config!;
        await RunIndexingAsync("Sweep", (progress, ct) =>
            _docService.SweepFoldersAsync(library, host, config, progress, ct));
    }

    private async void RebuildIndex_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!await EnsureActiveLibraryAsync() || _activeLibrary is null || _config is null)
        {
            AppendLog("Select a document library first.");
            return;
        }

        if (!TryGetCurrentHost(out var host)) return;

        var library = _activeLibrary!;
        var config = _config!;
        await RunIndexingAsync("Rebuild", (progress, ct) =>
            _docService.RebuildIndexAsync(library, host, config, progress, ct));
    }

    /// <summary>
    /// Live one-line ingest status: "Indexing 2/5: foo.pdf (chunk 40/120)", with a trailing
    /// "— N failed" when chunk embeds fail. Returns "Finishing…" for the terminal frame
    /// (empty CurrentFile); callers replace it with <see cref="IndexingSummary.Format"/> on success.
    /// </summary>
    private static string FormatProgressLine(string verb, IndexingProgress p)
    {
        if (string.IsNullOrEmpty(p.CurrentFile))
        {
            return "Finishing…";
        }

        var line = $"{verb} {p.CompletedFiles + 1}/{p.TotalFiles}: {p.CurrentFile}";
        if (p.TotalChunks > 0)
        {
            line += $" (chunk {p.EmbeddedChunks}/{p.TotalChunks})";
        }
        if (p.FailedChunks > 0)
        {
            line += $" — {p.FailedChunks} failed";
        }

        return line;
    }

    // ── #4 indexing transparency: progress bar + time-remaining estimate ────

    /// <summary>Reset the stopwatch and reveal the determinate progress row at
    /// the start of an ingest / sweep / rebuild.</summary>
    private void BeginIndexingProgress()
    {
        _indexingStopwatch.Restart();
        IndexingProgressBar.Value = 0;
        IndexingEtaText.Text = "estimating…";
        _embeddingBackendLabel ??= FreeAiSsd.Shared.GpuAccelerationPolicy
            .ResolveFor(FreeAiSsd.Shared.SystemResources.GetGpuVendor(), _config?.PreferredCompute)
            .BackendDescription;
        IndexingProgressPanel.ToolTip =
            $"Embedding acceleration: {_embeddingBackendLabel}. " +
            "If this is CPU on a GPU machine, the GPU runtime (CUDA/ROCm) isn't loaded — " +
            "see the log for Ollama's actual backend.";
        IndexingProgressPanel.Visibility = System.Windows.Visibility.Visible;
    }

    /// <summary>Stop the stopwatch and hide the progress row when the run ends
    /// (success or failure). Called from each handler's finally.</summary>
    private void EndIndexingProgress()
    {
        _indexingStopwatch.Stop();
        IndexingProgressPanel.Visibility = System.Windows.Visibility.Collapsed;
    }

    /// <summary>
    /// Runs an ingest/sweep/rebuild off the UI thread with a Cancel-able token and
    /// coalesced progress, then renders the summary. The actual work runs on a thread-pool
    /// thread (<see cref="Task.Run(System.Func{Task})"/>) so the heavy embed-result parsing
    /// and SQLite writes never land on the dispatcher; progress frames are marshaled back
    /// non-blocking via <see cref="System.Windows.Threading.Dispatcher.BeginInvoke(System.Delegate, object[])"/>
    /// and throttled to ~10/sec (the terminal frame always flushes). This replaces the old
    /// per-chunk blocking <c>Dispatcher.Invoke</c> that starved the message pump and showed
    /// the window as "not responding" during large ingests.
    /// </summary>
    private async Task RunIndexingAsync(string verb, Func<Action<IndexingProgress>, CancellationToken, Task> operation)
    {
        BeginIndexingProgress();
        var cts = new CancellationTokenSource();
        _indexingCts = cts;
        IndexingCancelButton.IsEnabled = true;

        IndexingProgress? last = null;
        long lastMarshalMs = 0;

        void OnProgress(IndexingProgress p)
        {
            last = p;
            var isTerminal = string.IsNullOrEmpty(p.CurrentFile);
            var nowMs = _indexingStopwatch.ElapsedMilliseconds;
            // Throttle non-terminal frames to ~10/sec; always flush the terminal frame.
            if (!isTerminal && nowMs - System.Threading.Interlocked.Read(ref lastMarshalMs) < 100)
            {
                return;
            }
            System.Threading.Interlocked.Exchange(ref lastMarshalMs, nowMs);
            Dispatcher.BeginInvoke(new Action(() => UpdateIndexingProgress(verb, p)));
        }

        try
        {
            await Task.Run(() => operation(OnProgress, cts.Token), cts.Token);
            RefreshLibraryUi();
            if (last is not null)
            {
                IndexingStatusText.Text = IndexingSummary.Format(last);
            }
        }
        catch (OperationCanceledException)
        {
            IndexingStatusText.Text = $"{verb} cancelled.";
            AppendLog($"{verb} cancelled by user.");
        }
        catch (Exception ex)
        {
            var cause = IndexingSummary.DescribeFailure(ex);
            AppendLog($"{verb} failed: {cause}");
            IndexingStatusText.Text = $"{verb} failed: {cause}";
        }
        finally
        {
            _indexingCts = null;
            cts.Dispose();
            EndIndexingProgress();
        }
    }

    /// <summary>Trip the active indexing cancellation token (Cancel button).</summary>
    private void CancelIndexing_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_indexingCts is null) return;
        IndexingCancelButton.IsEnabled = false;
        IndexingStatusText.Text = "Cancelling…";
        _indexingCts.Cancel();
    }

    /// <summary>
    /// Update the status line, progress bar, and ETA from a progress frame.
    /// Runs on the UI thread (callers wrap it in Dispatcher.Invoke). The
    /// terminal frame (empty CurrentFile) only refreshes the status line — the
    /// caller replaces it with the summary and the finally hides the bar.
    /// </summary>
    private void UpdateIndexingProgress(string verb, IndexingProgress p)
    {
        IndexingStatusText.Text = FormatProgressLine(verb, p);
        if (string.IsNullOrEmpty(p.CurrentFile)) return;

        var percent = ComputeIndexingPercent(p);
        IndexingProgressBar.Value = percent;

        var elapsed = _indexingStopwatch.Elapsed;
        if (percent >= 2 && percent < 100 && elapsed.TotalSeconds >= 1)
        {
            var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (100 - percent) / percent);
            IndexingEtaText.Text = $"~{FormatEta(remaining)} left";
        }
        else
        {
            IndexingEtaText.Text = "estimating…";
        }
    }

    /// <summary>
    /// Overall percent across the batch: completed whole files plus the current
    /// file's chunk fraction, over total files. For the single-large-file case
    /// (e.g. a Chuck's-Guide PDF) this is exactly EmbeddedChunks/TotalChunks.
    /// </summary>
    private static double ComputeIndexingPercent(IndexingProgress p)
    {
        if (p.TotalFiles <= 0) return 0;
        var fileFraction = p.TotalChunks > 0 ? (double)p.EmbeddedChunks / p.TotalChunks : 0;
        var overall = (p.CompletedFiles + fileFraction) / p.TotalFiles;
        return Math.Clamp(overall * 100.0, 0, 100);
    }

    private static string FormatEta(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
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
        UpdateEmbeddingModelHint();
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
            var sttResult = await _sttService.TranscribeAudioAsync(audioData);

            if (sttResult is TranscriptionResult.Failure sttFailure)
            {
                AppendLog($"Transcription failed: {sttFailure.ErrorMessage}");
                SetVoiceStatus(null);
                return;
            }

            var text = ((TranscriptionResult.Success)sttResult).Text;

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
        _logger?.Info(line);

        // Queue the line and schedule at most one pending flush. Bursts (thousands of Ollama
        // lines/sec during ingest) collapse into a single dispatcher op that drains the whole
        // backlog, instead of one InvokeAsync + AppendText + ScrollToEnd per line, which froze
        // the UI (#68). The flush also fires from the ThreadPool-safe BeginInvoke, preserving the
        // old reason for not using a synchronous Invoke here (Process.Dispose reader-pump deadlock).
        _logQueue.Enqueue(line);
        if (System.Threading.Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
        {
            Dispatcher.BeginInvoke(new Action(FlushLogQueue));
        }
    }

    /// <summary>
    /// Drains the queued log lines into the TextBox in one batched append, then caps the buffer.
    /// Runs on the UI thread. The scheduled flag is cleared first so lines enqueued during the
    /// drain reschedule exactly one follow-up flush rather than being lost.
    /// </summary>
    private void FlushLogQueue()
    {
        System.Threading.Interlocked.Exchange(ref _logFlushScheduled, 0);
        if (_logQueue.IsEmpty) return;

        var batch = new System.Text.StringBuilder();
        while (_logQueue.TryDequeue(out var queued))
        {
            batch.Append(queued).Append('\n');
        }
        LogText.AppendText(batch.ToString());

        // Bound the on-screen buffer so the TextBox layout cost stays O(cap), not O(session).
        if (LogText.Text.Length > MaxLogChars)
        {
            var overflow = LogText.Text.Length - MaxLogChars;
            var cut = LogText.Text.IndexOf('\n', overflow);
            LogText.Text = cut >= 0 ? LogText.Text[(cut + 1)..] : LogText.Text[overflow..];
        }

        LogText.ScrollToEnd();
    }

    private void UnlockDrive_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _ = TryUnlockEncryptedDriveAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Empty states
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Toggles the "Ollama is offline" CTA over the log area based on whether
    /// the local server is currently running.
    /// </summary>
    private void UpdateOllamaOfflineEmptyState()
    {
        var offline = !_ollamaService.IsRunning;
        OllamaOfflineEmptyState.Visibility = offline
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    /// <summary>
    /// Toggles the Reference Documents card between its management surface
    /// (buttons + index tools + files list) and a single hint line, as a
    /// mutually-exclusive pair driven by whether a library is selected.
    /// Mac-parity §B2: one clear hint, not per-button failures.
    /// </summary>
    private void UpdateNoLibraryEmptyState()
    {
        var hasLibrary = _activeLibrary is not null;
        LibraryManagementSection.Visibility = hasLibrary
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
        NoLibraryEmptyState.Visibility = hasLibrary
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
    }

    /// <summary>
    /// Workstream C: proactive embedding-model readiness. Shows an inline hint with a
    /// Pull action inside the active-library surface when the configured embedder isn't
    /// installed, so the user fixes it before an ingest throws "model not found". The
    /// hint lives inside LibraryManagementSection, so it only renders when a library is
    /// active. Readiness is disk-truth, so this is meaningful even before Ollama starts.
    /// </summary>
    private void UpdateEmbeddingModelHint()
    {
        var missing = _config is not null && !_modelService.IsEmbeddingModelInstalled(_config);
        if (missing)
        {
            EmbeddingModelHintText.Text =
                $"The embedding model “{_config!.EmbeddingModelName}” isn't installed yet — " +
                "documents can't be indexed until it's pulled.";
        }
        EmbeddingModelHint.Visibility = missing
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    /// <summary>
    /// Toggles the "no models installed" CTA on the model+prompt row. Disables
    /// the row controls when no models are configured so the user can't try to
    /// send into the void.
    /// </summary>
    private void UpdateNoModelsEmptyState()
    {
        var hasModels = ModelCombo.Items.Count > 0;
        NoModelsEmptyState.Visibility = hasModels
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
        ModelPromptRow.IsEnabled = hasModels;
    }

    /// <summary>
    /// Click handler for the "Open Prep app" CTA in the no-models empty state.
    /// Tries to launch the prep-app executable from its expected layout
    /// locations on the SSD; falls back to opening the SSD root folder if
    /// the binary can't be located.
    /// </summary>
    private void OpenPrepApp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Released SSDs ship the PrepApp single-file exe at the SSD root
        // (see .github/workflows/build.yml — payload root). Dev-machine layouts
        // may still have it under the source tree, so we probe a couple of
        // fallback locations using the SsdLayout.Windows constant where
        // applicable rather than hardcoding "windows".
        const string PrepAppExe = "FreeAiSsd.PrepApp.exe";
        var candidates = new[]
        {
            Path.Combine(_ssdRoot, PrepAppExe),
            Path.Combine(_ssdRoot, SsdLayout.Windows, "prep-app", PrepAppExe),
            Path.Combine(_ssdRoot, "prep-app", PrepAppExe),
        };

        var exe = candidates.FirstOrDefault(File.Exists);
        if (exe is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                AppendLog($"Failed to launch Prep app: {ex.Message}");
            }
        }

        AppendLog("Prep app executable not found on the SSD. Opening SSD root folder.");
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _ssdRoot, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to open SSD root: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Top-level tabs — Mac-parity initiative #44 stage 1.
    // Grid-based "tab" shell: every named control stays in the visual tree
    // (only the parent grid's Visibility flips). See RunnerTab.cs.
    // ─────────────────────────────────────────────────────────────────────────

    private void TabChat_Click(object sender, System.Windows.RoutedEventArgs e) => GoToTab(RunnerTab.Chat);
    private void TabVoice_Click(object sender, System.Windows.RoutedEventArgs e) => GoToTab(RunnerTab.Voice);
    private void TabSim_Click(object sender, System.Windows.RoutedEventArgs e) => GoToTab(RunnerTab.Sim);
    private void TabSystem_Click(object sender, System.Windows.RoutedEventArgs e) => GoToTab(RunnerTab.System);

    private void GoToTab(RunnerTab tab)
    {
        _currentTab = tab;

        ChatTab.Visibility = tab == RunnerTab.Chat ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        VoiceTab.Visibility = tab == RunnerTab.Voice ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        SimTab.Visibility = tab == RunnerTab.Sim ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        SystemTab.Visibility = tab == RunnerTab.System ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

        // The active header button paints the sunken surface + cyan
        // underline via the SegmentedGameButton style's Tag=Selected trigger.
        TabChatButton.Tag = tab == RunnerTab.Chat ? "Selected" : null;
        TabVoiceButton.Tag = tab == RunnerTab.Voice ? "Selected" : null;
        TabSimButton.Tag = tab == RunnerTab.Sim ? "Selected" : null;
        TabSystemButton.Tag = tab == RunnerTab.System ? "Selected" : null;

        // Reset the outer scroll so the new tab opens at its top rather than
        // inheriting the previous tab's scroll offset.
        RootScroll?.ScrollToTop();
    }

    /// <summary>
    /// Returns the tab on which the given FTUE target lives so the FTUE
    /// driver can switch tabs before painting the spotlight. Defaults to
    /// Chat for any unmapped target.
    /// </summary>
    private RunnerTab TabForFtueTarget(System.Windows.FrameworkElement target)
    {
        if (target == SystemCompatibilityCard) return RunnerTab.System;
        if (target == BindingsImportCard) return RunnerTab.Sim;
        if (target == PttCard) return RunnerTab.Voice;
        return RunnerTab.Chat;
    }

    /// <summary>
    /// Whether the given tab's header button is currently visible.
    /// Chat and System are always visible; Voice and Sim are gated by
    /// <see cref="RefreshProfileVisibility"/> (General profile hides them).
    /// </summary>
    private bool IsTabHeaderVisible(RunnerTab tab) => tab switch
    {
        RunnerTab.Voice => TabVoiceButton.Visibility == System.Windows.Visibility.Visible,
        RunnerTab.Sim => TabSimButton.Visibility == System.Windows.Visibility.Visible,
        _ => true,
    };

    // ─────────────────────────────────────────────────────────────────────────
    // FTUE (First-Time User Experience): 4-step spotlight tour.
    // ─────────────────────────────────────────────────────────────────────────

    private void ReplayTour_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        StartFtue();
    }

    private void StartFtue()
    {
        _ftueSteps = new (string, string, string)[]
        {
            ("Step 1 of 4", "Check compatibility",
                "Confirm GPU, CPU, and dependencies are good. Expand 'System details' if anything looks red."),
            ("Step 2 of 4", "Start Ollama",
                "Click Start Ollama to launch the local server. The status LED turns green when it's ready."),
            ("Step 3 of 4", "Pick a model and send a prompt",
                "Choose an installed model, type a prompt, and click Send. Use 🎤 Voice for hands-free input."),
            ("Step 4 of 4", "Optional: bindings or PTT",
                "Import flight-sim controller bindings into a library, or enable Push-to-Talk to query the AI from your HOTAS."),
        };
        _ftueTargets = new System.Windows.FrameworkElement[]
        {
            SystemCompatibilityCard,
            StartOllamaButton,
            ModelPromptCard,
            BindingsImportCard,
        };

        _ftueStepIndex = 0;
        FtueOverlay.Visibility = System.Windows.Visibility.Visible;
        ApplyFtueStep();
    }

    private void ApplyFtueStep()
    {
        if (_ftueStepIndex < 0 || _ftueStepIndex >= _ftueSteps.Length)
        {
            FinishFtue();
            return;
        }

        var (label, title, body) = _ftueSteps[_ftueStepIndex];
        FtueStepLabel.Text = label;
        FtueTitleText.Text = title;
        FtueBodyText.Text = body;
        FtueNextButton.Content = _ftueStepIndex == _ftueSteps.Length - 1 ? "Finish" : "Next";

        // Step 2 highlights the Start button: pulse the Ollama LED to draw
        // the eye to it. Restore prior state on later steps.
        if (_ftueStepIndex == 1)
        {
            OllamaStatusLed.State = LedState.Busy;
        }
        else if (OllamaStatusLed.State == LedState.Busy && !_ollamaService.IsRunning)
        {
            OllamaStatusLed.State = LedState.Idle;
        }

        // Mac-parity #44 stage 1: the FTUE targets are spread across
        // multiple tabs (System / Chat / Sim). Switch to the right tab
        // BEFORE we position the spotlight, or the target won't be
        // measured (Visibility=Collapsed parent => zero ActualWidth).
        // Skip the switch if the target tab is hidden under the active
        // profile (e.g. Sim under General) — the instruction card still
        // describes the action; PositionSpotlight's visibility check
        // will hide the ring rather than painting on nothing.
        if (_ftueStepIndex < _ftueTargets.Length)
        {
            var targetTab = TabForFtueTarget(_ftueTargets[_ftueStepIndex]);
            if (targetTab != _currentTab && IsTabHeaderVisible(targetTab))
            {
                GoToTab(targetTab);
            }
        }

        // Defer spotlight positioning until the target has a real layout.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(PositionSpotlight));
    }

    private void PositionSpotlight()
    {
        if (_ftueStepIndex < 0 || _ftueStepIndex >= _ftueTargets.Length)
        {
            FtueSpotlight.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        var target = _ftueTargets[_ftueStepIndex];
        if (target is null || !target.IsVisible || target.ActualWidth <= 0 || target.ActualHeight <= 0)
        {
            FtueSpotlight.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        // Scroll the outer ScrollViewer so the target is on-screen before
        // measuring. BringIntoView queues a scroll; we re-post at Background
        // priority so coordinates are sampled after layout has settled.
        target.BringIntoView();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => PlaceSpotlightOver(target)));
    }

    private void PlaceSpotlightOver(System.Windows.FrameworkElement target)
    {
        try
        {
            var transform = target.TransformToVisual(FtueSpotlightCanvas);
            var topLeft = transform.Transform(new System.Windows.Point(0, 0));
            const double pad = 8;
            System.Windows.Controls.Canvas.SetLeft(FtueSpotlight, topLeft.X - pad);
            System.Windows.Controls.Canvas.SetTop(FtueSpotlight, topLeft.Y - pad);
            FtueSpotlight.Width = target.ActualWidth + pad * 2;
            FtueSpotlight.Height = target.ActualHeight + pad * 2;
            FtueSpotlight.Visibility = System.Windows.Visibility.Visible;
        }
        catch (InvalidOperationException)
        {
            FtueSpotlight.Visibility = System.Windows.Visibility.Collapsed;
        }
    }

    private void OnFtueNextClick(object sender, System.Windows.RoutedEventArgs e)
    {
        _ftueStepIndex++;
        if (_ftueStepIndex >= _ftueSteps.Length)
        {
            FinishFtue();
            return;
        }
        ApplyFtueStep();
    }

    private void OnFtueSkipClick(object sender, System.Windows.RoutedEventArgs e)
    {
        FinishFtue();
    }

    private void FinishFtue()
    {
        FtueOverlay.Visibility = System.Windows.Visibility.Collapsed;
        FtueSpotlight.Visibility = System.Windows.Visibility.Collapsed;
        // Restore the Ollama LED if step 2 forced it into Busy purely for
        // attention. Don't clobber a real Busy/Ok/Error coming from runtime.
        if (OllamaStatusLed.State == LedState.Busy && !_ollamaService.IsRunning)
        {
            OllamaStatusLed.State = LedState.Idle;
        }

        if (_ftueCompletedCached) return;
        _ftueCompletedCached = true;
        _ = SaveFtueCompletedAsync();
    }

    private async Task SaveFtueCompletedAsync()
    {
        try
        {
            var statePath = Path.Combine(_ssdRoot, SsdLayout.Config, "runner-first-run.json");
            // Load is synchronous but cheap (small JSON); push it off the UI
            // thread for consistency since SaveAsync already runs there.
            var state = await Task.Run(() => RunnerFirstRunState.Load(statePath));
            if (state.FtueCompleted) return;
            state.FtueCompleted = true;
            state.LastCheckedUtc = DateTime.UtcNow;
            await state.SaveAsync(statePath);
        }
        catch (Exception ex)
        {
            // Don't let a bad I/O on the SSD crash the app via an unobserved
            // task exception. Worst case the user sees the FTUE again next launch.
            _logger?.Warn($"Failed to persist FTUE completion flag: {ex.Message}");
        }
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

    /// <summary>Step 2: Whole-row click toggles IsSelected (task #56).</summary>
    private void AircraftRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement el &&
            el.DataContext is DcsAircraftImportItem item &&
            item.CanImport)
        {
            item.IsSelected = !item.IsSelected;
            // DcsAircraftImportItem is a POCO with no INPC, so the CheckBox
            // visual won't update on its own — same pattern as Select/Deselect All.
            AircraftList.Items.Refresh();
        }
    }

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
        _ = _configStore.SaveAsync(_ssdRoot, _config, CancellationToken.None).ContinueWith(t =>
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

    // ─────────────────────────────────────────────────────────────────────────
    // Voice tab UI — TTS + STT settings (Mac-parity #44 stage 4)
    // ─────────────────────────────────────────────────────────────────────────

    // Suppresses change handlers while we're populating Voice-tab controls from
    // config so SelectionChanged / ValueChanged / Click don't write the loaded
    // value back to disk. Same pattern as _suppressPillEvents (profile pills).
    private bool _suppressVoiceUiEvents;

    /// <summary>
    /// Populates the Voice-tab TTS and STT controls from the current config.
    /// Called after config load or unlock, alongside <see cref="InitializeTts"/>
    /// and <see cref="InitializePtt"/>. Does not start any background work —
    /// the actual TTS service has already been built by InitializeTts.
    /// </summary>
    private void InitializeVoiceUi()
    {
        if (_config is null) return;

        _suppressVoiceUiEvents = true;
        try
        {
            // ── TTS ────────────────────────────────────────────────────────
            TtsEnabledCheck.IsChecked = _config.TtsEnabled;

            var isPiper = string.Equals(_config.TtsEngine, "piper", StringComparison.OrdinalIgnoreCase);
            TtsEngineSystemPill.IsChecked = !isPiper;
            TtsEnginePiperPill.IsChecked = isPiper;

            // Piper requires piper.exe staged on the SSD; surface a hint and
            // block the radio when it's not available so the user isn't
            // confused by silent fall-through to System.
            var piperAvailable = File.Exists(Path.Combine(_ssdRoot, "windows", "tools", "piper", "piper.exe"));
            TtsEnginePiperPill.IsEnabled = piperAvailable;
            PiperUnavailableHint.Visibility = piperAvailable
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;

            RefreshTtsVoiceList();
            RefreshTtsOutputDeviceList();

            TtsRateSlider.Value = _config.TtsRate;
            TtsRateLabel.Text = _config.TtsRate.ToString();
            TtsVolumeSlider.Value = _config.TtsVolume;
            TtsVolumeLabel.Text = _config.TtsVolume.ToString();

            // ── STT ────────────────────────────────────────────────────────
            RefreshMicDeviceList();

            WhisperTinyPill.IsChecked = _config.WhisperModelSize == WhisperModelSize.Tiny;
            WhisperBasePill.IsChecked = _config.WhisperModelSize == WhisperModelSize.Base;
            WhisperSmallPill.IsChecked = _config.WhisperModelSize == WhisperModelSize.Small;
            WhisperMediumPill.IsChecked = _config.WhisperModelSize == WhisperModelSize.Medium;

            AutoSendVoiceCheck.IsChecked = _config.AutoSendVoiceInput;
        }
        finally
        {
            _suppressVoiceUiEvents = false;
        }
    }

    /// <summary>
    /// Re-populates the TTS voice dropdown from the currently active engine
    /// and selects the configured voice (if any). Called after engine switch.
    /// </summary>
    private void RefreshTtsVoiceList()
    {
        if (_config is null) return;

        var voices = _ttsService?.GetAvailableVoices() ?? Array.Empty<string>();
        TtsVoiceCombo.ItemsSource = voices;

        if (!string.IsNullOrWhiteSpace(_config.TtsVoiceName) && voices.Contains(_config.TtsVoiceName))
        {
            TtsVoiceCombo.SelectedItem = _config.TtsVoiceName;
        }
        else if (voices.Count > 0)
        {
            TtsVoiceCombo.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Re-populates the TTS output device dropdown from the system mixer.
    /// Adds a "(System default)" entry at the top so the user can clear an
    /// override without editing config manually.
    /// </summary>
    private void RefreshTtsOutputDeviceList()
    {
        if (_config is null) return;

        const string defaultLabel = "(System default)";
        var items = new List<string> { defaultLabel };
        items.AddRange(SystemTextToSpeechService.GetAvailableOutputDevices());
        TtsOutputDeviceCombo.ItemsSource = items;

        if (!string.IsNullOrWhiteSpace(_config.TtsOutputDevice) && items.Contains(_config.TtsOutputDevice))
        {
            TtsOutputDeviceCombo.SelectedItem = _config.TtsOutputDevice;
        }
        else
        {
            TtsOutputDeviceCombo.SelectedIndex = 0;
        }
    }

    /// <summary>
    /// Re-populates the microphone dropdown. Adds "(System default)" so the
    /// user can clear an override.
    /// </summary>
    private void RefreshMicDeviceList()
    {
        if (_config is null) return;

        const string defaultLabel = "(System default)";
        var items = new List<string> { defaultLabel };
        items.AddRange(_audioCaptureService.GetAvailableDevices());
        MicDeviceCombo.ItemsSource = items;

        if (!string.IsNullOrWhiteSpace(_config.SelectedMicrophoneDevice) && items.Contains(_config.SelectedMicrophoneDevice))
        {
            MicDeviceCombo.SelectedItem = _config.SelectedMicrophoneDevice;
        }
        else
        {
            MicDeviceCombo.SelectedIndex = 0;
        }
    }

    // ── TTS event handlers ──────────────────────────────────────────────────

    private void TtsEnabled_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressVoiceUiEvents || _config is null) return;
        _config.TtsEnabled = TtsEnabledCheck.IsChecked == true;
        SaveConfigAsync();
    }

    private void TtsEngine_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressVoiceUiEvents || _config is null) return;

        var newEngine = TtsEnginePiperPill.IsChecked == true ? "piper" : "system";
        if (string.Equals(_config.TtsEngine, newEngine, StringComparison.OrdinalIgnoreCase)) return;

        _config.TtsEngine = newEngine;
        // Voice names don't carry across engines — clear the selection so the
        // dropdown re-defaults to the first voice on the new engine.
        _config.TtsVoiceName = null;
        SaveConfigAsync();

        // Recreate the TTS service so the engine actually changes. Then refresh
        // the voice list — System voices and Piper voices have different names.
        InitializeTts();
        _suppressVoiceUiEvents = true;
        try { RefreshTtsVoiceList(); } finally { _suppressVoiceUiEvents = false; }
    }

    private void TtsVoice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressVoiceUiEvents || _config is null) return;
        if (TtsVoiceCombo.SelectedItem is not string voice) return;

        _config.TtsVoiceName = voice;
        _ttsService?.SetVoice(voice);
        SaveConfigAsync();
    }

    private void TtsRateSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVoiceUiEvents || _config is null) return;

        var rate = (int)Math.Round(e.NewValue);
        if (rate == _config.TtsRate) return;

        _config.TtsRate = rate;
        TtsRateLabel.Text = rate.ToString();
        _ttsService?.SetRate(rate);
        SaveConfigAsync();
    }

    private void TtsVolumeSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVoiceUiEvents || _config is null) return;

        var vol = (int)Math.Round(e.NewValue);
        if (vol == _config.TtsVolume) return;

        _config.TtsVolume = vol;
        TtsVolumeLabel.Text = vol.ToString();
        _ttsService?.SetVolume(vol);
        SaveConfigAsync();
    }

    private void TtsOutputDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressVoiceUiEvents || _config is null) return;
        if (TtsOutputDeviceCombo.SelectedItem is not string label) return;

        // First item is the synthetic "(System default)" — store null in config.
        var deviceName = TtsOutputDeviceCombo.SelectedIndex == 0 ? null : label;
        if (string.Equals(_config.TtsOutputDevice ?? string.Empty, deviceName ?? string.Empty, StringComparison.Ordinal)) return;

        _config.TtsOutputDevice = deviceName;
        SaveConfigAsync();

        // OutputDeviceName is captured by the service at construction time,
        // so recreate to pick up the new device. Cheap — no model load.
        InitializeTts();
    }

    private void TtsOutputRefresh_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _suppressVoiceUiEvents = true;
        try { RefreshTtsOutputDeviceList(); } finally { _suppressVoiceUiEvents = false; }
    }

    private void TtsTest_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_ttsService is null)
        {
            AppendLog("TTS service not initialized.");
            return;
        }
        _ = _ttsService.SpeakAsync("This is a test of the assistant voice.");
    }

    // ── STT event handlers ──────────────────────────────────────────────────

    private void MicDevice_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressVoiceUiEvents || _config is null) return;
        if (MicDeviceCombo.SelectedItem is not string label) return;

        var deviceName = MicDeviceCombo.SelectedIndex == 0 ? null : label;
        if (string.Equals(_config.SelectedMicrophoneDevice ?? string.Empty, deviceName ?? string.Empty, StringComparison.Ordinal)) return;

        _config.SelectedMicrophoneDevice = deviceName;
        SaveConfigAsync();
    }

    private void MicRefresh_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _suppressVoiceUiEvents = true;
        try { RefreshMicDeviceList(); } finally { _suppressVoiceUiEvents = false; }
    }

    private void WhisperSize_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressVoiceUiEvents || _config is null) return;

        WhisperModelSize size;
        if (WhisperTinyPill.IsChecked == true) size = WhisperModelSize.Tiny;
        else if (WhisperSmallPill.IsChecked == true) size = WhisperModelSize.Small;
        else if (WhisperMediumPill.IsChecked == true) size = WhisperModelSize.Medium;
        else size = WhisperModelSize.Base;

        if (_config.WhisperModelSize == size) return;

        _config.WhisperModelSize = size;
        SaveConfigAsync();

        // The loaded Whisper model is held until the process exits or a new
        // InitializeAsync is called. If the new size's .bin is on the SSD we
        // reload immediately so the next voice press uses it; otherwise we
        // surface a hint and let the lazy-load path in OnVoiceClick handle
        // the download.
        var modelPath = WhisperModelManager.GetModelPath(_ssdRoot, size);
        if (File.Exists(modelPath))
        {
            WhisperReloadHint.Text = "Reloading…";
            WhisperReloadHint.Visibility = System.Windows.Visibility.Visible;
            _ = ReloadWhisperModelAsync();
        }
        else
        {
            WhisperReloadHint.Text = $"{size} not on SSD — will download on next voice input.";
            WhisperReloadHint.Visibility = System.Windows.Visibility.Visible;
        }
    }

    private async Task ReloadWhisperModelAsync()
    {
        try
        {
            await _sttService.InitializeAsync(_ssdRoot, _config!);
            await Dispatcher.InvokeAsync(() =>
            {
                WhisperReloadHint.Visibility = System.Windows.Visibility.Collapsed;
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                WhisperReloadHint.Text = $"Reload failed: {ex.Message}";
                WhisperReloadHint.Visibility = System.Windows.Visibility.Visible;
            });
        }
    }

    private void AutoSendVoice_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressVoiceUiEvents || _config is null) return;
        _config.AutoSendVoiceInput = AutoSendVoiceCheck.IsChecked == true;
        SaveConfigAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // System tab — Model parameters card (#58)
    // ─────────────────────────────────────────────────────────────────────────

    private bool _suppressModelParamEvents;

    // Each slider's leftmost step is the "use model default" sentinel zone.
    // Above that step the user is actively overriding; below it (or equal),
    // we treat the value as "unset" and omit the matching Ollama option.
    private const double ModelTemperatureSentinel = 0.0;   // first real step is 0.05
    private const double ModelTopPSentinel = 0.0;          // first real step is 0.05
    private const int ModelMaxOutputSentinel = 0;          // first real step is 128

    private void InitializeModelParametersUi()
    {
        if (_config is null) return;

        _suppressModelParamEvents = true;
        try
        {
            // Context window: 0 = default; otherwise display the token count.
            ModelContextSlider.Value = _config.ModelContextWindow;
            ModelContextLabel.Text = _config.ModelContextWindow > 0
                ? _config.ModelContextWindow.ToString()
                : "default";

            // Temperature: -1 sentinel maps to the slider's leftmost position (<0).
            ModelTemperatureSlider.Value = _config.ModelTemperature >= 0
                ? _config.ModelTemperature
                : ModelTemperatureSlider.Minimum;
            ModelTemperatureLabel.Text = _config.ModelTemperature >= 0
                ? _config.ModelTemperature.ToString("0.00")
                : "default";

            ModelTopPSlider.Value = _config.ModelTopP >= 0
                ? _config.ModelTopP
                : ModelTopPSlider.Minimum;
            ModelTopPLabel.Text = _config.ModelTopP >= 0
                ? _config.ModelTopP.ToString("0.00")
                : "default";

            ModelMaxOutputSlider.Value = _config.ModelMaxOutputTokens >= 0
                ? _config.ModelMaxOutputTokens
                : ModelMaxOutputSlider.Minimum;
            ModelMaxOutputLabel.Text = _config.ModelMaxOutputTokens > 0
                ? _config.ModelMaxOutputTokens.ToString()
                : "unlimited";

            // Thinking: select the item whose Tag matches the stored mode
            // (empty Tag = "Default"). Falls back to Default for unknown values.
            SelectThinkModeItem(_config.ModelThinkMode);
        }
        finally
        {
            _suppressModelParamEvents = false;
        }
    }

    private void SelectThinkModeItem(string? mode)
    {
        var target = (mode ?? string.Empty).Trim().ToLowerInvariant();
        foreach (var obj in ModelThinkModeCombo.Items)
        {
            if (obj is System.Windows.Controls.ComboBoxItem item
                && string.Equals((item.Tag as string) ?? string.Empty, target, StringComparison.OrdinalIgnoreCase))
            {
                ModelThinkModeCombo.SelectedItem = item;
                return;
            }
        }
        ModelThinkModeCombo.SelectedIndex = 0; // Default
    }

    private void ModelContextSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressModelParamEvents || _config is null) return;

        // Snap to 512-token increments, with 0 reserved as "use model default".
        var raw = (int)Math.Round(e.NewValue / 512.0) * 512;
        if (raw < 0) raw = 0;

        if (raw == _config.ModelContextWindow) return;

        _config.ModelContextWindow = raw;
        ModelContextLabel.Text = raw > 0 ? raw.ToString() : "default";
        SaveConfigAsync();
    }

    private void ModelTemperatureSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressModelParamEvents || _config is null) return;

        double newValue;
        string label;
        if (e.NewValue < ModelTemperatureSentinel)
        {
            newValue = -1;
            label = "default";
        }
        else
        {
            newValue = Math.Round(e.NewValue * 20.0) / 20.0; // snap to 0.05
            label = newValue.ToString("0.00");
        }

        if (Math.Abs(newValue - _config.ModelTemperature) < 0.0001) return;

        _config.ModelTemperature = newValue;
        ModelTemperatureLabel.Text = label;
        SaveConfigAsync();
    }

    private void ModelTopPSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressModelParamEvents || _config is null) return;

        double newValue;
        string label;
        if (e.NewValue < ModelTopPSentinel)
        {
            newValue = -1;
            label = "default";
        }
        else
        {
            newValue = Math.Round(e.NewValue * 20.0) / 20.0; // snap to 0.05
            label = newValue.ToString("0.00");
        }

        if (Math.Abs(newValue - _config.ModelTopP) < 0.0001) return;

        _config.ModelTopP = newValue;
        ModelTopPLabel.Text = label;
        SaveConfigAsync();
    }

    private void ModelMaxOutputSlider_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressModelParamEvents || _config is null) return;

        int newValue;
        string label;
        if (e.NewValue < ModelMaxOutputSentinel)
        {
            newValue = -1;
            label = "unlimited";
        }
        else
        {
            newValue = (int)Math.Round(e.NewValue / 128.0) * 128;
            label = newValue > 0 ? newValue.ToString() : "unlimited";
            if (newValue == 0) newValue = -1; // 0 + sentinel collapse onto "unlimited"
        }

        if (newValue == _config.ModelMaxOutputTokens) return;

        _config.ModelMaxOutputTokens = newValue;
        ModelMaxOutputLabel.Text = label;
        SaveConfigAsync();
    }

    private void ModelThinkModeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressModelParamEvents || _config is null) return;

        var mode = (ModelThinkModeCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? string.Empty;
        if (string.Equals(mode, _config.ModelThinkMode, StringComparison.Ordinal)) return;

        _config.ModelThinkMode = mode;
        SaveConfigAsync();
    }

    private void ResetModelParameters_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_config is null) return;

        _config.ModelContextWindow = 0;
        _config.ModelTemperature = -1;
        _config.ModelTopP = -1;
        _config.ModelMaxOutputTokens = -1;
        _config.ModelThinkMode = "";

        InitializeModelParametersUi(); // re-snap sliders + labels under suppression
        SaveConfigAsync();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // All shutdown work runs synchronously here, each step bounded.
        //
        // Why not the async-void Closed handler we used to register?
        // 1) Closed fires AFTER OnClosing returns and AFTER the dispatcher
        //    has begun unwinding — async continuations that capture the
        //    dispatcher context can fail to resume, leaving the process
        //    alive with a leaked Kestrel host.
        // 2) WebApplication.StopAsync has no default cap on graceful drain;
        //    an in-flight Companion request can keep it parked indefinitely.
        //
        // Each step swallows exceptions: we are tearing the window down and
        // a noisy stop must never block exit. See task #55.

        // 1. Stop HOTAS / PTT polling — synchronous, fast.
        TrySafe(CleanupPtt, "CleanupPtt");

        // 2. Stop the LAN API with a hard 5s budget. ConfigureAwait(false) is
        //    set inside StopAsync so .GetAwaiter().GetResult() can't deadlock
        //    the UI thread on a continuation that wants the dispatcher back.
        TrySafe(() =>
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _localApiService.StopAsync(stopCts.Token).GetAwaiter().GetResult();
        }, "LocalApi.StopAsync");

        // 3. Dispose the STT service (releases the Whisper context handle).
        TrySafe(() => _sttService.Dispose(), "Stt.Dispose");

        // 4. Drain queued config saves, then zero the in-memory key.
        TrySafe(() => _configStore.FlushAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(),
            "ConfigStore.FlushAsync");
        TrySafe(_configStore.LockSession, "ConfigStore.LockSession");

        base.OnClosing(e);
    }

    private void TrySafe(Action step, string name)
    {
        try { step(); }
        catch (Exception ex) { AppendLog($"Shutdown step '{name}' failed: {ex.Message}"); }
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
