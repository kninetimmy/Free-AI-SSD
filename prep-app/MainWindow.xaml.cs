using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using FreeAiSsd.Shared;

namespace FreeAiSsd.PrepApp;

public partial class MainWindow : System.Windows.Window
{
    private readonly DownloadManager _downloadManager = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadDrives();
        RefreshModelStatusGrid(Array.Empty<ModelConfigEntry>());
        RefreshReadinessGrid(Array.Empty<ReadinessItem>());
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

    private async void Finalize_Click(object sender, System.Windows.RoutedEventArgs e)
    {
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

        var selectedModels = GetSelectedModels();
        if (!selectedModels.Any())
        {
            AppendLog("Select at least one model.");
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

            var configPath = Path.Combine(root, new PortableConfig().ConfigRelativePath);
            var config = await PortableConfig.LoadAsync(configPath);
            config.PreparedAtUtc = DateTime.UtcNow;
            config.OllamaPort = 11434;
            config.PreferredCompute = "cpu";
            config.Models = MergeModelSelection(config.Models, selectedModels);
            await config.SaveAsync(configPath);
            RefreshModelStatusGrid(config.Models);

            var ollamaZipPath = Path.Combine(root, SsdLayout.Cache, "ollama-windows-amd64.zip");
            StatusText.Text = "Downloading Ollama package...";
            await _downloadManager.DownloadFileWithResumeAsync(
                new DownloadRequest(OllamaUrlText.Text.Trim(), ollamaZipPath),
                new Progress<DownloadProgress>(p =>
                {
                    Progress.Value = p.Percent;
                    StatusText.Text = $"Downloading Ollama {p.Percent:F1}%";
                }));

            var ollamaDir = Path.Combine(root, SsdLayout.Ollama);
            ExtractOllamaZip(ollamaZipPath, ollamaDir);
            logger.Info("Ollama package staged.");

            var ollamaExe = ResolveOllamaExe(ollamaDir);
            await PullModelsAsync(ollamaExe, root, selectedModels, logger, configPath);

            StatusText.Text = "Staging runner payload...";
            await StageRunnerAsync(root, logger);

            // Final readiness gate before allowing completion.
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

    private static void ExtractOllamaZip(string zipPath, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(zipPath, destination, overwriteFiles: true);
    }

    private static string ResolveOllamaExe(string ollamaDir)
    {
        var direct = Path.Combine(ollamaDir, "ollama.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        var nested = Directory.EnumerateFiles(ollamaDir, "ollama.exe", SearchOption.AllDirectories).FirstOrDefault();
        return nested ?? throw new FileNotFoundException("ollama.exe not found after extraction.");
    }

    private async Task PullModelsAsync(string ollamaExe, string ssdRoot, IReadOnlyList<string> models, SsdLogger logger, string configPath)
    {
        var modelPath = Path.Combine(ssdRoot, SsdLayout.Models);
        var pullPort = NetUtils.FindFreePort(11434);
        var env = new Dictionary<string, string>
        {
            ["OLLAMA_MODELS"] = modelPath,
            ["OLLAMA_HOST"] = $"127.0.0.1:{pullPort}"
        };

        logger.Info($"Using OLLAMA_MODELS={modelPath} for model pulls.");
        logger.Info($"Using dynamic OLLAMA_HOST port {pullPort} for model pulls.");

        foreach (var model in models)
        {
            StatusText.Text = $"Pulling model {model}...";
            await UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Downloading);

            var exitCode = await ProcessRunner.RunAsync(ollamaExe, $"pull {model}", Path.GetDirectoryName(ollamaExe)!, env,
                onOutput: line =>
                {
                    AppendLog(line);
                    logger.Info(line);
                });

            if (exitCode != 0)
            {
                await UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Failed);
                throw new InvalidOperationException($"Failed to pull model {model}. Exit code: {exitCode}");
            }

            var modelFile = FindModelBlobForModel(modelPath, model);
            if (modelFile is null)
            {
                await UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Failed);
                throw new FileNotFoundException($"Unable to locate model blob for {model} in {modelPath}.");
            }

            var sha256 = await ComputeSha256Async(modelFile);
            var size = new FileInfo(modelFile).Length;
            await UpdateModelStatusAsync(configPath, model, ModelInstallStatus.Installed, sha256, size, DateTime.UtcNow);
        }
    }

