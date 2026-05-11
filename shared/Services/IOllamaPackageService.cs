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
    /// <summary>
    /// Ensures Ollama is staged on the SSD and ready to launch. On Windows
    /// this dynamically resolves the latest upstream release via GitHub's
    /// API + the release's <c>sha256sum.txt</c>, downloads, verifies, and
    /// writes the trust attestation. On Mac the binary is bundled into the
    /// release zip at CI time and copied from the bundle here.
    /// </summary>
    Task<string> EnsureOllamaReadyAsync(string root, Action<string> onLog, IProgress<DownloadProgress>? progress, CancellationToken ct);
    string? ResolveOllamaExe(string ollamaDir);

    /// <summary>
    /// Starts a temporary Ollama server on a random port for the duration of model
    /// operations. This prevents Ollama CLI from auto-starting an uncontrolled server
    /// that can interfere with the host system (opens tray icons, persists after exit).
    /// The returned handle must be disposed to stop the server.
    ///
    /// <paramref name="extraEnv"/> (C27 Stage 3): optional extra environment
    /// variables to inject into the server process. Used to pass
    /// <c>HF_TOKEN</c> / <c>HUGGING_FACE_HUB_TOKEN</c> for gated or private
    /// Hugging Face GGUF pulls; the server reads these at request time.
    /// Null or empty = no extra env. Existing <c>OLLAMA_MODELS</c> /
    /// <c>OLLAMA_HOST</c> bindings always win, so callers can't accidentally
    /// override those.
    /// </summary>
    Task<IOllamaServerHandle> StartTemporaryServerAsync(
        string ollamaExe,
        string modelsRoot,
        Action<string> onLog,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? extraEnv = null);
}
