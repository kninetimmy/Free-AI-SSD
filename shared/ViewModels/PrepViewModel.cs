using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Mvvm;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Shared.ViewModels;

public class PrepViewModel : BaseViewModel
{
    private readonly IDriveService _driveService;
    private readonly IModelService _modelService;
    private readonly IOllamaPackageService _ollamaPackageService;
    private readonly IPrereqService _prereqService;
    private readonly IArtifactStagingService _artifactStagingService;
    private readonly IReadinessService _readinessService;
    private readonly IEncryptionService _encryptionService;
    private readonly IDialogService _dialogService;
    private readonly ILogService _logService;
    private readonly IElevationService _elevationService;

    private IReadOnlyList<DriveTarget> _drives = Array.Empty<DriveTarget>();
    private DriveTarget? _selectedDrive;
    private bool _showFixedDrives;
    private bool _isSelectedDriveEncrypted;
    private string _statusText = string.Empty;
    private double _progressValue;
    private bool _progressIsIndeterminate;
    private bool _isModelOperationRunning;
    // MAC31: receives ANSI-stripped pull progress lines from
    // ModelOperations.Consume's onProgress channel so the view can
    // render a single in-place progress label rather than letting
    // Ollama's TUI rewrite ticks scroll the log surface.
    private string _pullProgressLine = string.Empty;
    private string _modelTagInput = string.Empty;
    private string _ollamaUrl = OllamaPackageTrustPolicy.DefaultWindowsPackage.Url;
    private bool _prepareWindows = true;
    private bool _prepareMac;
    private bool _isMacPrepAvailable;
    private string _macPrepAvailabilityMessage = string.Empty;
    private string _prereqStatusText = string.Empty;
    private bool _enableEncryption;
    private string _volumeLabel = "Portable AI";
    private CancellationTokenSource? _modelOperationCts;
    private int? _systemRamGb;
    private int? _gpuVramGb;
    private bool _installVrCompanion;
    private string _companionHostAddress = string.Empty;
    private int _companionHostPort = 41555;
    private UserProfile? _selectedProfile;
    private string _profileSelectionWarning = string.Empty;
    private readonly SynchronizationContext? _uiSyncContext;

    // B3-Redux phase 2 state: filled from command-line args at startup
    // so the view model can decide whether to auto-resume a format
    // across the UAC relaunch and whether to emit diagnostic logging.
    private bool _diagEnabled;
    private string? _autoResumeFormatRoot;
    private string _autoResumeFormatLabel = string.Empty;
    private bool _isElevated;

    private readonly HashSet<string> _provenanceCheckedRoots = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<StarterCatalogEntry> _starterCatalog = Array.Empty<StarterCatalogEntry>();

