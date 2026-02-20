using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

public record OllamaStartResult(bool Success, string? ErrorMessage = null);

public interface IOllamaLifecycleService : IDisposable
{
    bool IsRunning { get; }
    int? CurrentPort { get; }
    string? CurrentHost { get; }

    event Action<string>? LogMessage;
    event Action? ProcessExited;

    /// <summary>
    /// Validates the Ollama package trust attestation before starting.
    /// </summary>
    (bool IsTrusted, string Message) ValidateTrust(string ssdRoot);

    /// <summary>
    /// Resolves a free port and starts the Ollama server process with
    /// SSD-relative environment variables. Call <see cref="ValidateTrust"/>
    /// before this to preserve the original check ordering.
    /// </summary>
    OllamaStartResult Start(PortableConfig config, string ssdRoot);

    /// <summary>
    /// Kills the running Ollama server process and cleans up.
    /// </summary>
    void Stop();
}
