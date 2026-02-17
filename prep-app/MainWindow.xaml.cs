using System.IO.Compression;
using System.IO;
using FreeAiSsd.Shared;

namespace FreeAiSsd.PrepApp;

public partial class MainWindow : System.Windows.Window
{
    private readonly DownloadManager _downloadManager = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadDrives();
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
    }

    private void DriveCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateWarning();

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
            await PullModelsAsync(ollamaExe, root, selectedModels, logger);

            StatusText.Text = "Staging runner payload...";
            await StageRunnerAsync(root, logger);

            var config = new PortableConfig
            {
                OllamaPort = 11434,
                Models = selectedModels,
                PreparedAtUtc = DateTime.UtcNow,
                PreferredCompute = "cpu"
            };
            config.Save(Path.Combine(root, config.ConfigRelativePath));

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

    private async Task PullModelsAsync(string ollamaExe, string ssdRoot, IReadOnlyList<string> models, SsdLogger logger)
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
            var exitCode = await ProcessRunner.RunAsync(ollamaExe, $"pull {model}", Path.GetDirectoryName(ollamaExe)!, env,
                onOutput: line =>
                {
                    AppendLog(line);
                    logger.Info(line);
                });

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Failed to pull model {model}. Exit code: {exitCode}");
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

    private List<string> GetSelectedModels()
    {
        var models = new List<string>();
        if (ModelLlama.IsChecked == true) models.Add("llama3.2:3b");
        if (ModelQwen.IsChecked == true) models.Add("qwen2.5:3b");
        return models;
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
}