    private static string? FindModelBlobForModel(string modelRoot, string model)
    {
        var manifestTag = model.Replace(':', '-');
        var manifestsPath = Path.Combine(modelRoot, "manifests", "registry.ollama.ai", "library");
        if (!Directory.Exists(manifestsPath))
        {
            return null;
        }

        var manifest = Directory.EnumerateFiles(manifestsPath, manifestTag, SearchOption.AllDirectories).FirstOrDefault();
        if (manifest is null)
        {
            return null;
        }

        var content = File.ReadAllText(manifest);
        var digestLine = content.Split('\n').FirstOrDefault(l => l.Contains("\"digest\"", StringComparison.OrdinalIgnoreCase));
        if (digestLine is null)
        {
            return null;
        }

        var marker = "sha256:";
        var idx = digestLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var hashStart = idx + marker.Length;
        var hashChars = new string(digestLine.Skip(hashStart).TakeWhile(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(hashChars))
        {
            return null;
        }

        var blob = Path.Combine(modelRoot, "blobs", $"sha256-{hashChars}");
        return File.Exists(blob) ? blob : null;
    }

    private static async Task<string> ComputeSha256Async(string modelPath)
    {
        await using var stream = File.OpenRead(modelPath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<bool> VerifyModelIntegrity(string modelPath, string expectedHash)
    {
        var actualHash = await ComputeSha256Async(modelPath);
        return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
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
        if (sha256 is not null)
        {
            model.Sha256 = sha256;
        }

        if (sizeBytes.HasValue)
        {
            model.SizeBytes = sizeBytes.Value;
        }

        model.LastVerifiedUtc = lastVerifiedUtc;

        await config.SaveAsync(configPath);
        RefreshModelStatusGrid(config.Models);
    }

    private static List<ModelConfigEntry> MergeModelSelection(List<ModelConfigEntry> existing, IReadOnlyList<string> selected)
    {
        var byName = existing.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var model in selected)
        {
            if (!byName.ContainsKey(model))
            {
                byName[model] = new ModelConfigEntry { Name = model, Status = ModelInstallStatus.NotInstalled };
            }
        }

        return byName.Values.OrderBy(m => m.Name).ToList();
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

    private List<string> GetSelectedModels()
    {
        var models = new List<string>();
        if (ModelLlama.IsChecked == true) models.Add("llama3.2:3b");
        if (ModelQwen.IsChecked == true) models.Add("qwen2.5:3b");
        return models;
    }

    private async Task RefreshModelStatusesForSelectedDriveAsync()
    {
        if (DriveCombo.SelectedItem is not DriveTarget drive)
        {
            RefreshModelStatusGrid(Array.Empty<ModelConfigEntry>());
            return;
        }

        var configPath = Path.Combine(drive.RootPath, new PortableConfig().ConfigRelativePath);
        if (!File.Exists(configPath))
        {
            var selected = GetSelectedModels().Select(m => new ModelConfigEntry { Name = m, Status = ModelInstallStatus.NotInstalled }).ToList();
            RefreshModelStatusGrid(selected);
            return;
        }

        var config = await PortableConfig.LoadAsync(configPath);
        var merged = MergeModelSelection(config.Models, GetSelectedModels());
        RefreshModelStatusGrid(merged);
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

        var configPath = Path.Combine(root, new PortableConfig().ConfigRelativePath);
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

                var modelBlob = FindModelBlobForModel(modelsDir, model.Name);
                if (modelBlob is null)
                {
                    invalidModels.Add($"{model.Name} (blob missing)");
                    model.Status = ModelInstallStatus.Failed;
                    continue;
                }

                var ok = await VerifyModelIntegrity(modelBlob, model.Sha256);
                if (!ok)
                {
                    invalidModels.Add($"{model.Name} (hash mismatch)");
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
        ModelStatusGrid.ItemsSource = models
            .OrderBy(m => m.Name)
            .Select(m => new { m.Name, Status = m.Status.ToString() })
            .ToList();
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

    private sealed record ReadinessItem(string Check, bool Passed, string Result)
    {
        public static ReadinessItem Pass(string check) => new(check, true, "OK");
        public static ReadinessItem Fail(string check, string reason) => new(check, false, reason);
    }
}
