using System.Windows.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Principal;
using FreeAiSsd.Shared;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Bitfield flags indicating which platforms the SSD should be prepared for.
/// Supports single or combined selection (Windows, macOS, or both).
/// </summary>
[Flags]
public enum PrepTargets
{
    None = 0,
    Windows = 1,
    Mac = 2
}

/// <summary>
/// Main window code-behind for the PrepApp — the SSD preparation tool.
/// This monolithic class manages the entire preparation workflow:
///
/// - Drive selection and formatting (NTFS via PowerShell)
/// - Model management: add, pull (download via Ollama), verify (SHA-256), remove
/// - Starter model catalog UI with hardware sizing warnings
/// - Prerequisite staging: bundles VC++ and .NET runtime installers
/// - Ollama package download with trust policy validation
/// - macOS artifact staging (Runner.app + Ollama universal binary)
/// - SSD readiness checks (files, models, config integrity)
/// - SSD finalization with optional AES-256-GCM encryption
/// - Encryption write-guard: prevents PrepApp writes to already-encrypted drives
///
/// Architecture note: This file contains ~1800 lines mixing UI state, I/O,
/// downloads, and business logic. A future refactoring to MVVM with a service
/// layer would improve testability and maintainability.
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    /// <summary>Handles resumable file downloads for Ollama packages and prerequisites.</summary>
    private readonly DownloadManager _downloadManager = new();
    /// <summary>Manages Ollama CLI interactions for model pull/verify/delete operations.</summary>
    private readonly ModelOperations _modelOperations = new();
    /// <summary>Cancellation source for the current model operation (pull/verify/delete).</summary>
    private CancellationTokenSource? _modelOperationCts;
    /// <summary>Guard flag preventing concurrent model operations.</summary>
    private bool _isModelOperationRunning;
    /// <summary>Cached system RAM in GB for model sizing warnings.</summary>
    private int? _systemRamGb;
    /// <summary>Cached GPU VRAM in GB for model sizing warnings.</summary>
    private int? _gpuVramGb;
    /// <summary>Persists the user's Windows/macOS platform selection between sessions.</summary>
    private readonly PrepTargetPreferenceStore _prepTargetPreferenceStore = new();
    /// <summary>Cached result of macOS artifact availability check.</summary>
    private MacArtifactAvailabilityResult _macArtifactAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
    /// <summary>Prevents re-entrant persistence when programmatically setting checkboxes.</summary>
    private bool _suppressPrepTargetPersistence;
    /// <summary>Ensures the macOS fallback dialog is shown at most once per session.</summary>
    private bool _macFallbackDialogShown;
    /// <summary>Backing data for the starter model catalog DataGrid.</summary>
    private readonly List<StarterModelRow> _starterModelRows = new();
    /// <summary>True if the selected drive has encryption enabled (blocks PrepApp writes).</summary>
    private bool _isSelectedDriveEncrypted;

    /// <summary>
    /// Initializes the PrepApp window: loads drives, starter model catalog,
    /// system hardware info, and checks macOS artifact availability.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        ApplyMacArtifactAvailability(forceDialogForPersistedMacPreference: true);
        LoadDrives();
        RefreshModelStatusGrid(Array.Empty<ModelConfigEntry>(), Array.Empty<string>());
        RefreshReadinessGrid(Array.Empty<ReadinessItem>());
        UpdateModelActionButtons();
        _systemRamGb = SystemResources.GetTotalSystemRamGb();
        _gpuVramGb = SystemResources.GetGpuVramGb();
        LoadStarterCatalog();
    }

    private void RefreshDrives_Click(object sender, System.Windows.RoutedEventArgs e) => LoadDrives();

    private void ShowFixedDrivesChanged(object sender, System.Windows.RoutedEventArgs e) => LoadDrives();

    /// <summary>
    /// Refreshes the drive combo box with candidate drives (removable + optionally fixed).
    /// Also refreshes encryption state, warnings, and model statuses for the newly selected drive.
    /// </summary>
    private void LoadDrives()
    {
        var includeFixed = ShowFixedDrivesCheckBox?.IsChecked == true;
        var drives = DriveInspector.GetCandidateDrives(includeFixed);
        DriveCombo.ItemsSource = drives;
        DriveCombo.SelectedIndex = drives.Count > 0 ? 0 : -1;
        RefreshSelectedDriveEncryptionState();
        UpdateWarning();
        UpdateModelActionButtons();
        RefreshStarterModelSizingWarnings();
        _ = RefreshModelStatusesForSelectedDriveAsync();
    }

    private void DriveCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RefreshSelectedDriveEncryptionState();
        UpdateWarning();
        UpdateModelActionButtons();
        RefreshStarterModelSizingWarnings();
        _ = RefreshModelStatusesForSelectedDriveAsync();
    }

    private PrepTargets GetSelectedPrepTargets()
    {
        var targets = PrepTargets.None;
        if (PrepareWindowsCheckBox?.IsChecked == true) targets |= PrepTargets.Windows;
        if (PrepareMacCheckBox?.IsChecked == true) targets |= PrepTargets.Mac;
        return targets;
    }


    private void PrepTargetSelectionChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_suppressPrepTargetPersistence)
        {
            return;
        }

        PersistPrepTargetSelection();
    }

    private void ApplyMacArtifactAvailability(bool forceDialogForPersistedMacPreference)
    {
        _macArtifactAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        var persistedTargets = _prepTargetPreferenceStore.Load();
        var fallbackApplied = false;

        if (!_macArtifactAvailability.MacArtifactsAvailable)
        {
            if (persistedTargets.HasFlag(PrepTargets.Mac))
            {
                fallbackApplied = true;
                persistedTargets = PrepTargets.Windows;
                _prepTargetPreferenceStore.Save(persistedTargets);
            }

            PrepareMacCheckBox.IsEnabled = false;
            PrepareMacCheckBox.IsChecked = false;
            MacPrepAvailabilityText.Text = _macArtifactAvailability.MacArtifactsProblem ?? string.Empty;
            MacPrepAvailabilityText.Visibility = System.Windows.Visibility.Visible;
        }
        else
        {
            PrepareMacCheckBox.IsEnabled = true;
            MacPrepAvailabilityText.Text = string.Empty;
            MacPrepAvailabilityText.Visibility = System.Windows.Visibility.Collapsed;
        }

        ApplyPrepTargetSelection(persistedTargets);

        if (fallbackApplied && forceDialogForPersistedMacPreference && !_macFallbackDialogShown)
        {
            _macFallbackDialogShown = true;
            var message = (_macArtifactAvailability.MacArtifactsProblem ?? "macOS preparation is unavailable.")
                + Environment.NewLine + Environment.NewLine
                + "Prep target has been reset to Windows.";
            System.Windows.MessageBox.Show(
                message,
                "macOS prep requires beta ZIP",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }
    }

    private void ApplyPrepTargetSelection(PrepTargets targets)
    {
        _suppressPrepTargetPersistence = true;
        try
        {
            if (!_macArtifactAvailability.MacArtifactsAvailable)
            {
                targets &= ~PrepTargets.Mac;
            }

            if (targets == PrepTargets.None)
            {
                targets = PrepTargets.Windows;
            }

            PrepareWindowsCheckBox.IsChecked = targets.HasFlag(PrepTargets.Windows);
            PrepareMacCheckBox.IsChecked = targets.HasFlag(PrepTargets.Mac);
        }
        finally
        {
            _suppressPrepTargetPersistence = false;
        }
    }

    private void PersistPrepTargetSelection()
    {
        _prepTargetPreferenceStore.Save(GetSelectedPrepTargets());
    }

    /// <summary>
    /// Adds a manually-entered model tag to the SSD config (does not download it yet).
    /// The user must subsequently click Pull/Install to download the model.
    /// </summary>
    private async void AddModel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var tag = (ModelTagText.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(tag))
        {
            AppendLog("Enter a model tag before adding.");
            return;
        }

        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Add model"))
        {
            return;
        }

        var configPath = GetConfigPath(drive.RootPath);
        SsdLayout.EnsureStructure(drive.RootPath);
        var config = await PortableConfig.LoadAsync(configPath);
        UpsertModel(config.Models, tag, ModelInstallStatus.NotInstalled);
        await config.SaveAsync(configPath);
        await RefreshModelStatusesForSelectedDriveAsync();
        ModelTagText.Text = string.Empty;
        AppendLog($"Added model '{tag}' to config.");
    }

    /// <summary>
    /// Loads the starter model catalog from disk or embedded fallback,
    /// populates the DataGrid grouped by size tier (Small/Medium/Large),
    /// and refreshes hardware sizing warnings for each model.
    /// </summary>
    private void LoadStarterCatalog()
    {
        var loadResult = StarterModelCatalogLoader.Load(AppContext.BaseDirectory);
        if (!string.IsNullOrWhiteSpace(loadResult.Warning))
        {
            StarterCatalogWarningText.Text = loadResult.Warning;
            StarterCatalogWarningText.Visibility = System.Windows.Visibility.Visible;
            AppendLog(loadResult.Warning);
        }
        else
        {
            StarterCatalogWarningText.Text = string.Empty;
            StarterCatalogWarningText.Visibility = System.Windows.Visibility.Collapsed;
        }

        var tierOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Small"] = 0,
            ["Medium"] = 1,
            ["Large"] = 2
        };

        _starterModelRows.Clear();
        foreach (var entry in loadResult.Catalog.Models
                     .OrderBy(m => tierOrder.TryGetValue(m.SizeTier, out var order) ? order : int.MaxValue)
                     .ThenBy(m => m.Tag, StringComparer.OrdinalIgnoreCase))
        {
            _starterModelRows.Add(new StarterModelRow(
                entry.Tag,
                entry.Params,
                entry.SizeTier,
                entry.Description,
                string.Join(", ", entry.UseCases),
                string.Empty));
        }

        var collectionView = new System.Windows.Data.ListCollectionView(_starterModelRows);
        collectionView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StarterModelRow.SizeTier)));
        StarterModelGrid.ItemsSource = collectionView;
        RefreshStarterModelSizingWarnings();
        UpdateStarterCatalogButtons();
    }

    private void RefreshStarterModelSizingWarnings()
    {
        var freeDiskGb = DriveCombo.SelectedItem is DriveTarget drive ? SystemResources.GetFreeDiskSpaceGb(drive.RootPath) : null;
        foreach (var row in _starterModelRows)
        {
            var warnings = GetSizingWarnings(row.Tag, freeDiskGb);
            row.SizingWarning = warnings.Count == 0 ? "OK" : string.Join("; ", warnings);
        }

        StarterModelGrid.Items.Refresh();
    }

    private async void AddSelectedStarterModels_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var selectedRows = _starterModelRows.Where(r => r.IsSelected).ToList();
        if (selectedRows.Count == 0)
        {
            AppendLog("Select one or more starter models first.");
            return;
        }

        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Add starter models"))
        {
            return;
        }

        var configPath = GetConfigPath(drive.RootPath);
        SsdLayout.EnsureStructure(drive.RootPath);
        var config = await PortableConfig.LoadAsync(configPath);

        foreach (var row in selectedRows)
        {
            UpsertModel(config.Models, row.Tag, ModelInstallStatus.NotInstalled);
            AppendLog($"Added starter model '{row.Tag}' to config.");
        }

        await config.SaveAsync(configPath);
        await RefreshModelStatusesForSelectedDriveAsync();
        ClearStarterSelection();
    }

    private void ClearStarterModelSelection_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ClearStarterSelection();
    }

    private void ClearStarterSelection()
    {
        foreach (var row in _starterModelRows)
        {
            row.IsSelected = false;
        }

        StarterModelGrid.Items.Refresh();
        UpdateStarterCatalogButtons();
    }

    private void StarterModelSelectionChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdateStarterCatalogButtons();
    }

    private void UpdateStarterCatalogButtons()
    {
        var hasSelection = _starterModelRows.Any(r => r.IsSelected);
        AddStarterModelsButton.IsEnabled = !_isModelOperationRunning && !_isSelectedDriveEncrypted && hasSelection;
        ClearStarterModelsSelectionButton.IsEnabled = hasSelection;
    }

    private async void AddOrphanToConfig_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Add on-disk model to config"))
        {
            return;
        }

        var selectedOrphans = GetSelectedModelRows().Where(r => r.IsOnDiskOnly).ToList();
        if (selectedOrphans.Count == 0)
        {
            AppendLog("Select one or more OnDiskOnly model rows to add to config.");
            return;
        }

        var configPath = GetConfigPath(drive.RootPath);
        var config = await PortableConfig.LoadAsync(configPath);
        foreach (var row in selectedOrphans)
        {
            UpsertModel(config.Models, row.Name, ModelInstallStatus.NotInstalled);
            AppendLog($"Added orphaned model '{row.Name}' to config.");
        }

        await config.SaveAsync(configPath);
        await RefreshModelStatusesForSelectedDriveAsync();
    }

    private async void PullInstall_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!EnsureSelectedDriveWritableForPrep("Pull/install model"))
        {
            return;
        }

        var selected = GetSelectedModelRows().Where(r => !r.IsOnDiskOnly).Select(r => r.Name).Take(1).ToList();
        if (selected.Count == 0)
        {
            AppendLog("Select a model row to pull/install.");
            return;
        }

        if (DriveCombo.SelectedItem is DriveTarget drive)
        {
            var pullWarnings = BuildPullSelectionWarnings(selected, drive.RootPath);
            if (pullWarnings.Count > 0)
            {
                var message = "Some selected models may run poorly on this machine or exceed available resources."
                    + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, pullWarnings.Select(w => $"- {w}"))
                    + Environment.NewLine + Environment.NewLine
                    + "Continue anyway?";

                var result = System.Windows.MessageBox.Show(
                    message,
                    "Model sizing warnings",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.No);

                if (result != System.Windows.MessageBoxResult.Yes)
                {
                    AppendLog("Pull selected cancelled after sizing warning.");
                    return;
                }
            }
        }

        await PullModelsForSelectedDriveAsync(selected);
    }

    private async void PullSelected_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!EnsureSelectedDriveWritableForPrep("Pull selected models"))
        {
            return;
        }

        var selected = GetSelectedModelRows().Where(r => !r.IsOnDiskOnly).Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (selected.Count == 0)
        {
            AppendLog("Select one or more configured model rows for pull.");
            return;
        }

        if (DriveCombo.SelectedItem is DriveTarget drive)
        {
            var pullWarnings = BuildPullSelectionWarnings(selected, drive.RootPath);
            if (pullWarnings.Count > 0)
            {
                var message = "Some selected models may run poorly on this machine or exceed available resources."
                    + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, pullWarnings.Select(w => $"- {w}"))
                    + Environment.NewLine + Environment.NewLine
                    + "Continue anyway?";

                var result = System.Windows.MessageBox.Show(
                    message,
                    "Model sizing warnings",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.No);

                if (result != System.Windows.MessageBoxResult.Yes)
                {
                    AppendLog("Pull selected cancelled after sizing warning.");
                    return;
                }
            }
        }

        await PullModelsForSelectedDriveAsync(selected);
    }

    /// <summary>
    /// Downloads one or more models from the Ollama registry to the SSD.
    /// Ensures Ollama is available (downloading if needed), then pulls each model
    /// sequentially with streaming progress updates. Updates model status in config
    /// after each successful pull, and handles cancellation gracefully.
    /// </summary>
    private async Task PullModelsForSelectedDriveAsync(IReadOnlyList<string> models)
    {
        if (_isModelOperationRunning)
        {
            AppendLog("A model operation is already running.");
            return;
        }

        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Pull model operation"))
        {
            return;
        }

        var logger = new SsdLogger(drive.RootPath, "prep");
        var configPath = GetConfigPath(drive.RootPath);

        _modelOperationCts = new CancellationTokenSource();
        SetModelOperationUiState(true, "Pulling...");
        Progress.IsIndeterminate = true;

        try
        {
            var ollamaExe = await EnsureOllamaReadyAsync(drive.RootPath, logger, _modelOperationCts.Token);
            var modelsRoot = Path.Combine(drive.RootPath, SsdLayout.Models);

            foreach (var model in models)
            {
                _modelOperationCts.Token.ThrowIfCancellationRequested();
                await UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Downloading);
                StatusText.Text = $"Pulling {model}...";
                AppendLog($"Pulling {model}...");
                logger.Info($"Pulling {model}...");

                try
                {
                    var result = await _modelOperations.PullModelAsync(
                        ollamaExe,
                        modelsRoot,
                        model,
                        line =>
                        {
                            AppendLog(line);
                            logger.Info(line);
                            Dispatcher.Invoke(() => StatusText.Text = ParseProgressLabel(line));
                        },
                        _modelOperationCts.Token);

                    await UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Installed, result.Sha256, result.SizeBytes, DateTime.UtcNow);
                    AppendLog($"Installed {model} ({FormatSize(result.SizeBytes)}). Sha256 {result.Sha256[..8]}...");
                    logger.Info($"Installed {model}.");
                }
                catch (OperationCanceledException)
                {
                    await UpdateModelStatusAsync(configPath, model, ModelInstallStatus.NotInstalled);
                    AppendLog($"Pull cancelled for {model}.");
                    logger.Info($"Pull cancelled for {model}.");
                    throw;
                }
                catch (Exception ex)
                {
                    await UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Failed);
                    AppendLog($"Failed to install {model}: {ex.Message}");
                    logger.Error(ex.ToString());
                }
            }

            StatusText.Text = "Model pull complete";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Model operation cancelled";
        }
        finally
        {
            Progress.IsIndeterminate = false;
            SetModelOperationUiState(false, StatusText.Text);
            _modelOperationCts?.Dispose();
            _modelOperationCts = null;
            await RefreshModelStatusesForSelectedDriveAsync();
        }
    }

    private async void Verify_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isModelOperationRunning)
        {
            AppendLog("Cannot verify while another model operation is running.");
            return;
        }

        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Verify model"))
        {
            return;
        }

        var selected = GetSelectedModelRows().Where(r => !r.IsOnDiskOnly).Select(r => r.Name).Take(1).ToList();
        if (selected.Count == 0)
        {
            AppendLog("Select a configured model row to verify.");
            return;
        }

        var modelName = selected[0];
        var configPath = GetConfigPath(drive.RootPath);
        var config = await PortableConfig.LoadAsync(configPath);
        var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
        if (model is null || string.IsNullOrWhiteSpace(model.Sha256))
        {
            AppendLog($"Cannot verify {modelName}: no stored hash in config.");
            return;
        }

        var logger = new SsdLogger(drive.RootPath, "prep");
        SetModelOperationUiState(true, "Verifying model...");

        try
        {
            var ok = await _modelOperations.VerifyModelAsync(Path.Combine(drive.RootPath, SsdLayout.Models), modelName, model.Sha256, line =>
            {
                AppendLog(line);
                logger.Info(line);
            }, CancellationToken.None);

            if (!ok)
            {
                await UpdateModelStatusAsync(configPath, modelName, ModelInstallStatus.Failed);
            }
            else
            {
                await UpdateModelStatusAsync(configPath, modelName, ModelInstallStatus.Installed, model.Sha256, model.SizeBytes, DateTime.UtcNow);
            }
        }
        finally
        {
            SetModelOperationUiState(false, "Verify complete");
        }
    }

    private async void Remove_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isModelOperationRunning)
        {
            AppendLog("Cannot remove while another model operation is running.");
            return;
        }

        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Remove/delete model"))
        {
            return;
        }

        var selectedRow = GetSelectedModelRows().Take(1).FirstOrDefault();
        if (selectedRow is null)
        {
            AppendLog("Select a model row to remove.");
            return;
        }

        var dialog = new RemoveModelDialog(selectedRow.Name) { Owner = this };
        var result = dialog.ShowDialog();
        if (result != true || dialog.Choice == ModelRemoveChoice.Cancel)
        {
            AppendLog("Remove/Delete cancelled.");
            return;
        }

        var logger = new SsdLogger(drive.RootPath, "prep");
        var configPath = GetConfigPath(drive.RootPath);
        var config = await PortableConfig.LoadAsync(configPath);
        var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, selectedRow.Name, StringComparison.OrdinalIgnoreCase));

        if (dialog.Choice == ModelRemoveChoice.ConfigOnly)
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
            await config.SaveAsync(configPath);
            await RefreshModelStatusesForSelectedDriveAsync();
            AppendLog($"{model.Name}: removed from config only (disk contents kept).");
            logger.Info($"{model.Name}: removed from config only.");
            return;
        }

        SetModelOperationUiState(true, $"Deleting {selectedRow.Name} from disk...");
        try
        {
            var ollamaExe = await EnsureOllamaReadyAsync(drive.RootPath, logger, CancellationToken.None);
            var modelsRoot = Path.Combine(drive.RootPath, SsdLayout.Models);
            AppendLog($"Deleting {selectedRow.Name} from disk with ollama rm...");
            logger.Info($"Deleting {selectedRow.Name} from disk with ollama rm...");

            await _modelOperations.DeleteModelAsync(ollamaExe, modelsRoot, selectedRow.Name, line =>
            {
                AppendLog(line);
                logger.Info(line);
            }, CancellationToken.None);

            if (model is not null)
            {
                model.Status = ModelInstallStatus.NotInstalled;
                model.Sha256 = null;
                model.SizeBytes = null;
                model.LastVerifiedUtc = null;
                await config.SaveAsync(configPath);
            }

            AppendLog($"{selectedRow.Name}: deleted from disk.");
            logger.Info($"{selectedRow.Name}: deleted from disk.");
        }
        catch (Exception ex)
        {
            AppendLog($"Delete failed for {selectedRow.Name}: {ex.Message}");
            logger.Error(ex.ToString());
            System.Windows.MessageBox.Show(
                $"Failed to delete model from disk: {ex.Message}",
                "Delete failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            SetModelOperationUiState(false);
            await RefreshModelStatusesForSelectedDriveAsync();
        }
    }

    /// <summary>
    /// Formats a removable drive as NTFS with a user-specified label, then creates
    /// the SSD directory structure. Requires Administrator privileges and explicit
    /// ERASE confirmation from the user. Only allowed for removable drives.
    /// </summary>
    private async void FormatPrepare_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Format and prepare drive"))
        {
            return;
        }

        if (!drive.IsRemovable)
        {
            System.Windows.MessageBox.Show("Formatting is only allowed for removable drives.", "Not allowed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (!IsRunningAsAdministrator())
        {
            System.Windows.MessageBox.Show("Formatting requires Administrator. Please re-run PrepApp as Administrator.", "Administrator required", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var label = string.IsNullOrWhiteSpace(VolumeLabelText.Text) ? "Portable AI" : VolumeLabelText.Text.Trim();
        var confirm = new EraseConfirmDialog(drive.RootPath, FormatSize(drive.TotalBytes)) { Owner = this };
        var accepted = confirm.ShowDialog() == true && confirm.IsConfirmed;
        if (!accepted)
        {
            AppendLog("Format & prepare cancelled (ERASE confirmation not provided).");
            return;
        }

        var logger = new SsdLogger(drive.RootPath, "prep");
        try
        {
            SetModelOperationUiState(true, "Formatting drive...");
            var driveLetter = drive.RootPath.TrimEnd('\\').TrimEnd(':')[0];
            var command = $"Format-Volume -DriveLetter {driveLetter} -FileSystem NTFS -NewFileSystemLabel '{label.Replace("'", "''")}' -Force";
            AppendLog($"Formatting {drive.RootPath} as NTFS with label '{label}'...");
            logger.Info($"Formatting drive {drive.RootPath}.");

            var formatResult = await RunPowerShellAsync(command);
            foreach (var line in formatResult)
            {
                AppendLog(line);
                logger.Info(line);
            }

            SsdLayout.EnsureStructure(drive.RootPath);
            AppendLog("Drive structure prepared.");
            logger.Info("Drive structure prepared after format.");
            StatusText.Text = "Format & prepare complete";
        }
        catch (Exception ex)
        {
            AppendLog($"Format & prepare failed: {ex.Message}");
            logger.Error(ex.ToString());
            StatusText.Text = "Format & prepare failed";
        }
        finally
        {
            SetModelOperationUiState(false);
            LoadDrives();
        }
    }

    private void CancelOperation_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _modelOperationCts?.Cancel();
        AppendLog("Cancellation requested for current model operation.");
    }

    /// <summary>
    /// Finalizes the SSD: stages all artifacts (Ollama, Runner, prerequisites, macOS),
    /// runs readiness checks, and optionally enables AES-256-GCM config encryption.
    /// This is the final step before handing the SSD to an end user.
    ///
    /// Finalization steps:
    /// 1. Ensure directory structure exists.
    /// 2. Save config with preparation timestamp.
    /// 3. Verify at least one model is installed.
    /// 4. Stage Windows artifacts (Ollama, prereqs, Runner) if Windows target selected.
    /// 5. Stage macOS artifacts (Runner.app, Ollama binary) if macOS target selected.
    /// 6. Run readiness checks (all must pass to proceed).
    /// 7. Optionally encrypt the config with user's password.
    /// </summary>
    private async void Finalize_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_isModelOperationRunning)
        {
            AppendLog("Finalize is disabled while model operations are running.");
            return;
        }

        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Finalize SSD"))
        {
            return;
        }

        if (!ConfirmDriveSelection(drive))
        {
            AppendLog("Finalize cancelled.");
            return;
        }

        var root = drive.RootPath;
        var logger = new SsdLogger(root, "prep");
        var enableEncryption = EnableDriveEncryptionCheckBox?.IsChecked == true;
        try
        {
            Progress.Value = 0;
            StatusText.Text = "Preparing folders...";
            SsdLayout.EnsureStructure(root);
            logger.Info($"Preparing SSD at {root}");

            var configPath = GetConfigPath(root);
            var config = await PortableConfig.LoadAsync(configPath);
            config.PreparedAtUtc = DateTime.UtcNow;
            config.OllamaPort = 11434;
            config.PreferredCompute = "cpu";
            config.IsEncrypted = false;
            config.EncryptionScheme = null;
            await config.SaveAsync(configPath);
            await RefreshModelStatusesForSelectedDriveAsync();

            var installedCount = config.Models.Count(m => m.Status == ModelInstallStatus.Installed);
            if (installedCount == 0)
            {
                System.Windows.MessageBox.Show(
                    "Cannot finalize SSD with zero installed models. Use Model Manager to pull at least one model first.",
                    "Finalize blocked",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                AppendLog("Finalize blocked: zero installed models.");
                StatusText.Text = "Finalize blocked";
                return;
            }

            _macArtifactAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
            if (!_macArtifactAvailability.MacArtifactsAvailable && PrepareMacCheckBox.IsEnabled)
            {
                ApplyMacArtifactAvailability(forceDialogForPersistedMacPreference: false);
            }

            var targets = GetSelectedPrepTargets();
            if (targets == PrepTargets.None)
            {
                System.Windows.MessageBox.Show("Select at least one prep target (Windows and/or macOS).", "Finalize blocked", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                AppendLog("Finalize blocked: no prep target selected.");
                return;
            }

            if (targets.HasFlag(PrepTargets.Windows))
            {
                await EnsureOllamaReadyAsync(root, logger, CancellationToken.None);

                StatusText.Text = "Staging offline prerequisites...";
                await StagePrerequisitesAsync(root, logger, CancellationToken.None);

                StatusText.Text = "Staging Windows runner payload...";
                await StageRunnerAsync(root, logger);
            }

            if (targets.HasFlag(PrepTargets.Mac))
            {
                var macAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
                if (!macAvailability.MacArtifactsAvailable)
                {
                    var message = macAvailability.MacArtifactsProblem ?? "macOS artifacts are unavailable.";
                    AppendLog($"Finalize blocked: {message}");
                    logger.Error($"Finalize blocked: {message}");
                    System.Windows.MessageBox.Show(
                        message,
                        "macOS prep unavailable",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    StatusText.Text = "Finalize blocked";
                    return;
                }

                StatusText.Text = "Staging macOS Runner...";
                await StageMacRunnerAsync(root, logger, CancellationToken.None);
                StatusText.Text = "Staging macOS Ollama runtime...";
                await StageMacOllamaAsync(root, logger, CancellationToken.None);
            }

            var readiness = await RunReadinessChecksAsync(root, logger);
            RefreshReadinessGrid(readiness);
            if (readiness.Any(r => !r.Passed))
            {
                var failures = string.Join(Environment.NewLine, readiness.Where(r => !r.Passed).Select(r => $"- {r.Check}: {r.Result}"));
                System.Windows.MessageBox.Show(
                    $"Cannot finalize SSD yet. Missing or invalid items:{Environment.NewLine}{failures}",
                    "SSD readiness check failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                StatusText.Text = "Finalize blocked (readiness failed)";
                return;
            }

            if (enableEncryption)
            {
                var passphrase = PromptForEncryptionPassword();
                if (passphrase is null)
                {
                    AppendLog("Finalize cancelled: encryption passphrase setup cancelled.");
                    StatusText.Text = "Finalize blocked";
                    return;
                }

                config.IsEncrypted = true;
                config.EncryptionScheme = SsdEncryption.SchemeName;
                await config.SaveAsync(configPath);
                await SsdEncryption.EnableConfigEncryptionAsync(root, configPath, passphrase);
                AppendLog("Drive encryption enabled. Runner will now require unlock before use.");
                logger.Info("Drive encryption enabled.");
            }

            Progress.Value = 100;
            StatusText.Text = "Complete";
            AppendLog("SSD finalized successfully.");
            logger.Info("SSD finalized successfully.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Finalize failed";
            AppendLog($"Finalize failed: {ex.Message}");
            logger.Error(ex.ToString());
        }
    }

    private bool ConfirmDriveSelection(DriveTarget drive)
    {
        if (!drive.IsFixed)
        {
            return true;
        }

        var firstConfirm = System.Windows.MessageBox.Show(
            $"You selected a fixed drive: {drive.RootPath}\n\nThis can overwrite or modify files on that drive. Continue?",
            "Advanced warning",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (firstConfirm != System.Windows.MessageBoxResult.Yes)
        {
            return false;
        }

        var secondConfirm = System.Windows.MessageBox.Show(
            "Final confirmation: this action may modify important files.\n\nClick Yes only if you fully understand the risk.",
            "Confirm fixed drive selection",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Exclamation,
            System.Windows.MessageBoxResult.No);

        return secondConfirm == System.Windows.MessageBoxResult.Yes;
    }

    /// <summary>
    /// Ensures the Ollama binary is available on the SSD. If already present and
    /// trust-attested, returns the path. Otherwise downloads the ZIP from the
    /// trusted URL, validates its SHA-256 digest, extracts it, and writes a
    /// trust attestation file for future runs.
    /// </summary>
    private async Task<string> EnsureOllamaReadyAsync(string root, SsdLogger logger, CancellationToken ct)
    {
        SsdLayout.EnsureStructure(root);
        var sourceValidation = OllamaPackageTrustPolicy.ValidatePackageSource(OllamaUrlText.Text);
        if (!sourceValidation.IsTrusted || sourceValidation.Metadata is null)
        {
            throw new InvalidOperationException(sourceValidation.Message);
        }

        var ollamaZipPath = Path.Combine(root, SsdLayout.Cache, "ollama-windows-amd64.zip");
        var ollamaDir = Path.Combine(root, SsdLayout.Ollama);

        var ollamaExe = ResolveOllamaExe(ollamaDir);
        if (ollamaExe is not null)
        {
            var executionGate = OllamaPackageTrustPolicy.ValidateExecutionAttestation(root, sourceValidation.Metadata.Url);
            if (!executionGate.IsTrusted)
            {
                throw new InvalidOperationException(executionGate.Message);
            }

            return ollamaExe;
        }

        StatusText.Text = "Downloading Ollama package...";
        await _downloadManager.DownloadFileWithResumeAsync(
            new DownloadRequest(sourceValidation.Metadata.Url, ollamaZipPath),
            new Progress<DownloadProgress>(p =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = p.Percent;
                StatusText.Text = $"Downloading Ollama {p.Percent:F1}%";
            }),
            ct);

        var digestValidation = OllamaPackageTrustPolicy.ValidateDownloadedPackage(ollamaZipPath, sourceValidation.Metadata);
        if (!digestValidation.IsTrusted)
        {
            throw new InvalidOperationException(digestValidation.Message);
        }

        ExtractOllamaZip(ollamaZipPath, ollamaDir);
        OllamaPackageTrustPolicy.WriteTrustAttestation(root, sourceValidation.Metadata);
        logger.Info("Ollama package staged.");

        return ResolveOllamaExe(ollamaDir) ?? throw new FileNotFoundException($"Unable to locate ollama.exe under {ollamaDir}");
    }

    private static void ExtractOllamaZip(string zipPath, string destination)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"Ollama ZIP not found at {zipPath}");
        }

        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(zipPath, destination, overwriteFiles: true);
    }

    private static string? ResolveOllamaExe(string ollamaDir)
    {
        if (!Directory.Exists(ollamaDir))
        {
            return null;
        }

        return Directory.EnumerateFiles(ollamaDir, "ollama.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string GetConfigPath(string root) => Path.Combine(root, new PortableConfig().ConfigRelativePath);

    private string? PromptForEncryptionPassword()
    {
        var dialog = new EncryptionSetupDialog { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private static void UpsertModel(List<ModelConfigEntry> models, string name, ModelInstallStatus status)
    {
        var model = models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            models.Add(new ModelConfigEntry
            {
                Name = name,
                Status = status
            });
            return;
        }

        model.Status = status;
    }

    private async Task UpdateModelStatusAsync(string configPath, string modelName, ModelInstallStatus status, string? sha256 = null, long? sizeBytes = null, DateTime? lastVerifiedUtc = null)
    {
        var config = await PortableConfig.LoadAsync(configPath);
        var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            model = new ModelConfigEntry { Name = modelName };
            config.Models.Add(model);
        }

        model.Status = status;
        model.Sha256 = sha256;
        model.SizeBytes = sizeBytes;
        model.LastVerifiedUtc = lastVerifiedUtc;

        await config.SaveAsync(configPath);
    }


    /// <summary>
    /// Copies bundled prerequisite installers (VC++ runtime, .NET runtime) to the SSD,
    /// writes a manifest with SHA-256 hashes, then optionally downloads updated versions
    /// from official URLs. If bundled files are missing or corrupt, offers to re-download.
    /// </summary>
    private async Task StagePrerequisitesAsync(string root, SsdLogger logger, CancellationToken ct)
    {
        var ssdPrereqDir = Path.Combine(root, SsdLayout.Prereqs);
        Directory.CreateDirectory(ssdPrereqDir);

        var bundledPrereqDir = Path.Combine(AppContext.BaseDirectory, SsdLayout.Prereqs);
        if (!Directory.Exists(bundledPrereqDir))
        {
            throw new DirectoryNotFoundException($"Bundled prerequisites folder is missing: {bundledPrereqDir}");
        }

        var bundledManifestPath = Path.Combine(bundledPrereqDir, PrereqCatalog.ManifestFileName);
        var ssdManifestPath = PrereqCatalog.GetManifestPath(root);

        var manifest = File.Exists(bundledManifestPath)
            ? PrereqManifest.Load(bundledManifestPath)
            : new PrereqManifest();

        foreach (var definition in PrereqCatalog.Tier1)
        {
            var sourcePath = Path.Combine(bundledPrereqDir, definition.TargetFileName);
            var targetPath = Path.Combine(ssdPrereqDir, definition.TargetFileName);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Bundled installer is missing: {sourcePath}");
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
            logger.Info($"Copied bundled prerequisite: {definition.DisplayName}");
            AppendLog($"Prereqs: bundled {definition.DisplayName}");

            var entry = manifest.Prerequisites.FirstOrDefault(p => string.Equals(p.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = PrereqCatalog.CreateManifestEntry(definition, DownloadManager.ComputeSha256(targetPath), new FileInfo(targetPath).Length);
                manifest.Prerequisites.Add(entry);
            }
        }

        await manifest.SaveAsync(ssdManifestPath);
        PrereqStatusText.Text = "Prereqs: bundled";
        AppendLog($"Wrote prerequisite manifest: {ssdManifestPath}");

        var bundleIssues = PrereqInstallValidator.ValidateBundleHealth(ssdPrereqDir, manifest);
        if (bundleIssues.Count > 0)
        {
            foreach (var issue in bundleIssues)
            {
                AppendLog($"Prereq bundle issue: {issue}");
                logger.Error($"Prereq bundle issue: {issue}");
            }

            var fixNow = System.Windows.MessageBox.Show(
                "Prerequisite bundle is missing or inconsistent. Re-download prerequisites now (online) and rewrite manifest?",
                "Prerequisite bundle verification",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.Yes);

            if (fixNow == System.Windows.MessageBoxResult.Yes)
            {
                await UpdatePrereqsOnlineAsync(ssdPrereqDir, manifest, logger, ct);
                await manifest.SaveAsync(ssdManifestPath);
                bundleIssues = PrereqInstallValidator.ValidateBundleHealth(ssdPrereqDir, manifest);
            }
            else
            {
                AppendLog("Continuing with warning: offline prerequisite install may fail until prereqs are refreshed.");
            }
        }

        try
        {
            PrereqStatusText.Text = "Prereqs: updating";
            await UpdatePrereqsOnlineAsync(ssdPrereqDir, manifest, logger, ct);
            await manifest.SaveAsync(ssdManifestPath);
            PrereqStatusText.Text = "Prereqs: up-to-date";
        }
        catch (Exception ex)
        {
            PrereqStatusText.Text = "Prereqs: warning";
            AppendLog($"Prereq update check failed, using bundled installers: {ex.Message}");
            logger.Error($"Prereq update check failed: {ex}");
        }
    }

    private async Task UpdatePrereqsOnlineAsync(string prereqDir, PrereqManifest manifest, SsdLogger logger, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        foreach (var definition in PrereqCatalog.Tier1)
        {
            var destinationPath = Path.Combine(prereqDir, definition.TargetFileName);
            var tempPath = destinationPath + ".download";

            AppendLog($"Checking prerequisite update: {definition.DisplayName}");
            using var response = await client.GetAsync(definition.SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, ct);
            }

            var downloadedSha = DownloadManager.ComputeSha256(tempPath);
            var existingSha = File.Exists(destinationPath) ? DownloadManager.ComputeSha256(destinationPath) : string.Empty;

            if (string.Equals(downloadedSha, existingSha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                AppendLog($"Prereq already up-to-date: {definition.DisplayName}");
            }
            else
            {
                File.Move(tempPath, destinationPath, overwrite: true);
                AppendLog($"Updated prerequisite: {definition.DisplayName}");
                logger.Info($"Updated prerequisite: {definition.DisplayName}");
            }

            var size = new FileInfo(destinationPath).Length;
            var existingEntry = manifest.Prerequisites.FirstOrDefault(p => string.Equals(p.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            var updated = PrereqCatalog.CreateManifestEntry(definition, DownloadManager.ComputeSha256(destinationPath), size);

            if (existingEntry is null)
            {
                manifest.Prerequisites.Add(updated);
            }
            else
            {
                existingEntry.DisplayName = updated.DisplayName;
                existingEntry.Filename = updated.Filename;
                existingEntry.SourceUrl = updated.SourceUrl;
                existingEntry.DownloadedAtUtc = updated.DownloadedAtUtc;
                existingEntry.Sha256 = updated.Sha256;
                existingEntry.SizeBytes = updated.SizeBytes;
                existingEntry.SilentArgs = updated.SilentArgs;
                existingEntry.RequiresAdmin = updated.RequiresAdmin;
                existingEntry.IsOptional = updated.IsOptional;
            }
        }
    }

    private async Task StageRunnerAsync(string ssdRoot, SsdLogger logger)
    {
        var sourceRunnerDir = ResolveRunnerPublishDirectory();
        var targetRunnerDir = Path.Combine(ssdRoot, SsdLayout.Runner);
        Directory.CreateDirectory(targetRunnerDir);

        if (sourceRunnerDir is null)
        {
            var hint = "Runner publish folder not found. Re-download the ZIP and ensure runner-publish is next to FreeAiSsd.PrepApp.exe, or run ./build.ps1 to stage runner artifacts for local development.";
            StatusText.Text = "Finalize failed: runner-publish missing";
            AppendLog(hint);
            logger.Error(hint);
            throw new DirectoryNotFoundException(hint);
        }

        logger.Info($"Using runner payload from: {sourceRunnerDir}");

        foreach (var file in Directory.EnumerateFiles(sourceRunnerDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRunnerDir, file);
            var destination = Path.Combine(targetRunnerDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        await Task.CompletedTask;
        logger.Info("Runner artifacts staged.");
    }

    private async Task StageMacRunnerAsync(string ssdRoot, SsdLogger logger, CancellationToken ct)
    {
        var macAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        if (!macAvailability.MacArtifactsAvailable)
        {
            var message = macAvailability.MacArtifactsProblem ?? "macOS artifacts are unavailable.";
            logger.Error($"Skipped macOS runner staging: {message}");
            AppendLog($"Skipped macOS runner staging: {message}");
            System.Windows.MessageBox.Show(
                message,
                "macOS prep unavailable",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var sourceRunnerZip = Path.Combine(AppContext.BaseDirectory, "mac", "Runner.app.zip");
        var macRoot = Path.Combine(ssdRoot, SsdLayout.Mac);
        Directory.CreateDirectory(macRoot);
        var targetZip = Path.Combine(macRoot, "Runner.app.zip");
        File.Copy(sourceRunnerZip, targetZip, overwrite: true);

        var extractedRunner = Path.Combine(macRoot, "Runner.app");
        if (Directory.Exists(extractedRunner))
        {
            Directory.Delete(extractedRunner, recursive: true);
        }

        ZipFile.ExtractToDirectory(targetZip, macRoot, overwriteFiles: true);
        logger.Info("Staged macOS Runner.app and archive.");
        await Task.CompletedTask;
    }

    private async Task StageMacOllamaAsync(string ssdRoot, SsdLogger logger, CancellationToken ct)
    {
        var macAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        if (!macAvailability.MacArtifactsAvailable)
        {
            var message = macAvailability.MacArtifactsProblem ?? "macOS artifacts are unavailable.";
            logger.Error($"Skipped macOS Ollama staging: {message}");
            AppendLog($"Skipped macOS Ollama staging: {message}");
            System.Windows.MessageBox.Show(
                message,
                "macOS prep unavailable",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var bundledArchive = Path.Combine(AppContext.BaseDirectory, "mac", "tools", "ollama", "ollama-darwin.zip");
        var cacheArchive = Path.Combine(ssdRoot, SsdLayout.Cache, "ollama-darwin.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheArchive)!);
        File.Copy(bundledArchive, cacheArchive, overwrite: true);
        var actualSha = DownloadManager.ComputeSha256(cacheArchive);

        var ollamaDir = Path.Combine(ssdRoot, SsdLayout.MacOllama);
        if (Directory.Exists(ollamaDir))
        {
            Directory.Delete(ollamaDir, recursive: true);
        }

        Directory.CreateDirectory(ollamaDir);
        ZipFile.ExtractToDirectory(cacheArchive, ollamaDir, overwriteFiles: true);

        var cliPath = Directory.EnumerateFiles(ollamaDir, "ollama", SearchOption.AllDirectories).FirstOrDefault();
        if (cliPath is null)
        {
            throw new FileNotFoundException("Could not locate macOS ollama binary after extraction.");
        }

        var finalCliPath = Path.Combine(ollamaDir, "ollama");
        File.Copy(cliPath, finalCliPath, overwrite: true);

        var sourceManifest = Path.Combine(AppContext.BaseDirectory, "mac", "tools", "ollama", "mac-tools-manifest.json");
        if (File.Exists(sourceManifest))
        {
            File.Copy(sourceManifest, Path.Combine(ollamaDir, "mac-tools-manifest.json"), overwrite: true);
        }

        var manifest = JsonSerializer.Serialize(new
        {
            id = MacToolCatalog.Ollama.Id,
            sourceUrl = MacToolCatalog.Ollama.SourceUrl,
            archive = MacToolCatalog.Ollama.ArchiveFileName,
            sha256 = actualSha,
            downloadedAtUtc = DateTime.UtcNow.ToString("O")
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(MacToolCatalog.GetManifestPath(ssdRoot), manifest, ct);
        logger.Info("Staged macOS Ollama runtime.");
    }

    private static string? ResolveRunnerPublishDirectory()
    {
        var baseDirCandidate = Path.Combine(AppContext.BaseDirectory, "runner-publish");
        if (DirectoryContainsRunner(baseDirCandidate))
        {
            return baseDirCandidate;
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
        {
            return null;
        }

        var buildConfigurations = new[] { "Release", "Debug" };
        foreach (var configuration in buildConfigurations)
        {
            var candidate = Path.Combine(repoRoot, "prep-app", "bin", configuration, "net8.0-windows", "runner-publish");
            if (DirectoryContainsRunner(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FreeAiSsd.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static bool DirectoryContainsRunner(string path)
    {
        return Directory.Exists(path) && File.Exists(Path.Combine(path, "FreeAiSsd.Runner.exe"));
    }

    private async Task RefreshModelStatusesForSelectedDriveAsync()
    {
        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            RefreshModelStatusGrid(Array.Empty<ModelConfigEntry>(), Array.Empty<string>());
            return;
        }

        var configPath = GetConfigPath(drive.RootPath);
        var config = await PortableConfig.LoadAsync(configPath);
        var discovered = ModelOperations.DiscoverModelsOnDisk(Path.Combine(drive.RootPath, SsdLayout.Models));
        RefreshModelStatusGrid(config.Models, discovered);
    }


    private async void CheckPrereqUpdates_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Check prerequisite updates"))
        {
            return;
        }

        var logger = new SsdLogger(drive.RootPath, "prep");
        var manifestPath = PrereqCatalog.GetManifestPath(drive.RootPath);
        var manifest = PrereqManifest.Load(manifestPath);
        PrereqStatusText.Text = "Prereqs: updating";

        try
        {
            await UpdatePrereqsOnlineAsync(Path.Combine(drive.RootPath, SsdLayout.Prereqs), manifest, logger, CancellationToken.None);
            await manifest.SaveAsync(manifestPath);
            PrereqStatusText.Text = "Prereqs: up-to-date";
            AppendLog("Prereq update check complete.");
        }
        catch (Exception ex)
        {
            PrereqStatusText.Text = "Prereqs: failed";
            AppendLog($"Prereq update check failed: {ex.Message}");
            logger.Error(ex.ToString());
        }
    }

    private async void CheckReadiness_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
            return;
        }

        if (!EnsureSelectedDriveWritableForPrep("Check SSD readiness"))
        {
            return;
        }

        var logger = new SsdLogger(drive.RootPath, "prep");
        var checks = await RunReadinessChecksAsync(drive.RootPath, logger);
        RefreshReadinessGrid(checks);

        var message = string.Join(Environment.NewLine, checks.Select(c => $"[{(c.Passed ? '✓' : '✗')}] {c.Check}: {c.Result}"));
        System.Windows.MessageBox.Show(
            message,
            "SSD Readiness",
            System.Windows.MessageBoxButton.OK,
            checks.All(c => c.Passed) ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
    }

    /// <summary>
    /// Runs comprehensive readiness checks on the SSD before finalization.
    /// Validates: Runner executables, config integrity, Ollama binaries,
    /// models directory, prerequisite bundle health, and model integrity
    /// (SHA-256 verification of all installed model blobs).
    /// </summary>
    private async Task<List<ReadinessItem>> RunReadinessChecksAsync(string root, SsdLogger logger)
    {
        var checks = new List<ReadinessItem>();

        var runnerDir = Path.Combine(root, SsdLayout.Runner);
        var runnerExe = Path.Combine(runnerDir, "FreeAiSsd.Runner.exe");
        checks.Add(File.Exists(runnerExe)
            ? ReadinessItem.Pass("Windows runner files present")
            : ReadinessItem.Warn("Windows runner files present", "Runner executable not found in SSD/windows/runner (ok if mac-only prep)."));

        var macRunnerDir = Path.Combine(root, SsdLayout.MacRunner);
        checks.Add(Directory.Exists(macRunnerDir)
            ? ReadinessItem.Pass("macOS Runner.app present")
            : ReadinessItem.Warn("macOS Runner.app present", "Runner.app not found in SSD/mac (ok if Windows-only prep)."));

        var configPath = GetConfigPath(root);
        var (config, configIsValid) = await PortableConfig.LoadWithValidationAsync(configPath);

        checks.Add(configIsValid
            ? ReadinessItem.Pass("Config.json valid")
            : ReadinessItem.Fail("Config.json valid", "Config missing or unreadable; defaults loaded."));

        var ollamaExe = Path.Combine(root, config.OllamaRelativePath);
        checks.Add(File.Exists(ollamaExe)
            ? ReadinessItem.Pass("Windows Ollama executable present")
            : ReadinessItem.Warn("Windows Ollama executable present", "ollama.exe missing under SSD/windows/tools/ollama (ok if mac-only prep)."));

        var macOllamaExe = Path.Combine(root, SsdLayout.MacOllama, "ollama");
        checks.Add(File.Exists(macOllamaExe)
            ? ReadinessItem.Pass("macOS Ollama executable present")
            : ReadinessItem.Warn("macOS Ollama executable present", "ollama missing under SSD/mac/tools/ollama (ok if Windows-only prep)."));

        var modelsDir = Path.Combine(root, SsdLayout.Models);
        checks.Add(Directory.Exists(modelsDir)
            ? ReadinessItem.Pass("Models directory present")
            : ReadinessItem.Fail("Models directory present", "SSD/models directory is missing."));

        var prereqDir = Path.Combine(root, SsdLayout.Prereqs);
        var prereqManifestPath = PrereqCatalog.GetManifestPath(root);
        var prereqManifest = PrereqManifest.Load(prereqManifestPath);
        var prereqIssues = PrereqInstallValidator.ValidateBundleHealth(prereqDir, prereqManifest);
        checks.Add(prereqIssues.Count == 0
            ? ReadinessItem.Pass("Prereq bundle verified")
            : ReadinessItem.Warn("Prereq bundle verified", "Warning: " + string.Join("; ", prereqIssues.Take(3))));

        var installedModels = config.Models.Where(m => m.Status == ModelInstallStatus.Installed).ToList();
        if (installedModels.Count == 0)
        {
            checks.Add(ReadinessItem.Fail("≥1 installed model", "No models marked Installed in config."));
        }
        else
        {
            var invalidModels = new List<string>();
            foreach (var model in installedModels)
            {
                if (string.IsNullOrWhiteSpace(model.Sha256))
                {
                    invalidModels.Add($"{model.Name} (missing stored hash)");
                    model.Status = ModelInstallStatus.Failed;
                    continue;
                }

                var ok = await _modelOperations.VerifyModelAsync(modelsDir, model.Name, model.Sha256, _ => { }, CancellationToken.None);
                if (!ok)
                {
                    invalidModels.Add($"{model.Name} (hash mismatch or blob missing)");
                    model.Status = ModelInstallStatus.Failed;
                }
                else
                {
                    model.LastVerifiedUtc = DateTime.UtcNow;
                }
            }

            if (invalidModels.Count > 0)
            {
                await config.SaveAsync(configPath);
                logger.Error("Model integrity check failed for: " + string.Join(", ", invalidModels));
                checks.Add(ReadinessItem.Fail("≥1 installed model", "Integrity failed: " + string.Join("; ", invalidModels)));
            }
            else
            {
                await config.SaveAsync(configPath);
                checks.Add(ReadinessItem.Pass("≥1 installed model"));
            }
        }

        await RefreshModelStatusesForSelectedDriveAsync();
        return checks;
    }

    /// <summary>
    /// Rebuilds the model status DataGrid by merging configured models (from config)
    /// with discovered models on disk. Configured models show their install status;
    /// on-disk-only models (orphans) are shown separately for the user to adopt.
    /// Includes hardware sizing warnings for each model.
    /// </summary>
    private void RefreshModelStatusGrid(IEnumerable<ModelConfigEntry> configModels, IReadOnlyCollection<string> discoveredOnDisk)
    {
        var rows = new List<ModelGridRow>();
        var configured = configModels.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var freeDiskGb = DriveCombo.SelectedItem is DriveTarget drive ? SystemResources.GetFreeDiskSpaceGb(drive.RootPath) : null;

        foreach (var model in configured)
        {
            var onDisk = discoveredOnDisk.Contains(model.Name);
            var state = DetermineConfiguredState(model, onDisk);
            var warnings = GetSizingWarnings(model.Name, freeDiskGb);
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
            var warnings = GetSizingWarnings(discovered, freeDiskGb);
            rows.Add(new ModelGridRow(discovered, "OnDiskOnly", "Disk", warnings.Count == 0 ? "OK" : string.Join("; ", warnings), "—", "—", "—", true));
        }

        ModelStatusGrid.ItemsSource = rows;
        UpdateModelActionButtons();
    }

    private static string DetermineConfiguredState(ModelConfigEntry model, bool onDisk)
    {
        if (model.Status == ModelInstallStatus.Installed)
        {
            return "Ready";
        }

        if ((model.Status == ModelInstallStatus.NotInstalled || model.Status == ModelInstallStatus.Failed) && !onDisk)
        {
            return "ConfiguredNotDownloaded";
        }

        return model.Status.ToString();
    }

    private List<string> BuildPullSelectionWarnings(IReadOnlyList<string> models, string rootPath)
    {
        var warnings = new List<string>();
        var freeDiskGb = SystemResources.GetFreeDiskSpaceGb(rootPath);
        var estimatedDiskGb = 0;

        foreach (var model in models)
        {
            var modelWarnings = GetSizingWarnings(model, freeDiskGb);
            if (modelWarnings.Count > 0)
            {
                warnings.Add($"{model}: {string.Join("; ", modelWarnings)}");
            }

            estimatedDiskGb += ModelSizingCatalog.Suggest(model).ApproxDiskGb;
        }

        if (freeDiskGb.HasValue && freeDiskGb.Value < estimatedDiskGb)
        {
            warnings.Add($"Selection total needs ~{estimatedDiskGb} GB, but only ~{freeDiskGb.Value} GB is free.");
        }

        return warnings;
    }

    /// <summary>
    /// Generates hardware sizing warnings for a model based on the system's RAM,
    /// VRAM, and available disk space compared to the model's requirements.
    /// </summary>
    private List<string> GetSizingWarnings(string modelTag, int? freeDiskGb)
    {
        var sizing = ModelSizingCatalog.Suggest(modelTag);
        var warnings = new List<string>();

        if (_systemRamGb.HasValue && _systemRamGb.Value < sizing.RecommendedSystemRamGb)
        {
            warnings.Add($"Low RAM: model recommends {sizing.RecommendedSystemRamGb} GB");
        }

        if (sizing.RecommendedVramGb.HasValue)
        {
            if (!_gpuVramGb.HasValue)
            {
                warnings.Add($"VRAM unknown: model recommends {sizing.RecommendedVramGb.Value} GB (may run on CPU)");
            }
            else if (_gpuVramGb.Value < sizing.RecommendedVramGb.Value)
            {
                warnings.Add($"Low VRAM: model recommends {sizing.RecommendedVramGb.Value} GB (may run on CPU)");
            }
        }

        if (freeDiskGb.HasValue && freeDiskGb.Value < sizing.ApproxDiskGb)
        {
            warnings.Add($"Low disk space: needs ~{sizing.ApproxDiskGb} GB");
        }

        return warnings;
    }

    private static string FormatSize(long sizeBytes)
    {
        string[] units = new[] { "B", "KB", "MB", "GB", "TB" };
        double size = sizeBytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", size, units[unit]);
    }

    private string ParseProgressLabel(string line)
    {
        var percentMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\d{1,3})%");
        if (percentMatch.Success)
        {
            return $"Pulling... {percentMatch.Groups[1].Value}%";
        }

        return $"Pulling... {line}";
    }

    private void RefreshReadinessGrid(IEnumerable<ReadinessItem> checks)
    {
        ReadinessGrid.ItemsSource = checks
            .Select(c => new { c.Check, Result = $"{(c.Passed ? "PASS" : "FAIL")}: {c.Result}" })
            .ToList();
    }

    private void RefreshSelectedDriveEncryptionState()
    {
        _isSelectedDriveEncrypted = DriveCombo.SelectedItem is DriveTarget drive
            && SsdEncryption.IsEffectivelyEncryptedForWriteGuard(drive.RootPath);
        if (_isSelectedDriveEncrypted && !_isModelOperationRunning)
        {
            StatusText.Text = "Encrypted drive selected (read-only in PrepApp)";
        }
    }

    private bool EnsureSelectedDriveWritableForPrep(string operationName)
    {
        RefreshSelectedDriveEncryptionState();
        if (!PrepDriveWriteGuard.IsWriteBlocked(_isSelectedDriveEncrypted))
        {
            return true;
        }

        var message = PrepDriveWriteGuard.BuildBlockedOperationMessage(operationName);
        StatusText.Text = "Encrypted drive selected (read-only in PrepApp)";
        AppendLog(message);
        UpdateWarning();
        UpdateModelActionButtons();
        return false;
    }

    private void UpdateWarning()
    {
        var warnings = new List<string>();
        if (DriveCombo.SelectedItem is DriveTarget drive && !string.IsNullOrWhiteSpace(drive.Warning))
        {
            warnings.Add(drive.Warning);
        }

        if (_isSelectedDriveEncrypted)
        {
            warnings.Add(PrepDriveWriteGuard.ReadOnlyReason);
        }

        WarningText.Text = string.Join(Environment.NewLine, warnings);
    }

    private void AppendLog(string line)
    {
        Dispatcher.Invoke(() =>
        {
            LogText.AppendText(line + Environment.NewLine);
            LogText.ScrollToEnd();
        });
    }

    private List<ModelGridRow> GetSelectedModelRows()
    {
        return ModelStatusGrid.SelectedItems
            .OfType<ModelGridRow>()
            .ToList();
    }

    private void ModelStatusGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateModelActionButtons();
        _systemRamGb = SystemResources.GetTotalSystemRamGb();
        _gpuVramGb = SystemResources.GetGpuVramGb();
    }

    private void SetModelOperationUiState(bool running, string? status = null)
    {
        _isModelOperationRunning = running;
        UpdateModelActionButtons();
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusText.Text = status;
        }
    }

    /// <summary>
    /// Updates the enabled/disabled state of all model action buttons based on
    /// current state: whether a drive is selected, model operation is running,
    /// drive is encrypted (read-only), and what type of model rows are selected.
    /// </summary>
    private void UpdateModelActionButtons()
    {
        RefreshSelectedDriveEncryptionState();
        var selected = GetSelectedModelRows();
        var selectedDrive = DriveCombo.SelectedItem as DriveTarget;
        var hasDriveSelected = selectedDrive is not null;
        var hasConfiguredSelection = selected.Any(r => !r.IsOnDiskOnly);
        var hasOrphanedSelection = selected.Any(r => r.IsOnDiskOnly);
        var canMutateDrive = !_isModelOperationRunning && !_isSelectedDriveEncrypted;

        AddModelButton.IsEnabled = canMutateDrive && hasDriveSelected;
        ModelTagText.IsEnabled = canMutateDrive;
        FinalizeButton.IsEnabled = canMutateDrive && hasDriveSelected;
        EnableDriveEncryptionCheckBox.IsEnabled = canMutateDrive && hasDriveSelected;
        PullInstallButton.IsEnabled = canMutateDrive && hasConfiguredSelection;
        VerifyButton.IsEnabled = canMutateDrive && hasConfiguredSelection;
        RemoveButton.IsEnabled = canMutateDrive && selected.Count > 0;
        PullSelectedButton.IsEnabled = canMutateDrive && hasConfiguredSelection;
        AddOrphanButton.IsEnabled = canMutateDrive && hasOrphanedSelection;
        CancelOperationButton.IsEnabled = _isModelOperationRunning && !_isSelectedDriveEncrypted;
        FormatPrepareButton.IsEnabled = canMutateDrive && selectedDrive?.IsRemovable == true;
        CheckPrereqUpdatesButton.IsEnabled = canMutateDrive && hasDriveSelected;
        CheckReadinessButton.IsEnabled = canMutateDrive && hasDriveSelected;
        UpdateStarterCatalogButtons();
    }

    private static bool IsRunningAsAdministrator()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task<List<string>> RunPowerShellAsync(string command)
    {
        var output = new List<string>();
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        while (!process.StandardOutput.EndOfStream)
        {
            var line = await process.StandardOutput.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                output.Add(line);
            }
        }

        while (!process.StandardError.EndOfStream)
        {
            var line = await process.StandardError.ReadLineAsync();
            if (!string.IsNullOrWhiteSpace(line))
            {
                output.Add("ERR: " + line);
            }
        }

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell command failed with exit code {process.ExitCode}.");
        }

        return output;
    }

    /// <summary>
    /// Result of a single readiness check: a named check with pass/fail status and result detail.
    /// Warn is treated as a pass with additional info (e.g., macOS Runner missing on Windows-only prep).
    /// </summary>
    private sealed record ReadinessItem(string Check, bool Passed, string Result)
    {
        public static ReadinessItem Pass(string check) => new(check, true, "OK");
        public static ReadinessItem Fail(string check, string reason) => new(check, false, reason);
        public static ReadinessItem Warn(string check, string reason) => new(check, true, reason);
    }

    /// <summary>
    /// View model for a row in the model status DataGrid, showing install state,
    /// SHA preview, sizing warnings, and whether the model is config-tracked or on-disk-only.
    /// </summary>
    private sealed record ModelGridRow(string Name, string Status, string Source, string SizingWarning, string SizeDisplay, string ShaPreview, string LastVerifiedDisplay, bool IsOnDiskOnly);

    /// <summary>
    /// View model for a row in the starter model catalog DataGrid, with
    /// selection state and hardware sizing warning display.
    /// </summary>
    private sealed class StarterModelRow(
        string tag,
        string @params,
        string sizeTier,
        string description,
        string useCasesDisplay,
        string sizingWarning)
    {
        public bool IsSelected { get; set; }
        public string Tag { get; } = tag;
        public string Params { get; } = @params;
        public string SizeTier { get; } = sizeTier;
        public string Description { get; } = description;
        public string UseCasesDisplay { get; } = useCasesDisplay;
        public string SizingWarning { get; set; } = sizingWarning;
    }
}
