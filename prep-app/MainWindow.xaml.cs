using System.Globalization;
using System.IO.Compression;
using FreeAiSsd.Shared;
using System.IO;

namespace FreeAiSsd.PrepApp;

public partial class MainWindow : System.Windows.Window
{
    private readonly DownloadManager _downloadManager = new();
    private readonly ModelOperations _modelOperations = new();
    private CancellationTokenSource? _modelOperationCts;
    private bool _isModelOperationRunning;

    public MainWindow()
    {
        InitializeComponent();
        LoadDrives();
        RefreshModelStatusGrid(Array.Empty<ModelConfigEntry>());
        RefreshReadinessGrid(Array.Empty<ReadinessItem>());
        UpdateModelActionButtons();
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
        RefreshModelStatusGrid(config.Models);
        ModelTagText.Text = string.Empty;
        AppendLog($"Added model '{tag}' to config.");
    }

    private async void PullInstall_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var selected = GetSelectedModelNames().Take(1).ToList();
        if (selected.Count == 0)
        {
            AppendLog("Select a model row to pull/install.");
            return;
        }

        await PullModelsForSelectedDriveAsync(selected);
    }

    private async void PullSelected_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var selected = GetSelectedModelNames();
        if (selected.Count == 0)
        {
            AppendLog("Select one or more model rows for pull.");
            return;
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

        var selected = GetSelectedModelNames().Take(1).ToList();
        if (selected.Count == 0)
        {
            AppendLog("Select a model row to verify.");
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

        var selected = GetSelectedModelNames().Take(1).ToList();
        if (selected.Count == 0)
        {
            AppendLog("Select a model row to remove.");
            return;
        }

        var configPath = GetConfigPath(drive.RootPath);
        var config = await PortableConfig.LoadAsync(configPath);
        var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, selected[0], StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return;
        }

        model.Status = ModelInstallStatus.NotInstalled;
        model.Sha256 = null;
        model.SizeBytes = null;
        model.LastVerifiedUtc = null;
        await config.SaveAsync(configPath);
        RefreshModelStatusGrid(config.Models);
        AppendLog($"{model.Name}: model removed from config; storage cleanup not implemented yet.");
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
            RefreshModelStatusGrid(config.Models);

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
        RefreshModelStatusGrid(config.Models);
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
            RefreshModelStatusGrid(Array.Empty<ModelConfigEntry>());
            return;
        }

        var configPath = GetConfigPath(drive.RootPath);
        if (!File.Exists(configPath))
        {
            RefreshModelStatusGrid(Array.Empty<ModelConfigEntry>());
            return;
        }

        var config = await PortableConfig.LoadAsync(configPath);
        RefreshModelStatusGrid(config.Models);
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

        RefreshModelStatusGrid(config.Models);
        return checks;
    }

    private void RefreshModelStatusGrid(IEnumerable<ModelConfigEntry> models)
    {
        var rows = models
            .OrderBy(m => m.Name)
            .Select(m => new ModelGridRow(
                m.Name,
                m.Status.ToString(),
                m.SizeBytes.HasValue ? FormatSize(m.SizeBytes.Value) : "—",
                string.IsNullOrWhiteSpace(m.Sha256) ? "—" : m.Sha256[..Math.Min(8, m.Sha256.Length)],
                m.LastVerifiedUtc.HasValue ? m.LastVerifiedUtc.Value.ToLocalTime().ToString("u") : "—"))
            .ToList();

        ModelStatusGrid.ItemsSource = rows;
        UpdateModelActionButtons();
    }

    private static string FormatSize(long sizeBytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
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

    private List<string> GetSelectedModelNames()
    {
        return ModelStatusGrid.SelectedItems
            .OfType<ModelGridRow>()
            .Select(r => r.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void ModelStatusGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateModelActionButtons();
    }

    private void SetModelOperationUiState(bool running, string? status = null)
    {
        _isModelOperationRunning = running;
        FinalizeButton.IsEnabled = !running;
        AddModelButton.IsEnabled = !running;
        PullInstallButton.IsEnabled = !running && GetSelectedModelNames().Count > 0;
        VerifyButton.IsEnabled = !running && GetSelectedModelNames().Count > 0;
        RemoveButton.IsEnabled = !running && GetSelectedModelNames().Count > 0;
        PullSelectedButton.IsEnabled = !running && GetSelectedModelNames().Count > 0;
        CancelOperationButton.IsEnabled = running;
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusText.Text = status;
        }
    }

    private void UpdateModelActionButtons()
    {
        var hasSelection = GetSelectedModelNames().Count > 0;
        PullInstallButton.IsEnabled = !_isModelOperationRunning && hasSelection;
        VerifyButton.IsEnabled = !_isModelOperationRunning && hasSelection;
        RemoveButton.IsEnabled = !_isModelOperationRunning && hasSelection;
        PullSelectedButton.IsEnabled = !_isModelOperationRunning && hasSelection;
    }

    private sealed record ReadinessItem(string Check, bool Passed, string Result)
    {
        public static ReadinessItem Pass(string check) => new(check, true, "OK");
        public static ReadinessItem Fail(string check, string reason) => new(check, false, reason);
    }

    private sealed record ModelGridRow(string Name, string Status, string SizeDisplay, string ShaPreview, string LastVerifiedDisplay);
}
