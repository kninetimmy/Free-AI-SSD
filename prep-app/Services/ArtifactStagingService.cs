using System.IO;
using System.IO.Compression;
using System.Text.Json;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class ArtifactStagingService : IArtifactStagingService
{
    public async Task StageRunnerAsync(string ssdRoot, Action<string> onLog)
    {
        var sourceRunnerDir = ResolveRunnerPublishDirectory();
        var targetRunnerDir = Path.Combine(ssdRoot, SsdLayout.Runner);
        Directory.CreateDirectory(targetRunnerDir);

        if (sourceRunnerDir is null)
        {
            var hint = "Runner publish folder not found. Re-download the ZIP and ensure runner-publish is next to FreeAiSsd.PrepApp.exe, or run ./build.ps1 to stage runner artifacts for local development.";
            onLog(hint);
            throw new DirectoryNotFoundException(hint);
        }

        onLog($"Using runner payload from: {sourceRunnerDir}");
        foreach (var file in Directory.EnumerateFiles(sourceRunnerDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRunnerDir, file);
            var destination = Path.Combine(targetRunnerDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        onLog("Runner artifacts staged.");
        await Task.CompletedTask;
    }

    public async Task StageMacRunnerAsync(string ssdRoot, Action<string> onLog, CancellationToken ct)
    {
        var macAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        if (!macAvailability.MacArtifactsAvailable)
        {
            var message = macAvailability.MacArtifactsProblem ?? "macOS artifacts are unavailable.";
            onLog($"Skipped macOS runner staging: {message}");
            throw new InvalidOperationException(message);
        }

        var sourceRunnerZip = Path.Combine(AppContext.BaseDirectory, "mac", "Runner.app.zip");
        var macRoot = Path.Combine(ssdRoot, SsdLayout.Mac);
        Directory.CreateDirectory(macRoot);
        var targetZip = Path.Combine(macRoot, "Runner.app.zip");
        File.Copy(sourceRunnerZip, targetZip, overwrite: true);

        var extractedRunner = Path.Combine(macRoot, "Runner.app");
        if (Directory.Exists(extractedRunner))
            Directory.Delete(extractedRunner, recursive: true);

        ZipFile.ExtractToDirectory(targetZip, macRoot, overwriteFiles: true);
        onLog("Staged macOS Runner.app and archive.");
        await Task.CompletedTask;
    }

    public async Task StageMacOllamaAsync(string ssdRoot, Action<string> onLog, CancellationToken ct)
    {
        var macAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        if (!macAvailability.MacArtifactsAvailable)
        {
            var message = macAvailability.MacArtifactsProblem ?? "macOS artifacts are unavailable.";
            onLog($"Skipped macOS Ollama staging: {message}");
            throw new InvalidOperationException(message);
        }

        var bundledArchive = Path.Combine(AppContext.BaseDirectory, "mac", "tools", "ollama", "ollama-darwin.zip");
        var cacheArchive = Path.Combine(ssdRoot, SsdLayout.Cache, "ollama-darwin.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheArchive)!);
        File.Copy(bundledArchive, cacheArchive, overwrite: true);
        var actualSha = DownloadManager.ComputeSha256(cacheArchive);

        var ollamaDir = Path.Combine(ssdRoot, SsdLayout.MacOllama);
        if (Directory.Exists(ollamaDir))
            Directory.Delete(ollamaDir, recursive: true);

        Directory.CreateDirectory(ollamaDir);
        ZipFile.ExtractToDirectory(cacheArchive, ollamaDir, overwriteFiles: true);

        var cliPath = Directory.EnumerateFiles(ollamaDir, "ollama", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("Could not locate macOS ollama binary after extraction.");

        var finalCliPath = Path.Combine(ollamaDir, "ollama");
        File.Copy(cliPath, finalCliPath, overwrite: true);

        var sourceManifest = Path.Combine(AppContext.BaseDirectory, "mac", "tools", "ollama", "mac-tools-manifest.json");
        if (File.Exists(sourceManifest))
            File.Copy(sourceManifest, Path.Combine(ollamaDir, "mac-tools-manifest.json"), overwrite: true);

        var manifest = JsonSerializer.Serialize(new
        {
            id = MacToolCatalog.Ollama.Id,
            sourceUrl = MacToolCatalog.Ollama.SourceUrl,
            archive = MacToolCatalog.Ollama.ArchiveFileName,
            sha256 = actualSha,
            downloadedAtUtc = DateTime.UtcNow.ToString("O")
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(MacToolCatalog.GetManifestPath(ssdRoot), manifest, ct);
        onLog("Staged macOS Ollama runtime.");
    }

    public bool AreMacArtifactsAvailable(out string? problem)
    {
        var result = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        problem = result.MacArtifactsProblem;
        return result.MacArtifactsAvailable;
    }

    private static string? ResolveRunnerPublishDirectory()
    {
        var baseDirCandidate = Path.Combine(AppContext.BaseDirectory, "runner-publish");
        if (DirectoryContainsRunner(baseDirCandidate))
            return baseDirCandidate;

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
            return null;

        var buildConfigurations = new[] { "Release", "Debug" };
        foreach (var configuration in buildConfigurations)
        {
            var candidate = Path.Combine(repoRoot, "prep-app", "bin", configuration, "net8.0-windows", "runner-publish");
            if (DirectoryContainsRunner(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FreeAiSsd.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static bool DirectoryContainsRunner(string path)
        => Directory.Exists(path) && File.Exists(Path.Combine(path, "FreeAiSsd.Runner.exe"));
}
