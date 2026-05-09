using System.IO.Compression;
using System.Net.Http;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Prereqs;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class OllamaPackageService : IOllamaPackageService
{
    private readonly DownloadManager _downloadManager = new();

    public async Task<string> EnsureOllamaReadyAsync(string root, Action<string> onLog,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        SsdLayout.EnsureStructure(root);

        var ollamaDir = Path.Combine(root, SsdLayout.Ollama);
        var ollamaExe = ResolveOllamaExe(ollamaDir);
        if (ollamaExe is not null)
        {
            // Already staged: re-validate the on-SSD attestation and short-circuit
            // when it's still trusted. A failing attestation forces a re-download.
            var executionGate = OllamaPackageTrustPolicy.ValidateExecutionAttestation(root);
            if (executionGate.IsTrusted) return ollamaExe;
            onLog($"Existing Ollama attestation rejected ({executionGate.Message}); re-staging.");
        }

        // MAC38: resolve the latest Ollama release dynamically + verify its
        // bytes against the upstream sha256sum.txt. There is no static URL or
        // hash pin in this repo for Ollama anymore.
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FreeAiSsd-PrepApp/1.0 (+https://github.com/kninetimmy/free-ai-ssd)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var resolution = await PrereqResolver.ResolveLatestOllamaWindowsAsync(http, ct);
        onLog($"Resolved Ollama {resolution.Version} from {resolution.Url} ({resolution.TrustNote}).");

        var sourceValidation = OllamaPackageTrustPolicy.ValidatePackageSource(resolution.Url);
        if (!sourceValidation.IsTrusted)
            throw new InvalidOperationException(sourceValidation.Message);

        var ollamaZipPath = Path.Combine(root, SsdLayout.Cache, "ollama-windows-amd64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(ollamaZipPath)!);

        onLog("Downloading Ollama package...");
        await _downloadManager.DownloadFileWithResumeAsync(
            new DownloadRequest(resolution.Url, ollamaZipPath),
            progress ?? new Progress<DownloadProgress>(),
            ct);

        var resolvedMetadata = new OllamaPackageMetadata(resolution.Version, resolution.Url, resolution.Hash!);
        var digestValidation = OllamaPackageTrustPolicy.ValidateDownloadedPackage(ollamaZipPath, resolvedMetadata);
        if (!digestValidation.IsTrusted)
            throw new InvalidOperationException(digestValidation.Message);

        ExtractOllamaZip(ollamaZipPath, ollamaDir);
        OllamaPackageTrustPolicy.WriteTrustAttestation(root, resolvedMetadata);
        onLog($"Ollama {resolution.Version} staged.");

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
