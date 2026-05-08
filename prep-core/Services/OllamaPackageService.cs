using System.IO.Compression;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class OllamaPackageService : IOllamaPackageService
{
    private readonly DownloadManager _downloadManager = new();

    public async Task<string> EnsureOllamaReadyAsync(string root, string ollamaUrl,
        Action<string> onLog, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        SsdLayout.EnsureStructure(root);
        var sourceValidation = OllamaPackageTrustPolicy.ValidatePackageSource(ollamaUrl);
        if (!sourceValidation.IsTrusted || sourceValidation.Metadata is null)
            throw new InvalidOperationException(sourceValidation.Message);

        var ollamaZipPath = Path.Combine(root, SsdLayout.Cache, "ollama-windows-amd64.zip");
        var ollamaDir = Path.Combine(root, SsdLayout.Ollama);

        var ollamaExe = ResolveOllamaExe(ollamaDir);
        if (ollamaExe is not null)
        {
            var executionGate = OllamaPackageTrustPolicy.ValidateExecutionAttestation(root, sourceValidation.Metadata.Url);
            if (!executionGate.IsTrusted)
                throw new InvalidOperationException(executionGate.Message);
            return ollamaExe;
        }

        onLog("Downloading Ollama package...");
        await _downloadManager.DownloadFileWithResumeAsync(
            new DownloadRequest(sourceValidation.Metadata.Url, ollamaZipPath),
            progress ?? new Progress<DownloadProgress>(),
            ct);

        var digestValidation = OllamaPackageTrustPolicy.ValidateDownloadedPackage(ollamaZipPath, sourceValidation.Metadata);
        if (!digestValidation.IsTrusted)
            throw new InvalidOperationException(digestValidation.Message);

        ExtractOllamaZip(ollamaZipPath, ollamaDir);
        OllamaPackageTrustPolicy.WriteTrustAttestation(root, sourceValidation.Metadata);
        onLog("Ollama package staged.");

        return ResolveOllamaExe(ollamaDir) ?? throw new FileNotFoundException($"Unable to locate Ollama binary under {ollamaDir}");
    }

    public async Task<IOllamaServerHandle> StartTemporaryServerAsync(
        string ollamaExe, string modelsRoot, Action<string> onLog, CancellationToken ct)
    {
        return await OllamaServerHandle.StartAsync(ollamaExe, modelsRoot, onLog, ct);
    }

    public string? ResolveOllamaExe(string ollamaDir)
        => OperatingSystem.IsWindows()
            ? ResolveOllamaExe(ollamaDir, GetOllamaFileName())
            : ResolveMacOllamaExe(ollamaDir);

    internal static string GetOllamaFileName()
        => OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";

    internal static string? ResolveOllamaExe(string ollamaDir, string fileName)
    {
        if (!Directory.Exists(ollamaDir)) return null;
        return Directory.EnumerateFiles(ollamaDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
    }

    // MAC26: on Mac the upstream `ollama-darwin.zip` ships an Ollama.app GUI
    // bundle. The top-level `ollama` binary is a LaunchServices shim (strips
    // env, SIGKILL-prone). The self-contained CLI server is buried at
    // Ollama.app/Contents/Resources/ollama. ArtifactStagingService deletes the
    // shim, so this resolver returns the inner path directly rather than
    // walking arbitrary subdirectories — that walk is what let the shim win
    // pre-MAC26.
    internal static string? ResolveMacOllamaExe(string ollamaDir)
    {
        if (!Directory.Exists(ollamaDir)) return null;
        var inner = Path.Combine(ollamaDir, "Ollama.app", "Contents", "Resources", "ollama");
        return File.Exists(inner) ? inner : null;
    }

    private static void ExtractOllamaZip(string zipPath, string destination)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException($"Ollama ZIP not found at {zipPath}");

        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);

        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(zipPath, destination, overwriteFiles: true);
    }
}
