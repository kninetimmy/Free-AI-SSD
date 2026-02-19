using System.Collections.ObjectModel;
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

    private IReadOnlyList<DriveTarget> _drives = Array.Empty<DriveTarget>();
    private DriveTarget? _selectedDrive;
    private bool _showFixedDrives;
    private bool _isSelectedDriveEncrypted;
    private string _statusText = string.Empty;
    private double _progressValue;
    private bool _progressIsIndeterminate;
    private bool _isModelOperationRunning;
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

    public PrepViewModel(
        IDriveService driveService,
        IModelService modelService,
        IOllamaPackageService ollamaPackageService,
        IPrereqService prereqService,
        IArtifactStagingService artifactStagingService,
        IReadinessService readinessService,
        IEncryptionService encryptionService,
        IDialogService dialogService,
        ILogService logService)
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

        ModelRows = new ObservableCollection<ModelGridRow>();
        StarterModels = new ObservableCollection<StarterModelRow>();
        ReadinessItems = new ObservableCollection<ReadinessItem>();
        LogLines = new ObservableCollection<string>();

        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        AddModelCommand = new AsyncRelayCommand(AddModelAsync, () => CanMutateDrive && HasDriveSelected);
        AddStarterModelsCommand = new AsyncRelayCommand(AddStarterModelsAsync, () => CanMutateDrive && HasDriveSelected);
        ClearStarterSelectionCommand = new RelayCommand(ClearStarterSelection);
        AddOrphanToConfigCommand = new AsyncRelayCommand(AddOrphanToConfigAsync, () => CanMutateDrive && HasDriveSelected);
        PullInstallCommand = new AsyncRelayCommand(PullInstallAsync, () => CanMutateDrive);
        PullSelectedCommand = new AsyncRelayCommand(PullSelectedAsync, () => CanMutateDrive);
        VerifyCommand = new AsyncRelayCommand(VerifyAsync, () => CanMutateDrive);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => CanMutateDrive);
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

    public Action? OnPrepTargetsChanged { get; set; }

    public bool PrepareWindows
    {
        get => _prepareWindows;
        set
        {
            if (SetProperty(ref _prepareWindows, value))
                OnPrepTargetsChanged?.Invoke();
        }
    }

    public bool PrepareMac
    {
        get => _prepareMac;
        set
        {
            if (!_isMacPrepAvailable) value = false;
            if (SetProperty(ref _prepareMac, value))
                OnPrepTargetsChanged?.Invoke();
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
    public ObservableCollection<StarterModelRow> StarterModels { get; }
    public ObservableCollection<ReadinessItem> ReadinessItems { get; }
    public ObservableCollection<string> LogLines { get; }

    public RelayCommand RefreshDrivesCommand { get; }
    public AsyncRelayCommand AddModelCommand { get; }
    public AsyncRelayCommand AddStarterModelsCommand { get; }
    public RelayCommand ClearStarterSelectionCommand { get; }
    public AsyncRelayCommand AddOrphanToConfigCommand { get; }
    public AsyncRelayCommand PullInstallCommand { get; }
    public AsyncRelayCommand PullSelectedCommand { get; }
    public AsyncRelayCommand VerifyCommand { get; }
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
        if (_selectedDrive is null) return false;
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

    private async Task AddStarterModelsAsync()
    {
        var selectedRows = StarterModels.Where(r => r.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            AppendLog("Select one or more starter models first.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Add starter models")) return;

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        _driveService.EnsureSsdStructure(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);
        foreach (var row in selectedRows)
        {
            _modelService.UpsertModel(config.Models, row.Tag, ModelInstallStatus.NotInstalled);
            AppendLog($"Added starter model '{row.Tag}' to config.");
        }
        await _modelService.SaveConfigAsync(configPath, config);
        await RefreshModelStatusesAsync();
        ClearStarterSelection();
    }

    private void ClearStarterSelection()
    {
        foreach (var row in StarterModels)
            row.IsSelected = false;
    }

    private async Task AddOrphanToConfigAsync()
    {
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Add on-disk model to config")) return;

        var selectedOrphans = ModelRows.Where(r => r.IsOnDiskOnly).ToList();
        if (selectedOrphans.Count == 0)
        {
            AppendLog("Select one or more OnDiskOnly model rows to add to config.");
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

    public IReadOnlyList<ModelGridRow> SelectedModelRows { get; set; } = Array.Empty<ModelGridRow>();

    private async Task PullInstallAsync()
    {
        if (!EnsureWritable("Pull/install model")) return;

        var selected = SelectedModelRows.Where(r => !r.IsOnDiskOnly).Select(r => r.Name).Take(1).ToList();
        if (selected.Count == 0)
        {
            AppendLog("Select a model row to pull/install.");
            return;
        }

        if (!ConfirmSizingWarningsIfNeeded(selected)) return;
        await PullModelsAsync(selected);
    }

    private async Task PullSelectedAsync()
    {
        if (!EnsureWritable("Pull selected models")) return;

        var selected = SelectedModelRows
            .Where(r => !r.IsOnDiskOnly)
            .Select(r => r.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (selected.Count == 0)
        {
            AppendLog("Select one or more configured model rows for pull.");
            return;
        }

        if (!ConfirmSizingWarningsIfNeeded(selected)) return;
        await PullModelsAsync(selected);
    }

    private bool ConfirmSizingWarningsIfNeeded(IReadOnlyList<string> models)
    {
        if (_selectedDrive is null) return true;

        var warnings = _modelService.BuildPullSelectionWarnings(models, _selectedDrive.RootPath, _systemRamGb, _gpuVramGb);
        if (warnings.Count > 0)
        {
            if (!_dialogService.ConfirmSizingWarnings(warnings))
            {
                AppendLog("Pull cancelled after sizing warning.");
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
        if (!EnsureWritable("Pull model operation")) return;

        var root = _selectedDrive.RootPath;
        var configPath = GetConfigPath(root);
        _modelOperationCts = new CancellationTokenSource();
        SetModelOperationState(true, "Pulling...");
        ProgressIsIndeterminate = true;

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
            foreach (var model in models)
            {
                _modelOperationCts.Token.ThrowIfCancellationRequested();
                await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Downloading);
                StatusText = $"Pulling {model}...";
                AppendLog($"Pulling {model}...");

                try
                {
                    var result = await _modelService.PullModelAsync(
                        ollamaExe, modelsRoot, model, AppendLog, _modelOperationCts.Token);

                    await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Installed, result.Sha256, result.SizeBytes, DateTime.UtcNow);
                    AppendLog($"Installed {model} ({FormatSize(result.SizeBytes)}). Sha256 {result.Sha256[..8]}...");
                }
                catch (OperationCanceledException)
                {
                    await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.NotInstalled);
                    AppendLog($"Pull cancelled for {model}.");
                    throw;
                }
                catch (Exception ex)
                {
                    await _modelService.UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Failed);
                    AppendLog($"Failed to install {model}: {ex.Message}");
                }
            }
            StatusText = "Model pull complete";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Model operation cancelled";
        }
        finally
        {
            ProgressIsIndeterminate = false;
            SetModelOperationState(false, StatusText);
            _modelOperationCts?.Dispose();
            _modelOperationCts = null;
            await RefreshModelStatusesAsync();
        }
    }

    private async Task VerifyAsync()
    {
        if (_isModelOperationRunning)
        {
            AppendLog("Cannot verify while another model operation is running.");
            return;
        }
        if (_selectedDrive is null)
        {
            AppendLog("Select a target drive first.");
            return;
        }
        if (!EnsureWritable("Verify model")) return;

        var selected = SelectedModelRows.Where(r => !r.IsOnDiskOnly).Select(r => r.Name).Take(1).ToList();
        if (selected.Count == 0)
        {
            AppendLog("Select a configured model row to verify.");
            return;
        }

        var modelName = selected[0];
        var configPath = GetConfigPath(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);
        var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
        if (model is null || string.IsNullOrWhiteSpace(model.Sha256))
        {
            AppendLog($"Cannot verify {modelName}: no stored hash in config.");
            return;
        }

        SetModelOperationState(true, "Verifying model...");
        try
        {
            var modelsRoot = Path.Combine(_selectedDrive.RootPath, SsdLayout.Models);
            var ok = await _modelService.VerifyModelAsync(modelsRoot, modelName, model.Sha256, AppendLog, CancellationToken.None);
            if (!ok)
                await _modelService.UpdateModelStatusAsync(configPath, modelName, ModelInstallStatus.Failed);
            else
                await _modelService.UpdateModelStatusAsync(configPath, modelName, ModelInstallStatus.Installed, model.Sha256, model.SizeBytes, DateTime.UtcNow);
        }
        finally
        {
            SetModelOperationState(false, "Verify complete");
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

        var selectedRow = SelectedModelRows.FirstOrDefault();
        if (selectedRow is null)
        {
            AppendLog("Select a model row to remove.");
            return;
        }

        var choice = _dialogService.PromptRemoveModel(selectedRow.Name);
        if (choice == ModelRemoveChoice.Cancel)
        {
            AppendLog("Remove/Delete cancelled.");
            return;
        }

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);
        var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, selectedRow.Name, StringComparison.OrdinalIgnoreCase));

        if (choice == ModelRemoveChoice.ConfigOnly)
        {
            if (model is null)
            {
                AppendLog($"{selectedRow.Name} is already on-disk-only and not in config.");
                return;
            }
            model.Status = ModelInstallStatus.NotInstalled;
            model.Sha256 = null;
            model.SizeBytes = null;
            model.LastVerifiedUtc = null;
            await _modelService.SaveConfigAsync(configPath, config);
            await RefreshModelStatusesAsync();
            AppendLog($"{model.Name}: removed from config only (disk contents kept).");
            return;
        }

        SetModelOperationState(true, $"Deleting {selectedRow.Name} from disk...");
        try
        {
            var ollamaExe = await _ollamaPackageService.EnsureOllamaReadyAsync(
                _selectedDrive.RootPath, _ollamaUrl, AppendLog, null, CancellationToken.None);
            var modelsRoot = Path.Combine(_selectedDrive.RootPath, SsdLayout.Models);
            AppendLog($"Deleting {selectedRow.Name} from disk with ollama rm...");

            await _modelService.DeleteModelAsync(ollamaExe, modelsRoot, selectedRow.Name, AppendLog, CancellationToken.None);

            if (model is not null)
            {
                model.Status = ModelInstallStatus.NotInstalled;
                model.Sha256 = null;
                model.SizeBytes = null;
                model.LastVerifiedUtc = null;
                await _modelService.SaveConfigAsync(configPath, config);
            }
            AppendLog($"{selectedRow.Name}: deleted from disk.");
        }
        catch (Exception ex)
        {
            AppendLog($"Delete failed for {selectedRow.Name}: {ex.Message}");
            _dialogService.ShowError($"Failed to delete model from disk: {ex.Message}", "Delete failed");
        }
        finally
        {
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

        if (_selectedDrive.IsFixed)
        {
            if (!_dialogService.ConfirmFixedDrive(_selectedDrive.RootPath))
            {
                AppendLog("Format cancelled by user.");
                return;
            }
        }

        if (!_dialogService.ConfirmErase(_selectedDrive.RootPath,
            _driveService.GetFreeDiskSpaceGb(_selectedDrive.RootPath)?.ToString() ?? "unknown"))
        {
            AppendLog("Format cancelled by user.");
            return;
        }

        try
        {
            StatusText = "Preparing drive structure...";
            var root = _selectedDrive.RootPath;
            _driveService.EnsureSsdStructure(root);

            var configPath = GetConfigPath(root);
            var config = new PortableConfig
            {
                PreparedAtUtc = DateTime.UtcNow,
                OllamaPort = 11434,
                PreferredCompute = "cpu"
            };
            await _modelService.SaveConfigAsync(configPath, config);

            StatusText = "Drive prepared";
            AppendLog($"Drive structure created on {root}.");
            await RefreshModelStatusesAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Prepare failed";
            AppendLog($"Drive preparation failed: {ex.Message}");
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
                await _modelService.SaveConfigAsync(configPath, config);
                await _encryptionService.EnableConfigEncryptionAsync(root, configPath, passphrase);
                AppendLog("Drive encryption enabled. Runner will now require unlock before use.");
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
            ModelRows.Clear();
            return;
        }

        var configPath = GetConfigPath(_selectedDrive.RootPath);
        var config = await _modelService.LoadConfigAsync(configPath);
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

        foreach (var model in configured)
        {
            var onDisk = discoveredOnDisk.Contains(model.Name);
            var state = DetermineConfiguredState(model, onDisk);
            var warnings = _modelService.GetSizingWarnings(model.Name, freeDiskGb, _systemRamGb, _gpuVramGb);
            rows.Add(new ModelGridRow(
                model.Name,
                state,
                "Config",
                warnings.Count == 0 ? "OK" : string.Join("; ", warnings),
                model.SizeBytes.HasValue ? FormatSize(model.SizeBytes.Value) : "—",
                string.IsNullOrWhiteSpace(model.Sha256) ? "—" : model.Sha256[..Math.Min(8, model.Sha256.Length)],
                model.LastVerifiedUtc.HasValue ? model.LastVerifiedUtc.Value.ToLocalTime().ToString("u") : "—",
                false));
        }

        var configuredNames = new HashSet<string>(configured.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var discovered in discoveredOnDisk.Where(d => !configuredNames.Contains(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var warnings = _modelService.GetSizingWarnings(discovered, freeDiskGb, _systemRamGb, _gpuVramGb);
            rows.Add(new ModelGridRow(discovered, "OnDiskOnly", "Disk", warnings.Count == 0 ? "OK" : string.Join("; ", warnings), "—", "—", "—", true));
        }

        return rows;
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

    private void AppendLog(string message)
    {
        LogLines.Add(message);
        _logService.AppendLog(message);
    }

    private void RaiseAllCommandsCanExecuteChanged()
    {
        OnPropertyChanged(nameof(CanMutateDrive));
        OnPropertyChanged(nameof(HasDriveSelected));
        AddModelCommand.RaiseCanExecuteChanged();
        AddStarterModelsCommand.RaiseCanExecuteChanged();
        AddOrphanToConfigCommand.RaiseCanExecuteChanged();
        PullInstallCommand.RaiseCanExecuteChanged();
        PullSelectedCommand.RaiseCanExecuteChanged();
        VerifyCommand.RaiseCanExecuteChanged();
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
        if (model.Status == ModelInstallStatus.Installed) return "Ready";
        if ((model.Status == ModelInstallStatus.NotInstalled || model.Status == ModelInstallStatus.Failed) && !onDisk)
            return "ConfiguredNotDownloaded";
        return model.Status.ToString();
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