    public PrepViewModel(
        IDriveService driveService,
        IModelService modelService,
        IOllamaPackageService ollamaPackageService,
        IPrereqService prereqService,
        IArtifactStagingService artifactStagingService,
        IReadinessService readinessService,
        IEncryptionService encryptionService,
        IDialogService dialogService,
        ILogService logService,
        IElevationService elevationService)
    {
        _driveService = driveService;
        _modelService = modelService;
        _ollamaPackageService = ollamaPackageService;
        _prereqService = prereqService;
        _artifactStagingService = artifactStagingService;
        _readinessService = readinessService;
        _encryptionService = encryptionService;
        _dialogService = dialogService;
        _logService = logService;
        _elevationService = elevationService;
        _isElevated = elevationService.IsElevated();
        _uiSyncContext = SynchronizationContext.Current;

        ModelRows = new ObservableCollection<ModelGridRow>();
        ReadinessItems = new ObservableCollection<ReadinessItem>();
        LogLines = new ObservableCollection<string>();

        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        AddModelCommand = new AsyncRelayCommand(AddModelAsync, () => CanMutateDrive && HasDriveSelected);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        AddOrphanToConfigCommand = new AsyncRelayCommand(AddOrphanToConfigAsync, () => CanMutateDrive && HasDriveSelected);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => CanMutateDrive && HasDriveSelected);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => CanMutateDrive && HasDriveSelected);
        CancelOperationCommand = new RelayCommand(CancelOperation, () => _isModelOperationRunning);
        FormatPrepareCommand = new AsyncRelayCommand(FormatPrepareAsync, () => CanMutateDrive && HasDriveSelected);
        FinalizeCommand = new AsyncRelayCommand(FinalizeAsync, () => CanMutateDrive && HasDriveSelected);
        CheckPrereqUpdatesCommand = new AsyncRelayCommand(CheckPrereqUpdatesAsync, () => CanMutateDrive && HasDriveSelected);
        CheckReadinessCommand = new AsyncRelayCommand(CheckReadinessAsync, () => CanMutateDrive && HasDriveSelected);
    }

    public IReadOnlyList<DriveTarget> Drives
    {
        get => _drives;
        private set => SetProperty(ref _drives, value);
    }

    public DriveTarget? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value))
            {
                OnSelectedDriveChanged();
            }
        }
    }

    public bool ShowFixedDrives
    {
        get => _showFixedDrives;
        set
        {
            if (SetProperty(ref _showFixedDrives, value))
            {
                RefreshDrives();
            }
        }
    }

    public bool IsSelectedDriveEncrypted
    {
        get => _isSelectedDriveEncrypted;
        private set => SetProperty(ref _isSelectedDriveEncrypted, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public bool ProgressIsIndeterminate
    {
        get => _progressIsIndeterminate;
        set => SetProperty(ref _progressIsIndeterminate, value);
    }

    public bool IsModelOperationRunning
    {
        get => _isModelOperationRunning;
        private set
        {
            if (SetProperty(ref _isModelOperationRunning, value))
            {
                RaiseAllCommandsCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// MAC31: latest pull progress line (ANSI-stripped, coalesced from
    /// Ollama's TUI rewrites). Bound to a single Text element so a
    /// long pull renders as one in-place line rather than spamming
    /// the log surface. Empty between pulls.
    /// </summary>
    public string PullProgressLine
    {
        get => _pullProgressLine;
        private set => SetProperty(ref _pullProgressLine, value);
    }

    public string ModelTagInput
    {
        get => _modelTagInput;
        set => SetProperty(ref _modelTagInput, value);
    }

    public string OllamaUrl
    {
        get => _ollamaUrl;
        set => SetProperty(ref _ollamaUrl, value);
    }

    public Action? OnPreferenceStateChanged { get; set; }

    public bool PrepareWindows
    {
        get => _prepareWindows;
        set
        {
            if (SetProperty(ref _prepareWindows, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public bool PrepareMac
    {
        get => _prepareMac;
        set
        {
            if (!_isMacPrepAvailable) value = false;
            if (SetProperty(ref _prepareMac, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public bool IsMacPrepAvailable
    {
        get => _isMacPrepAvailable;
        private set => SetProperty(ref _isMacPrepAvailable, value);
    }

    public string MacPrepAvailabilityMessage
    {
        get => _macPrepAvailabilityMessage;
        private set => SetProperty(ref _macPrepAvailabilityMessage, value);
    }

    public string PrereqStatusText
    {
        get => _prereqStatusText;
        set => SetProperty(ref _prereqStatusText, value);
    }

    public bool EnableEncryption
    {
        get => _enableEncryption;
        set => SetProperty(ref _enableEncryption, value);
    }

    public string VolumeLabel
    {
        get => _volumeLabel;
        set => SetProperty(ref _volumeLabel, value);
    }

    /// <summary>
    /// True when the current process is running with admin rights. Drives
    /// the persistent elevation banner in the PrepApp window so the user
    /// always knows which window (elevated vs. not) they're looking at.
    /// </summary>
    public bool IsElevated
    {
        get => _isElevated;
        private set
        {
            if (SetProperty(ref _isElevated, value))
            {
                OnPropertyChanged(nameof(ElevationBannerText));
            }
        }
    }

    /// <summary>
    /// True iff valid auto-resume intent was parsed from startup args
    /// (root + label survived revalidation). Used by the banner to pick
    /// the "ready to continue" copy over the generic "click format"
    /// fallback copy.
    /// </summary>
    public bool HasAutoResumeIntent => !string.IsNullOrEmpty(_autoResumeFormatRoot);

    /// <summary>
    /// Copy shown in the elevation banner. Consumers bind
    /// <see cref="IsElevated"/> to Visibility and this string to Text.
    /// </summary>
    public string ElevationBannerText =>
        HasAutoResumeIntent
            ? "Running as administrator — format operation ready to continue."
            : "Running as administrator. Click Format & Prepare Drive to continue.";

    public bool InstallVrCompanion
    {
        get => _installVrCompanion;
        set
        {
            if (SetProperty(ref _installVrCompanion, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public string CompanionHostAddress
    {
        get => _companionHostAddress;
        set
        {
            if (SetProperty(ref _companionHostAddress, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public int CompanionHostPort
    {
        get => _companionHostPort;
        set
        {
            if (SetProperty(ref _companionHostPort, value))
                OnPreferenceStateChanged?.Invoke();
        }
    }

    public UserProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                OnPropertyChanged(nameof(HasSelectedProfile));
                if (_selectedProfile is not null)
                    ProfileSelectionWarning = string.Empty;
                OnPreferenceStateChanged?.Invoke();
            }
        }
    }

    public bool HasSelectedProfile => _selectedProfile is not null;

    public string ProfileSelectionWarning
    {
        get => _profileSelectionWarning;
        private set => SetProperty(ref _profileSelectionWarning, value);
    }

    public int? SystemRamGb
    {
        get => _systemRamGb;
        set => SetProperty(ref _systemRamGb, value);
    }

    public int? GpuVramGb
    {
        get => _gpuVramGb;
        set => SetProperty(ref _gpuVramGb, value);
    }

    public ObservableCollection<ModelGridRow> ModelRows { get; }
    public ObservableCollection<ReadinessItem> ReadinessItems { get; }
    public ObservableCollection<string> LogLines { get; }

    public RelayCommand RefreshDrivesCommand { get; }
    public AsyncRelayCommand AddModelCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand AddOrphanToConfigCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public RelayCommand CancelOperationCommand { get; }
    public AsyncRelayCommand FormatPrepareCommand { get; }
    public AsyncRelayCommand FinalizeCommand { get; }
    public AsyncRelayCommand CheckPrereqUpdatesCommand { get; }
    public AsyncRelayCommand CheckReadinessCommand { get; }

    public bool CanMutateDrive => !_isModelOperationRunning && !_isSelectedDriveEncrypted;
    public bool HasDriveSelected => _selectedDrive is not null;

    public string SelectedDriveWarning
    {
        get
        {
            var warnings = new List<string>();
            if (_selectedDrive is not null && !string.IsNullOrWhiteSpace(_selectedDrive.Warning))
                warnings.Add(_selectedDrive.Warning);
            if (_isSelectedDriveEncrypted)
                warnings.Add(PrepDriveWriteGuard.ReadOnlyReason);
            return string.Join(Environment.NewLine, warnings);
        }
    }

    public void Initialize()
    {
        CheckMacArtifactAvailability();
        RefreshDrives();
    }

    /// <summary>
    /// Seed the view model with values parsed from command-line args.
    /// Called by MainWindow after construction and before
    /// <see cref="Initialize"/>. Values are expected to have already
    /// been revalidated by <c>PrepStartupArgs.Parse</c> — this method
    /// does no further input validation, it just stashes the state.
    /// </summary>
    public void ApplyStartupIntent(
        string? autoResumeFormatRoot,
        string autoResumeFormatLabel,
        bool diagEnabled)
    {
        _autoResumeFormatRoot = autoResumeFormatRoot;
        _autoResumeFormatLabel = autoResumeFormatLabel ?? string.Empty;
        _diagEnabled = diagEnabled;
        OnPropertyChanged(nameof(HasAutoResumeIntent));
        OnPropertyChanged(nameof(ElevationBannerText));
    }

    /// <summary>
    /// If auto-resume intent was set via <see cref="ApplyStartupIntent"/>,
    /// attempts to resume the format operation that triggered the UAC
    /// relaunch. Intent is consumed on attempt (pass or fail) so it
    /// never fires twice. Safe to call when no intent is set — returns
    /// immediately. Must run after <see cref="Initialize"/>.
    /// </summary>
    public async Task TryAutoResumeFormatAsync()
    {
        var root = _autoResumeFormatRoot;
        if (string.IsNullOrEmpty(root)) return;

        // Consume intent before any branch that might leave state mid-
        // flight. The banner flips to its no-intent copy after this.
        var requestedLabel = _autoResumeFormatLabel;
        _autoResumeFormatRoot = null;
        _autoResumeFormatLabel = string.Empty;
        OnPropertyChanged(nameof(HasAutoResumeIntent));
        OnPropertyChanged(nameof(ElevationBannerText));

        // Drive-letter drift guard: user may have unplugged the SSD
        // between Format-click and UAC-approval. Re-enumerate fresh
        // (not relying on Drives already being populated) and bail if
        // the requested root is no longer present.
        var drives = _driveService.GetCandidateDrives(_showFixedDrives);
        var match = drives.FirstOrDefault(d =>
            string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            AppendLog($"Auto-resume format cancelled: drive {root} is no longer present.");
            _dialogService.ShowWarning(
                $"The drive {root} that you asked to format is no longer connected." + Environment.NewLine + Environment.NewLine +
                "Reconnect the drive and click Format & Prepare Drive to continue.",
                "Drive not found");
            return;
        }

        Drives = drives;
        SelectedDrive = match;
        VolumeLabel = requestedLabel;
        AppendLog($"Auto-resume: continuing format of {root} (label '{requestedLabel}') after UAC relaunch.");

        // FormatPrepareAsync contains the non-negotiable ConfirmErase
        // dialog — that is the post-relaunch safety gate the user must
        // click through before Format-Volume actually runs. We never
        // bypass it.
        await FormatPrepareAsync();
    }

    private void RefreshDrives()
    {
        Drives = _driveService.GetCandidateDrives(_showFixedDrives);
        SelectedDrive = Drives.Count > 0 ? Drives[0] : null;
    }

    private void OnSelectedDriveChanged()
    {
        RefreshEncryptionState();
        OnPropertyChanged(nameof(SelectedDriveWarning));
        OnPropertyChanged(nameof(CanMutateDrive));
        OnPropertyChanged(nameof(HasDriveSelected));
        RaiseAllCommandsCanExecuteChanged();
        _ = RefreshModelStatusesAsync();
        _ = CheckAndPromptLibraryReindexAsync();
    }

    private async Task CheckAndPromptLibraryReindexAsync()
    {
        if (_isModelOperationRunning) return;
        if (_isSelectedDriveEncrypted) return;
        if (_selectedDrive is null) return;

        var root = _selectedDrive.RootPath;
        if (!_provenanceCheckedRoots.Add(root)) return;

        PortableConfig config;
        try { config = await _modelService.LoadConfigAsync(GetConfigPath(root)); }
        catch { return; }

        var currentModel = config.EmbeddingModelName;
        if (string.IsNullOrWhiteSpace(currentModel)) return;

        var libraryManager = new DocumentLibraryManager(root);
        var mismatches = libraryManager.ScanProvenanceMismatches(currentModel);
        if (mismatches.Count == 0) return;

        var lines = string.Join("\n", mismatches.Select(m => $"- {m.Name} ({m.LastEmbeddingModel})"));
        var message = $"The following document libraries were indexed with a different embedding model:\n\n{lines}\n\nCurrent model: '{currentModel}'. Reindex all affected libraries now?";
        const string title = "Embedding Model Changed — Reindex Required";

        bool confirm;
        if (_uiSyncContext is null || SynchronizationContext.Current == _uiSyncContext)
        {
            confirm = _dialogService.Confirm(message, title);
        }
        else
        {
            var tcs = new TaskCompletionSource<bool>();
            _uiSyncContext.Post(_ => tcs.SetResult(_dialogService.Confirm(message, title)), null);
            confirm = await tcs.Task;
        }

        if (!confirm)
        {
            AppendLog("Reindex skipped. Document search results may be incorrect until libraries are reindexed.");
            return;
        }

        var ollamaDir = Path.Combine(root, SsdLayout.Ollama);
        var ollamaExe = _ollamaPackageService.ResolveOllamaExe(ollamaDir);
        if (ollamaExe is null)
        {
            AppendLog("Reindex aborted: Ollama executable not found on this drive. Run Finalize first.");
            return;
        }

        var modelsRoot = Path.Combine(root, SsdLayout.Models);
        SetModelOperationState(true, "Reindexing document libraries...");
        IOllamaServerHandle? serverHandle = null;
        try
        {
            serverHandle = await _ollamaPackageService.StartTemporaryServerAsync(ollamaExe, modelsRoot, AppendLog, CancellationToken.None);
            var ingestor = new DocumentIngestor(libraryManager, new EmbeddingClient());

            foreach (var manifest in mismatches)
            {
                AppendLog($"Reindexing '{manifest.Name}'...");
                try
                {
                    await ingestor.RebuildIndexAsync(manifest, serverHandle.Host, config, cancellationToken: CancellationToken.None);
                    AppendLog($"Reindexed '{manifest.Name}' successfully.");
                }
                catch (Exception ex)
                {
                    AppendLog($"Reindex failed for '{manifest.Name}': {ex.Message}");
                }
            }
            SetModelOperationState(false, "Reindex complete");
        }
        catch (Exception ex)
        {
            AppendLog($"Reindex operation failed: {ex.Message}");
            SetModelOperationState(false, "Reindex failed");
        }
        finally
        {
            serverHandle?.Dispose();
            if (_isModelOperationRunning)
                SetModelOperationState(false);
        }
    }

    private void RefreshEncryptionState()
    {
        if (_selectedDrive is not null)
        {
            IsSelectedDriveEncrypted = _encryptionService.IsEncryptionEnabled(_selectedDrive.RootPath);
        }
        else
        {
            IsSelectedDriveEncrypted = false;
        }

        if (_isSelectedDriveEncrypted && !_isModelOperationRunning)
        {
            StatusText = "Encrypted drive selected (read-only in PrepApp)";
        }
    }

    private bool EnsureWritable(string operationName)
    {
        if (_selectedDrive is null)
        {
            StatusText = "Select a target drive first";
            AppendLog($"{operationName} blocked: no drive selected.");
            return false;
        }
        if (!_driveService.EnsureWritable(_selectedDrive.RootPath, operationName, out var blockedMessage))
        {
            StatusText = "Encrypted drive selected (read-only in PrepApp)";
            AppendLog(blockedMessage ?? $"{operationName} blocked: drive is encrypted.");
            return false;
        }
        return true;
    }

    private void CheckMacArtifactAvailability()
    {
        IsMacPrepAvailable = _artifactStagingService.AreMacArtifactsAvailable(out var problem);
        MacPrepAvailabilityMessage = problem ?? string.Empty;
        if (!_isMacPrepAvailable && _prepareMac)
        {
            PrepareMac = false;
            PrepareWindows = true;
        }
    }

    private PrepTargets GetSelectedPrepTargets()
    {
        var targets = PrepTargets.None;
        if (_prepareWindows) targets |= PrepTargets.Windows;
        if (_prepareMac) targets |= PrepTargets.Mac;
        return targets;
    }

    // MAC10a: Windows-only → NTFS, anything that includes Mac → exFAT.
    // APFS is deferred (MAC1) until a Mac-native prep workflow exists, so
    // exFAT is the only cross-OS option Windows PrepApp can stage today.
    internal static string ResolveFileSystem(PrepTargets targets) =>
        targets switch
        {
            PrepTargets.None => throw new InvalidOperationException(
                "PrepTargets must include Windows or Mac before resolving a filesystem."),
            PrepTargets.Windows => "NTFS",
            _ => "exFAT",
        };

    private async Task AddModelAsync()
    {
        var tag = (_modelTagInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            AppendLog("Enter a model tag before adding.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Add model")) return;

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        _driveService.EnsureSsdStructure(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);
        _modelService.UpsertModel(config.Models, tag, ModelInstallStatus.NotInstalled);
        await _modelService.SaveConfigAsync(configPath, config);
        await RefreshModelStatusesAsync();
        ModelTagInput = string.Empty;
        AppendLog($"Added model '{tag}' to config.");
    }

    private void ClearSelection()
    {
        foreach (var row in ModelRows)
            row.IsSelected = false;
    }

    /// <summary>
    /// Seed the VM with the starter-catalog entries the PrepApp loaded
    /// from JSON. Calling this triggers a grid refresh so recommended
    /// rows appear immediately — even before the user has selected a
    /// drive or added anything to config. Idempotent.
    /// </summary>
    public async Task SetStarterCatalogAsync(IEnumerable<StarterCatalogEntry> entries)
    {
        _starterCatalog = entries?.ToList() ?? new List<StarterCatalogEntry>();
        await RefreshModelStatusesAsync();
    }

    private async Task AddOrphanToConfigAsync()
    {
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Add on-disk model to config")) return;

        var selectedOrphans = GetCheckedModelRows().Where(r => r.IsOnDiskOnly).ToList();
        if (selectedOrphans.Count == 0)
        {
            AppendLog("Check one or more OnDiskOnly model rows to add to config.");
            return;
        }

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);
        foreach (var row in selectedOrphans)
        {
            _modelService.UpsertModel(config.Models, row.Name, ModelInstallStatus.NotInstalled);
            AppendLog($"Added orphaned model '{row.Name}' to config.");
        }
        await _modelService.SaveConfigAsync(configPath, config);
        await RefreshModelStatusesAsync();
    }

    private IReadOnlyList<ModelGridRow> GetCheckedModelRows()
        => ModelRows.Where(r => r.IsSelected).ToList().AsReadOnly();

    private static bool IsStarterOnlyRecommendationRow(ModelGridRow row)
        => string.Equals(row.Source, "Recommended", StringComparison.OrdinalIgnoreCase);

    private static string DescribeRemoveSelection(IReadOnlyList<ModelGridRow> rows)
        => rows.Count == 1 ? rows[0].Name : $"{rows.Count} selected models";

    private async Task DownloadAsync()
    {
        if (!EnsureWritable("Download models")) return;
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        var checkedRows = GetCheckedModelRows();
        if (checkedRows.Count == 0)
        {
            StatusText = "No models checked — check one or more models to download";
            AppendLog("Check one or more models to download.");
            return;
        }

        var selected = checkedRows
            .Where(r => !r.IsPresentOnDrive)
            .Select(r => r.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0)
        {
            StatusText = "All checked models are already on the drive";
            AppendLog("All checked models are already on the drive — nothing to download.");
            return;
        }

        var skippedCount = checkedRows.Count - selected.Count;
        if (skippedCount > 0)
            AppendLog($"Skipping {skippedCount} checked row(s) already on the drive.");

        _driveService.EnsureSsdStructure(_selectedDrive.RootPath);

        if (!ConfirmSizingWarningsIfNeeded(selected)) return;
        await PullModelsAsync(selected);
        ClearSelection();
    }

    private bool ConfirmSizingWarningsIfNeeded(IReadOnlyList<string> models)
    {
        if (_selectedDrive is null) return true;

        var warnings = _modelService.BuildPullSelectionWarnings(models, _selectedDrive.RootPath, _systemRamGb, _gpuVramGb);
        if (warnings.Count > 0)
        {
            if (!_dialogService.ConfirmSizingWarnings(warnings))
            {
                AppendLog("Download cancelled after sizing warning.");
                return false;
            }
        }
        return true;
    }

    private async Task PullModelsAsync(IReadOnlyList<string> models)
    {
        if (_isModelOperationRunning)
        {
            AppendLog("A model operation is already running.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Download operation")) return;

        var root = _selectedDrive.RootPath;
        var configPath = GetConfigPath(root);
        _modelOperationCts = new CancellationTokenSource();
        SetModelOperationState(true, "Downloading...");
        ProgressIsIndeterminate = true;

        IOllamaServerHandle? serverHandle = null;
        try
        {
            var ollamaExe = await _ollamaPackageService.EnsureOllamaReadyAsync(
                root, _ollamaUrl, AppendLog,
                new Progress<DownloadProgress>(p =>
                {
                    ProgressIsIndeterminate = false;
                    ProgressValue = p.Percent;
                    StatusText = $"Downloading Ollama {p.Percent:F1}%";
                }),
                _modelOperationCts.Token);

            var modelsRoot = Path.Combine(root, SsdLayout.Models);

            // Start a controlled temporary server so that `ollama pull` doesn't
            // auto-start an uncontrolled background server (which can open tray
            // icons, persist after the app exits, and cause crashes).
            ProgressIsIndeterminate = true;
            StatusText = "Starting temporary Ollama server...";
            serverHandle = await _ollamaPackageService.StartTemporaryServerAsync(
                ollamaExe, modelsRoot, AppendLog, _modelOperationCts.Token);

            foreach (var model in models)
            {
                _modelOperationCts.Token.ThrowIfCancellationRequested();
                await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Downloading);
                StatusText = $"Downloading {model}...";
                AppendLog($"Downloading {model}...");

                // MAC31: seed the in-place progress line with a "Resuming
                // from NN%..." message when partial blobs already exist
                // for this tag. Without this, a retry after a cancelled
                // pull starts visually at 0% even though Ollama IS
                // resumable — which made cancel-and-retry feel broken
                // in the v1.3.10 mac field test.
                var seed = _modelService.EstimatePartialPullProgress(modelsRoot, model);
                PullProgressLine = seed > 0
                    ? $"Resuming {model} from {seed:P0}…"
                    : $"Pulling {model}…";

                try
                {
                    var result = await _modelService.PullModelAsync(
                        ollamaExe, modelsRoot, model, AppendLog, _modelOperationCts.Token, serverHandle.Host,
                        // MAC31: route Ollama's TUI ticks (ANSI-stripped
                        // by OllamaPullProgressFilter) into the single
                        // in-place label instead of the scrolling log.
                        // SetPullProgressLineSafe dispatches via the UI
                        // sync context because the lambda fires from
                        // ModelOperations.Consume's drain thread.
                        onProgress: SetPullProgressLineSafe);

                    await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Installed, result.Sha256, result.SizeBytes, DateTime.UtcNow);
                    AppendLog($"Downloaded {model} ({FormatSize(result.SizeBytes)}). Sha256 {result.Sha256[..8]}...");
                }
                catch (OperationCanceledException)
                {
                    await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.NotInstalled);
                    AppendLog($"Download cancelled for {model}.");
                    throw;
                }
                catch (Exception ex)
                {
                    await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Failed);
                    AppendLog($"Failed to download {model}: {ex.Message}");
                }
            }
            StatusText = "Download complete";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download cancelled";
        }
        finally
        {
            serverHandle?.Dispose();
            ProgressIsIndeterminate = false;
            // MAC31: clear the in-place progress label when the batch
            // ends so the next pull starts from a known-empty state.
            PullProgressLine = string.Empty;
            SetModelOperationState(false, StatusText);
            _modelOperationCts?.Dispose();
            _modelOperationCts = null;
            await RefreshModelStatusesAsync();
        }
    }

    private async Task RemoveAsync()
    {
        if (_isModelOperationRunning)
        {
            AppendLog("Cannot remove while another model operation is running.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Remove/delete model")) return;

        var checkedRows = GetCheckedModelRows();
        if (checkedRows.Count == 0)
        {
            AppendLog("Check one or more models to remove.");
            return;
        }

        var removableRows = checkedRows.Where(row => !IsStarterOnlyRecommendationRow(row)).ToList();
        if (removableRows.Count == 0)
        {
            AppendLog("Remove only works for models already in the configuration or on the drive.");
            return;
        }

        var skippedCount = checkedRows.Count - removableRows.Count;
        if (skippedCount > 0)
            AppendLog($"Skipping {skippedCount} recommended row(s) that are not yet on the drive or in the configuration.");

        var choice = _dialogService.PromptRemoveModel(DescribeRemoveSelection(removableRows));
        if (choice == ModelRemoveChoice.Cancel)
        {
            AppendLog("Remove cancelled.");
            return;
        }

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);

        if (choice == ModelRemoveChoice.ConfigOnly)
        {
            var configDirty = false;
            foreach (var selectedRow in removableRows)
            {
                var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, selectedRow.Name, StringComparison.OrdinalIgnoreCase));
                if (model is null)
                {
                    AppendLog($"{selectedRow.Name} is already only on the drive and not in the configuration.");
                    continue;
                }

                config.Models.Remove(model);
                configDirty = true;
                AppendLog(selectedRow.IsPresentOnDrive
                    ? $"{selectedRow.Name}: removed from configuration only (disk contents kept)."
                    : $"{selectedRow.Name}: removed from configuration.");
            }

            if (configDirty)
                await _modelService.SaveConfigAsync(configPath, config);

            await RefreshModelStatusesAsync();
            return;
        }

        var deleteFailures = new List<string>();
        var configDirtyForDelete = false;
        SetModelOperationState(
            true,
            removableRows.Count == 1
                ? $"Deleting {removableRows[0].Name} from disk..."
                : $"Deleting {removableRows.Count} models from disk...");
        IOllamaServerHandle? serverHandle = null;
        try
        {
            foreach (var selectedRow in removableRows.Where(row => !row.IsPresentOnDrive))
            {
                var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, selectedRow.Name, StringComparison.OrdinalIgnoreCase));
                if (model is null)
                {
                    AppendLog($"{selectedRow.Name}: no on-disk files were present to delete.");
                    continue;
                }

                config.Models.Remove(model);
                configDirtyForDelete = true;
                AppendLog($"{selectedRow.Name}: removed from configuration (no on-disk files were present).");
            }

            var rowsPresentOnDrive = removableRows.Where(row => row.IsPresentOnDrive).ToList();
            if (rowsPresentOnDrive.Count > 0)
            {
                var ollamaExe = await _ollamaPackageService.EnsureOllamaReadyAsync(
                    _selectedDrive.RootPath, _ollamaUrl, AppendLog, null, CancellationToken.None);
                var modelsRoot = Path.Combine(_selectedDrive.RootPath, SsdLayout.Models);

                // Start a controlled temporary server once for the whole batch.
                serverHandle = await _ollamaPackageService.StartTemporaryServerAsync(
                    ollamaExe, modelsRoot, AppendLog, CancellationToken.None);

                foreach (var selectedRow in rowsPresentOnDrive)
                {
                    try
                    {
                        AppendLog($"Deleting {selectedRow.Name} from disk with ollama rm...");
                        await _modelService.DeleteModelAsync(ollamaExe, modelsRoot, selectedRow.Name, AppendLog, CancellationToken.None, serverHandle.Host);

                        var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, selectedRow.Name, StringComparison.OrdinalIgnoreCase));
                        if (model is not null)
                        {
                            config.Models.Remove(model);
                            configDirtyForDelete = true;
                            AppendLog($"{selectedRow.Name}: deleted from disk and removed from configuration.");
                        }
                        else
                        {
                            AppendLog($"{selectedRow.Name}: deleted from disk.");
                        }
                    }
                    catch (Exception ex)
                    {
                        deleteFailures.Add($"{selectedRow.Name}: {ex.Message}");
                        AppendLog($"Delete failed for {selectedRow.Name}: {ex.Message}");
                    }
                }
            }

            if (configDirtyForDelete)
                await _modelService.SaveConfigAsync(configPath, config);

            if (deleteFailures.Count == 1)
            {
                _dialogService.ShowError($"Failed to delete model from disk: {deleteFailures[0]}", "Delete failed");
            }
            else if (deleteFailures.Count > 1)
            {
                var message =
                    "Failed to delete some models from disk:"
                    + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, deleteFailures.Select(f => $"- {f}"));
                _dialogService.ShowError(message, "Delete failed");
            }
        }
        finally
        {
            serverHandle?.Dispose();
            SetModelOperationState(false);
            await RefreshModelStatusesAsync();
        }
    }

    private void CancelOperation()
    {
        _modelOperationCts?.Cancel();
        AppendLog("Cancellation requested for current model operation.");
    }

    private async Task FormatPrepareAsync()
    {
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Format & Prepare Drive")) return;

        var root = _selectedDrive.RootPath;

        // Refuse to format the drive the PrepApp itself is running from —
        // that would wipe the running executable out from under us.
        var appRoot = Path.GetPathRoot(AppContext.BaseDirectory);
        if (!string.IsNullOrEmpty(appRoot) &&
            string.Equals(Path.GetPathRoot(root), appRoot, StringComparison.OrdinalIgnoreCase))
        {
            _dialogService.ShowError(
                $"Cannot format {root} because PrepApp is running from that drive.{Environment.NewLine}" +
                "Move PrepApp to another drive and try again.",
                "Self-format blocked");
            AppendLog($"Format aborted: PrepApp is running from {appRoot}.");
            return;
        }

        if (_selectedDrive.IsFixed)
        {
            if (!_dialogService.ConfirmFixedDrive(root))
            {
                AppendLog("Format cancelled by user.");
                return;
            }
        }

        var fileSystem = ResolveFileSystem(GetSelectedPrepTargets());

        if (!_dialogService.ConfirmErase(root,
            _driveService.GetFreeDiskSpaceGb(root)?.ToString() ?? "unknown",
            fileSystem))
        {
            AppendLog("Format cancelled by user.");
            return;
        }

        if (!_elevationService.IsElevated())
        {
            var relaunch = _dialogService.Confirm(
                "Formatting a drive requires administrator privileges." + Environment.NewLine + Environment.NewLine +
                "Relaunch PrepApp as administrator now? You'll be prompted by Windows to approve.",
                "Administrator required");
            if (!relaunch)
            {
                AppendLog("Format cancelled: administrator privileges required.");
                return;
            }

            try
            {
                // Forward the current format intent across the UAC gap so
                // the elevated instance can auto-resume (subject to the
                // ConfirmErase dialog as the non-negotiable safety gate)
                // instead of leaving the user staring at a fresh PrepApp
                // window wondering why their format didn't proceed.
                var relaunchArgs = new List<string>
                {
                    $"--autoresume-format={root}",
                    $"--autoresume-label={_volumeLabel ?? string.Empty}"
                };
                if (_diagEnabled) relaunchArgs.Add("--diag");

                if (!_elevationService.TryRelaunchElevated(relaunchArgs))
                {
                    AppendLog("Format cancelled: UAC prompt was declined.");
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"Could not relaunch as administrator: {ex.Message}", "Elevation failed");
                AppendLog($"Elevated relaunch failed: {ex.Message}");
            }
            return;
        }

        // Mark the whole format+prepare flow as a busy operation so the
        // other mutating commands (Download, Remove, Finalize) are
        // gated by CanMutateDrive while Format-Volume is running. Without
        // this, a user could kick off a download against a drive root that's
        // mid-erase and either fail against a disappearing volume or
        // repopulate the drive before SaveConfigAsync lands.
        SetModelOperationState(true, $"Formatting {root}...");

        // B3-Redux diagnostic sidecar — only opened when the --diag flag
        // was passed at launch. Duplicates every log line written during
        // the format flow into a text file on disk because the UI
        // LogListBox doesn't support free-text selection. Default runs
        // skip this entirely (clean log, no temp file).
        var diagPath = Path.Combine(Path.GetTempPath(), "freeai-format-diagnostic.log");
        StreamWriter? diagSink = null;
        if (_diagEnabled)
        {
            try
            {
                diagSink = new StreamWriter(diagPath, append: false)
                {
                    AutoFlush = true
                };
                diagSink.WriteLine($"# B3-Redux diagnostic log — started {DateTime.Now:O}");
            }
            catch (Exception ex)
            {
                AppendLog($"[diag] Could not open sidecar log at {diagPath}: {ex.Message}");
            }
        }

        void DiagLog(string line)
        {
            AppendLog(line);
            try { diagSink?.WriteLine(line); } catch { /* best-effort */ }
        }

        try
        {
            ProgressIsIndeterminate = true;

            var preLabel = _selectedDrive.VolumeLabel;
            if (_diagEnabled)
            {
                DiagLog("=== B3-Redux diagnostic snapshot (pre-format) ===");
                DiagLog($"Sidecar log file     : {diagPath}");
                DiagLog($"Selected root        : {root}");
                DiagLog($"Selected label (pre) : \"{preLabel}\"");
                DiagLog($"Requested label      : \"{_volumeLabel}\"");
                DiagLog($"IsFixed              : {_selectedDrive.IsFixed}");
                DiagLog($"IsElevated (at call) : {_elevationService.IsElevated()}");
                DiagLog($"PrepApp base dir     : {AppContext.BaseDirectory}");
                DiagLog($"PrepApp drive root   : {Path.GetPathRoot(AppContext.BaseDirectory)}");
            }
            AppendLog($"Formatting {root} as {fileSystem} (label: '{_volumeLabel}')...");

            await _driveService.FormatAsync(
                root,
                _volumeLabel,
                fileSystem,
                onOutput: DiagLog,
                verboseDiagnostics: _diagEnabled,
                ct: CancellationToken.None);

            StatusText = "Preparing drive structure...";
            _driveService.EnsureSsdStructure(root);

            var configPath = GetConfigPath(root);
            var config = new PortableConfig
            {
                PreparedAtUtc = DateTime.UtcNow,
                OllamaPort = 11434,
                PreferredCompute = "cpu"
            };
            await _modelService.SaveConfigAsync(configPath, config);

            AppendLog($"Drive formatted and structure created on {root}.");

            // Re-enumerate drives so the dropdown picks up the new volume
            // label and post-format free-bytes instead of stale metadata
            // captured before the format. Preserve selection by root path
            // so the user's choice doesn't jump to Drives[0].
            Drives = _driveService.GetCandidateDrives(_showFixedDrives);
            SelectedDrive = Drives.FirstOrDefault(d =>
                string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase))
                ?? (Drives.Count > 0 ? Drives[0] : null);

            if (_diagEnabled)
            {
                DiagLog("=== B3-Redux diagnostic snapshot (post-format) ===");
                DiagLog($"Enumerated drives    : {Drives.Count}");
                foreach (var d in Drives)
                {
                    DiagLog($"  {d.RootPath} label=\"{d.VolumeLabel}\" fixed={d.IsFixed}");
                }
                var selRoot = SelectedDrive?.RootPath ?? "(null)";
                var selLabel = SelectedDrive?.VolumeLabel ?? "(null)";
                DiagLog($"Selected after       : {selRoot}");
                DiagLog($"Selected label (post): \"{selLabel}\"");
                DiagLog($"Root letter match    : {string.Equals(selRoot, root, StringComparison.OrdinalIgnoreCase)}");
                DiagLog($"Label actually chgd  : {!string.Equals(preLabel, selLabel, StringComparison.Ordinal)}");
                DiagLog("=== end B3-Redux diagnostic ===");
                AppendLog($"[diag] Full diagnostic log saved to: {diagPath}");
            }

            await RefreshModelStatusesAsync();
            SetModelOperationState(false, "Drive prepared");
        }
        catch (Exception ex)
        {
            AppendLog($"Drive preparation failed: {ex.Message}");
            if (_diagEnabled)
            {
                DiagLog($"Exception type       : {ex.GetType().FullName}");
                DiagLog($"Stack trace          :{Environment.NewLine}{ex.StackTrace}");
                AppendLog($"[diag] Full diagnostic log saved to: {diagPath}");
            }
            _dialogService.ShowError(ex.Message, "Format failed");
            SetModelOperationState(false, "Prepare failed");
        }
        finally
        {
            ProgressIsIndeterminate = false;
            try { diagSink?.Dispose(); } catch { /* best-effort */ }
        }
    }

    private async Task FinalizeAsync()
    {
        if (_isModelOperationRunning)
        {
            AppendLog("Finalize is disabled while model operations are running.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!TryGetFinalizeProfile(out var selectedProfile))
        {
            return;
        }
        if (!EnsureWritable("Finalize SSD")) return;

        if (_selectedDrive.IsFixed)
        {
            if (!_dialogService.ConfirmFixedDrive(_selectedDrive.RootPath))
            {
                AppendLog("Finalize cancelled.");
                return;
            }
        }

        var root = _selectedDrive.RootPath;
        try
        {
            ProgressValue = 0;
            StatusText = "Preparing folders...";
            _driveService.EnsureSsdStructure(root);

            var configPath = GetConfigPath(root);
            var config = await _modelService.LoadConfigAsync(configPath);
            config.PreparedAtUtc = DateTime.UtcNow;
            config.OllamaPort = 11434;
            config.PreferredCompute = "cpu";
            config.IsEncrypted = false;
            config.EncryptionScheme = null;
            // MAC34: ensure NetworkApiKey is populated before any persist.
            // Pre-MAC34, this defaulted to "" with NetworkRequireApiKey=true,
            // so toggling Network Mode would 503 every request via the
            // RunnerLocalApiService fail-closed guard. Mirror the Mac
            // PrepApp's EncryptedConfigWriter.generateRandomApiKey behavior:
            // 32 bytes of OS RNG, hex-encoded, set once at first prep.
            // Existing keys are preserved (idempotent re-finalize).
            if (string.IsNullOrWhiteSpace(config.NetworkApiKey))
            {
                config.NetworkApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                    .ToLowerInvariant();
            }
            await _modelService.SaveConfigAsync(configPath, config);
            await RefreshModelStatusesAsync();

            var installedCount = config.Models.Count(m => m.Status == ModelInstallStatus.Installed);
            if (installedCount == 0)
            {
                _dialogService.ShowWarning(
                    "Cannot finalize SSD with zero installed models. Use Model Manager to pull at least one model first.",
                    "Finalize blocked");
                AppendLog("Finalize blocked: zero installed models.");
                StatusText = "Finalize blocked";
                return;
            }

            CheckMacArtifactAvailability();

            var targets = GetSelectedPrepTargets();
            if (targets == PrepTargets.None)
            {
                _dialogService.ShowWarning("Select at least one prep target (Windows and/or macOS).", "Finalize blocked");
                AppendLog("Finalize blocked: no prep target selected.");
                return;
            }

            if (targets.HasFlag(PrepTargets.Windows))
            {
                await _ollamaPackageService.EnsureOllamaReadyAsync(root, _ollamaUrl, AppendLog, null, CancellationToken.None);

                StatusText = "Staging offline prerequisites...";
                await _prereqService.StagePrerequisitesAsync(root, AppendLog, CancellationToken.None);

                StatusText = "Staging Windows runner payload...";
                await _artifactStagingService.StageRunnerAsync(root, AppendLog);

                if (InstallVrCompanion)
                {
                    if (CompanionHostPort < 1 || CompanionHostPort > 65535)
                    {
                        _dialogService.ShowWarning("Companion host port must be between 1 and 65535.", "Finalize blocked");
                        StatusText = "Finalize blocked";
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(CompanionHostAddress))
                    {
                        var hostType = Uri.CheckHostName(CompanionHostAddress.Trim());
                        if (hostType == UriHostNameType.Unknown)
                        {
                            _dialogService.ShowWarning("Companion host must be a dotted-quad IPv4 or a resolvable hostname.", "Finalize blocked");
                            StatusText = "Finalize blocked";
                            return;
                        }
                    }

                    StatusText = "Staging VR companion payload...";
                    await _artifactStagingService.StageCompanionAsync(root, AppendLog);

                    var companionDir = Path.Combine(root, "companion");
                    var companionConfig = new CompanionConfig
                    {
                        HostAddress = CompanionHostAddress,
                        HostPort = CompanionHostPort,
                        ApiKey = config.NetworkApiKey,
                        PttBinding = string.Empty,
                        AutoReconnect = true,
                        SchemaVersion = 1
                    };
                    companionConfig.Save(Path.Combine(companionDir, "companion-config.json"));

                    var readmeLines = new[]
                    {
                        "Free AI SSD VR Companion Quick Setup",
                        "1. Plug this SSD into your VR PC.",
                        "2. Open the companion folder and run FreeAiSsd.Companion.exe.",
                        "3. Verify HostAddress/HostPort in companion-config.json or Settings.",
                        "4. Hold your configured PTT binding while speaking in DCS.",
                        "5. Release PTT to send /api/voice/query to the host Runner.",
                        "6. AI response audio plays on this VR PC output device."
                    };
                    await File.WriteAllLinesAsync(Path.Combine(companionDir, "README-VR.txt"), readmeLines);
                    AppendLog("VR companion staged to SSD:/companion.");
                }
            }

            if (targets.HasFlag(PrepTargets.Mac))
            {
                if (!_artifactStagingService.AreMacArtifactsAvailable(out var macProblem))
                {
                    var message = macProblem ?? "macOS artifacts are unavailable.";
                    AppendLog($"Finalize blocked: {message}");
                    _dialogService.ShowWarning(message, "macOS prep unavailable");
                    StatusText = "Finalize blocked";
                    return;
                }

                StatusText = "Staging macOS Runner...";
                await _artifactStagingService.StageMacRunnerAsync(root, AppendLog, CancellationToken.None);
                StatusText = "Staging macOS Ollama runtime...";
                await _artifactStagingService.StageMacOllamaAsync(root, AppendLog, CancellationToken.None);
            }

            var readiness = await _readinessService.RunReadinessChecksAsync(root, AppendLog, CancellationToken.None);
            RefreshReadinessItems(readiness);
            if (readiness.Any(r => !r.Passed))
            {
                var failures = string.Join(Environment.NewLine, readiness.Where(r => !r.Passed).Select(r => $"- {r.Check}: {r.Result}"));
                _dialogService.ShowWarning(
                    $"Cannot finalize SSD yet. Missing or invalid items:{Environment.NewLine}{failures}",
                    "SSD readiness check failed");
                StatusText = "Finalize blocked (readiness failed)";
                return;
            }

            config.ActiveProfile = selectedProfile;
            ProfileDefaults.Apply(config, selectedProfile);

            if (_enableEncryption)
            {
                var passphrase = _dialogService.PromptForEncryptionPassword();
                if (passphrase is null)
                {
                    AppendLog("Finalize cancelled: encryption passphrase setup cancelled.");
                    StatusText = "Finalize blocked";
                    return;
                }

                config.IsEncrypted = true;
                config.EncryptionScheme = SsdEncryption.SchemeName;
                await _encryptionService.EnableConfigEncryptionAsync(root, config, passphrase);
                AppendLog("Drive encryption enabled. Runner will now require unlock before use.");
            }
            else
            {
                await _modelService.SaveConfigAsync(configPath, config);
            }

            ProgressValue = 100;
            StatusText = "Complete";
            AppendLog("SSD finalized successfully.");
        }
        catch (Exception ex)
        {
            StatusText = "Finalize failed";
            AppendLog($"Finalize failed: {ex.Message}");
        }
    }

    private async Task CheckPrereqUpdatesAsync()
    {
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Check prerequisite updates")) return;

        PrereqStatusText = "Prereqs: updating";
        try
        {
            await _prereqService.UpdatePrereqsOnlineAsync(_selectedDrive.RootPath, AppendLog, CancellationToken.None);
            PrereqStatusText = "Prereqs: up-to-date";
            AppendLog("Prereq update check complete.");
        }
        catch (Exception ex)
        {
            PrereqStatusText = "Prereqs: failed";
            AppendLog($"Prereq update check failed: {ex.Message}");
        }
    }

    private async Task CheckReadinessAsync()
    {
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Check SSD readiness")) return;

        var checks = await _readinessService.RunReadinessChecksAsync(_selectedDrive.RootPath, AppendLog, CancellationToken.None);
        RefreshReadinessItems(checks);

        var message = string.Join(Environment.NewLine, checks.Select(c => $"[{(c.Passed ? '✓' : '✗')}] {c.Check}: {c.Result}"));
        if (checks.All(c => c.Passed))
            _dialogService.ShowInfo(message, "SSD Readiness");
        else
            _dialogService.ShowWarning(message, "SSD Readiness");
    }

    public async Task RefreshModelStatusesAsync()
    {
        if (_selectedDrive is null)
        {
            // Even without a drive, surface the starter catalog so the
            // merged grid isn't empty on first launch. Sizing warnings
            // are "OK" placeholder — they require a drive root to compute.
            ModelRows.Clear();
            foreach (var row in BuildStarterOnlyRows(freeDiskGb: null, takenNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                ModelRows.Add(row);
            return;
        }

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);

        // Recover stale "Downloading" statuses left behind by a crash or forced exit.
        // If no model operation is currently running, any model still marked as
        // Downloading was interrupted and should be reset to NotInstalled.
        if (!_isModelOperationRunning)
        {
            var staleModels = config.Models.Where(m => m.Status == ModelInstallStatus.Downloading).ToList();
            if (staleModels.Count > 0)
            {
                foreach (var stale in staleModels)
                {
                    stale.Status = ModelInstallStatus.NotInstalled;
                    stale.Sha256 = null;
                    stale.SizeBytes = null;
                    stale.LastVerifiedUtc = null;
                    AppendLog($"Recovered stale download status for '{stale.Name}' → NotInstalled.");
                }
                await _modelService.SaveConfigAsync(configPath, config);
            }
        }

        var discovered = _modelService.DiscoverModelsOnDisk(Path.Combine(_selectedDrive.RootPath, SsdLayout.Models));
        var freeDiskGb = _driveService.GetFreeDiskSpaceGb(_selectedDrive.RootPath);

        var rows = BuildModelGridRows(config.Models, discovered, freeDiskGb);
        ModelRows.Clear();
        foreach (var row in rows)
            ModelRows.Add(row);
    }

    private List<ModelGridRow> BuildModelGridRows(
        IEnumerable<ModelConfigEntry> configModels,
        IReadOnlyCollection<string> discoveredOnDisk,
        int? freeDiskGb)
    {
        var rows = new List<ModelGridRow>();
        var configured = configModels.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var catalogByTag = _starterCatalog.ToDictionary(e => e.Tag, StringComparer.OrdinalIgnoreCase);

        foreach (var model in configured)
        {
            var onDisk = discoveredOnDisk.Contains(model.Name);
            var state = DetermineConfiguredState(model, onDisk);
            var warnings = _modelService.GetSizingWarnings(model.Name, freeDiskGb, _systemRamGb, _gpuVramGb);
            var (tier, bestAt) = LookupStarterMeta(catalogByTag, model.Name);
            rows.Add(new ModelGridRow(
                model.Name,
                state,
                "Config",
                warnings.Count == 0 ? "OK" : string.Join("; ", warnings),
                model.SizeBytes.HasValue ? FormatSize(model.SizeBytes.Value) : "—",
                string.IsNullOrWhiteSpace(model.Sha256) ? "—" : model.Sha256[..Math.Min(8, model.Sha256.Length)],
                model.LastVerifiedUtc.HasValue ? model.LastVerifiedUtc.Value.ToLocalTime().ToString("u") : "—",
                isOnDiskOnly: false,
                isPresentOnDrive: onDisk,
                tier: tier,
                bestAt: bestAt));
        }

        var configuredNames = new HashSet<string>(configured.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var discovered in discoveredOnDisk.Where(d => !configuredNames.Contains(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var warnings = _modelService.GetSizingWarnings(discovered, freeDiskGb, _systemRamGb, _gpuVramGb);
            var (tier, bestAt) = LookupStarterMeta(catalogByTag, discovered);
            rows.Add(new ModelGridRow(
                discovered,
                "On drive only",
                "Disk",
                warnings.Count == 0 ? "OK" : string.Join("; ", warnings),
                "—", "—", "—",
                isOnDiskOnly: true,
                isPresentOnDrive: true,
                tier: tier,
                bestAt: bestAt));
        }

        // Third pass: recommended starters that aren't in config or on-disk.
        // Lets the merged grid surface Small/Medium/Large groupings before
        // the user has downloaded anything. IsSelected default = false.
        var taken = new HashSet<string>(configuredNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in discoveredOnDisk) taken.Add(name);
        foreach (var starter in BuildStarterOnlyRows(freeDiskGb, taken))
            rows.Add(starter);

        return rows;
    }

    private IEnumerable<ModelGridRow> BuildStarterOnlyRows(int? freeDiskGb, HashSet<string> takenNames)
    {
        foreach (var entry in _starterCatalog
                     .Where(e => !takenNames.Contains(e.Tag))
                     .OrderBy(e => e.Tag, StringComparer.OrdinalIgnoreCase))
        {
            var warnings = _modelService.GetSizingWarnings(entry.Tag, freeDiskGb, _systemRamGb, _gpuVramGb);
            var row = new ModelGridRow(
                entry.Tag,
                "Not downloaded",
                "Recommended",
                warnings.Count == 0 ? "OK" : string.Join("; ", warnings),
                "—", "—", "—",
                isOnDiskOnly: false,
                isPresentOnDrive: false,
                tier: entry.SizeTier,
                bestAt: entry.BestAt);
            row.IsSelected = false;
            yield return row;
        }
    }

    private static (string tier, string bestAt) LookupStarterMeta(
        Dictionary<string, StarterCatalogEntry> catalogByTag, string name)
    {
        if (catalogByTag.TryGetValue(name, out var entry))
            return (entry.SizeTier, entry.BestAt);
        return ("Custom", string.Empty);
    }

    private void RefreshReadinessItems(List<ReadinessItem> checks)
    {
        ReadinessItems.Clear();
        foreach (var check in checks)
            ReadinessItems.Add(check);
    }

    private void SetModelOperationState(bool running, string? status = null)
    {
        IsModelOperationRunning = running;
        if (!string.IsNullOrWhiteSpace(status))
            StatusText = status;
    }

    private bool TryGetFinalizeProfile(out UserProfile selectedProfile)
    {
        if (_selectedProfile is UserProfile profile)
        {
            ProfileSelectionWarning = string.Empty;
            selectedProfile = profile;
            return true;
        }

        const string message =
            "Choose a Runner profile before finishing setup. Flight Sim enables DCS bindings, HOTAS push-to-talk, and voice defaults; General Assistant keeps the runtime chat-first.";
        ProfileSelectionWarning = message;
        _dialogService.ShowWarning(message, "Profile required");
        AppendLog("Finalize blocked: no profile selected.");
        StatusText = "Finalize blocked";
        selectedProfile = default;
        return false;
    }

    private void AppendLog(string message)
    {
        if (_uiSyncContext is null || SynchronizationContext.Current == _uiSyncContext)
        {
            LogLines.Add(message);
        }
        else
        {
            _uiSyncContext.Post(_ => LogLines.Add(message), null);
        }

        _logService.AppendLog(message);
    }

    /// <summary>
    /// MAC31: thread-safe setter for <see cref="PullProgressLine"/>.
    /// Mirrors <see cref="AppendLog"/>'s dispatch pattern because the
    /// onProgress lambda fires from <c>ModelOperations.Consume</c>'s
    /// stdout-drain thread, not the UI thread. WPF binding can usually
    /// route a string PropertyChanged across threads on its own, but
    /// going through the sync context keeps the entire VM consistent
    /// and avoids surprising the binding engine when the surrounding
    /// SetProperty triggers other reactive code.
    /// </summary>
    private void SetPullProgressLineSafe(string line)
    {
        if (_uiSyncContext is null || SynchronizationContext.Current == _uiSyncContext)
        {
            PullProgressLine = line;
        }
        else
        {
            _uiSyncContext.Post(_ => PullProgressLine = line, null);
        }
    }

    private void RaiseAllCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanMutateDrive));
        OnPropertyChanged(nameof(HasDriveSelected));
        AddModelCommand.RaiseCanExecuteChanged();
        AddOrphanToConfigCommand.RaiseCanExecuteChanged();
        DownloadCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        CancelOperationCommand.RaiseCanExecuteChanged();
        FormatPrepareCommand.RaiseCanExecuteChanged();
        FinalizeCommand.RaiseCanExecuteChanged();
        CheckPrereqUpdatesCommand.RaiseCanExecuteChanged();
        CheckReadinessCommand.RaiseCanExecuteChanged();
    }

    private static string GetConfigPath(string root) => Path.Combine(root, new PortableConfig().ConfigRelativePath);

    private static string DetermineConfiguredState(ModelConfigEntry model, bool onDisk)
    {
        return model.Status switch
        {
            ModelInstallStatus.Installed => "Downloaded",
            ModelInstallStatus.Downloading => "Downloading…",
            ModelInstallStatus.Failed => "Failed — retry",
            ModelInstallStatus.NotInstalled when !onDisk => "Not downloaded",
            _ => "Not downloaded"
        };
    }

    internal static string FormatSize(long sizeBytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = sizeBytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.##} {1}", size, units[unit]);
    }
}
