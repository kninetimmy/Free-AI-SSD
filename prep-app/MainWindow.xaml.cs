using System.IO.Compression;
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

    private void LoadDrives()
    {
        var drives = DriveInspector.GetCandidateDrives();
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
            AppendLog($"Finalize failed: {ex.Message}");
            logger.Error(ex.ToString());
        }
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
        var env = new Dictionary<string, string>
        {
            ["OLLAMA_MODELS"] = Path.Combine(ssdRoot, SsdLayout.Models),
            ["OLLAMA_HOST"] = "127.0.0.1:11434"
        };

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
        var sourceRunnerDir = Path.Combine(AppContext.BaseDirectory, "runner-publish");
        var targetRunnerDir = Path.Combine(ssdRoot, SsdLayout.Runner);
        Directory.CreateDirectory(targetRunnerDir);

        if (!Directory.Exists(sourceRunnerDir))
        {
            var hint = "Runner publish folder not found. Build runner and copy publish output to prep-app/bin/.../runner-publish.";
            AppendLog(hint);
            logger.Error(hint);
            return;
        }

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
