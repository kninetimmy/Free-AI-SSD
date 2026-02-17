using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Principal;
using FreeAiSsd.Shared;

namespace FreeAiSsd.PrepApp;

public partial class MainWindow : System.Windows.Window
{
    private readonly DownloadManager _downloadManager = new();
    private readonly ModelOperations _modelOperations = new();
    private CancellationTokenSource? _modelOperationCts;
    private bool _isModelOperationRunning;
    private int? _systemRamGb;
    private int? _gpuVramGb;

    public MainWindow()
    {
        InitializeComponent();
        LoadDrives();
        RefreshModelStatusGrid(Array.Empty<ModelConfigEntry>(), Array.Empty<string>());
        RefreshReadinessGrid(Array.Empty<ReadinessItem>());
        UpdateModelActionButtons();
        _systemRamGb = SystemResources.GetTotalSystemRamGb();
        _gpuVramGb = SystemResources.GetGpuVramGb();
    }

    private void RefreshDrives_Click(object sender, System.Windows.RoutedEventArgs e) => LoadDrives();

    private void ShowFixedDrivesChanged(object sender, System.Windows.RoutedEventArgs e) => LoadDrives();

    private void LoadDrives()
    {
        var includeFixed = ShowFixedDrivesCheckBox?.IsChecked == true;
        var drives = DriveInspector.GetCandidateDrives(includeFixed);
        DriveCombo.ItemsSource = drives;
        DriveCombo.SelectedIndex = drives.Count > 0 ? 0 : -1;
        UpdateWarning();
        _ = RefreshModelStatusesForSelectedDriveAsync();
    }

    private void DriveCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateWarning();
        _ = RefreshModelStatusesForSelectedDriveAsync();
    }

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

        var configPath = GetConfigPath(drive.RootPath);
        SsdLayout.EnsureStructure(drive.RootPath);
        var config = await PortableConfig.LoadAsync(configPath);
        UpsertModel(config.Models, tag, ModelInstallStatus.NotInstalled);
        await config.SaveAsync(configPath);
        await RefreshModelStatusesForSelectedDriveAsync();
        ModelTagText.Text = string.Empty;
        AppendLog($"Added model '{tag}' to config.");
    }

    private async void AddOrphanToConfig_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
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

    private async void FormatPrepare_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            AppendLog("Select a target drive first.");
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

        if (!ConfirmDriveSelection(drive))
        {
            AppendLog("Finalize cancelled.");
            return;
        }

        var root = drive.RootPath;
        var logger = new SsdLogger(root, "prep");
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

            await EnsureOllamaReadyAsync(root, logger, CancellationToken.None);

            StatusText.Text = "Staging offline prerequisites...";
            await StagePrerequisitesAsync(root, logger, CancellationToken.None);

            StatusText.Text = "Staging runner payload...";
            await StageRunnerAsync(root, logger);

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

    private async Task<string> EnsureOllamaReadyAsync(string root, SsdLogger logger, CancellationToken ct)
    {
        SsdLayout.EnsureStructure(root);
        var ollamaZipPath = Path.Combine(root, SsdLayout.Cache, "ollama-windows-amd64.zip");
        var ollamaDir = Path.Combine(root, SsdLayout.Ollama);

        var ollamaExe = ResolveOllamaExe(ollamaDir);
        if (ollamaExe is not null)
        {
            return ollamaExe;
        }

        StatusText.Text = "Downloading Ollama package...";
        await _downloadManager.DownloadFileWithResumeAsync(
            new DownloadRequest(OllamaUrlText.Text.Trim(), ollamaZipPath),
            new Progress<DownloadProgress>(p =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = p.Percent;
                StatusText.Text = $"Downloading Ollama {p.Percent:F1}%";
            }),
            ct);

        ExtractOllamaZip(ollamaZipPath, ollamaDir);
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

    private async Task<List<ReadinessItem>> RunReadinessChecksAsync(string root, SsdLogger logger)
    {
        var checks = new List<ReadinessItem>();

        var runnerDir = Path.Combine(root, SsdLayout.Runner);
        var runnerExe = Path.Combine(runnerDir, "FreeAiSsd.Runner.exe");
        checks.Add(File.Exists(runnerExe)
            ? ReadinessItem.Pass("Runner files present")
            : ReadinessItem.Fail("Runner files present", "Runner executable not found in SSD/runner."));

        var configPath = GetConfigPath(root);
        var (config, configIsValid) = await PortableConfig.LoadWithValidationAsync(configPath);

        checks.Add(configIsValid
            ? ReadinessItem.Pass("Config.json valid")
            : ReadinessItem.Fail("Config.json valid", "Config missing or unreadable; defaults loaded."));

        var ollamaExe = Path.Combine(root, config.OllamaRelativePath);
        checks.Add(File.Exists(ollamaExe)
            ? ReadinessItem.Pass("Ollama executable present")
            : ReadinessItem.Fail("Ollama executable present", "ollama.exe is missing in staged tools path."));

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

    private void UpdateWarning()
    {
        WarningText.Text = DriveCombo.SelectedItem is DriveTarget d ? d.Warning : string.Empty;
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
        FinalizeButton.IsEnabled = !running;
        AddModelButton.IsEnabled = !running;
        PullInstallButton.IsEnabled = !running && GetSelectedModelRows().Any(r => !r.IsOnDiskOnly);
        VerifyButton.IsEnabled = !running && GetSelectedModelRows().Any(r => !r.IsOnDiskOnly);
        RemoveButton.IsEnabled = !running && GetSelectedModelRows().Count > 0;
        PullSelectedButton.IsEnabled = !running && GetSelectedModelRows().Any(r => !r.IsOnDiskOnly);
        AddOrphanButton.IsEnabled = !running && GetSelectedModelRows().Any(r => r.IsOnDiskOnly);
        CancelOperationButton.IsEnabled = running;
        FormatPrepareButton.IsEnabled = !running && DriveCombo.SelectedItem is DriveTarget d && d.IsRemovable;
        CheckPrereqUpdatesButton.IsEnabled = !running;
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusText.Text = status;
        }
    }

    private void UpdateModelActionButtons()
    {
        var selected = GetSelectedModelRows();
        var hasConfiguredSelection = selected.Any(r => !r.IsOnDiskOnly);
        var hasOrphanedSelection = selected.Any(r => r.IsOnDiskOnly);
        PullInstallButton.IsEnabled = !_isModelOperationRunning && hasConfiguredSelection;
        VerifyButton.IsEnabled = !_isModelOperationRunning && hasConfiguredSelection;
        RemoveButton.IsEnabled = !_isModelOperationRunning && selected.Count > 0;
        PullSelectedButton.IsEnabled = !_isModelOperationRunning && hasConfiguredSelection;
        AddOrphanButton.IsEnabled = !_isModelOperationRunning && hasOrphanedSelection;
        FormatPrepareButton.IsEnabled = !_isModelOperationRunning && DriveCombo.SelectedItem is DriveTarget d && d.IsRemovable;
        CheckPrereqUpdatesButton.IsEnabled = !_isModelOperationRunning && DriveCombo.SelectedItem is not null;
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

    private sealed record ReadinessItem(string Check, bool Passed, string Result)
    {
        public static ReadinessItem Pass(string check) => new(check, true, "OK");
        public static ReadinessItem Fail(string check, string reason) => new(check, false, reason);
        public static ReadinessItem Warn(string check, string reason) => new(check, true, reason);
    }

    private sealed record ModelGridRow(string Name, string Status, string Source, string SizingWarning, string SizeDisplay, string ShaPreview, string LastVerifiedDisplay, bool IsOnDiskOnly);
}
