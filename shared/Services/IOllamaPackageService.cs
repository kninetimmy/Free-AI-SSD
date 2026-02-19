namespace FreeAiSsd.Shared.Services;

public interface IOllamaPackageService
{
    Task<string> EnsureOllamaReadyAsync(string root, string ollamaUrl, Action<string> onLog, IProgress<DownloadProgress>? progress, CancellationToken ct);
    string? ResolveOllamaExe(string ollamaDir);
}
