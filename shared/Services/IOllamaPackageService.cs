namespace FreeAiSsd.Shared.Services;

/// <summary>
/// A disposable handle to a temporary Ollama server process.
/// Disposing the handle stops the server and cleans up the process.
/// </summary>
public interface IOllamaServerHandle : IDisposable
{
    /// <summary>The "127.0.0.1:port" host string to pass as OLLAMA_HOST to CLI commands.</summary>
    string Host { get; }
}

public interface IOllamaPackageService
{
    Task<string> EnsureOllamaReadyAsync(string root, string ollamaUrl, Action<string> onLog, IProgress<DownloadProgress>? progress, CancellationToken ct);
    string? ResolveOllamaExe(string ollamaDir);

    /// <summary>
    /// Starts a temporary Ollama server on a random port for the duration of model
    /// operations. This prevents Ollama CLI from auto-starting an uncontrolled server
    /// that can interfere with the host system (opens tray icons, persists after exit).
    /// The returned handle must be disposed to stop the server.
    /// </summary>
    Task<IOllamaServerHandle> StartTemporaryServerAsync(string ollamaExe, string modelsRoot, Action<string> onLog, CancellationToken ct);
}
