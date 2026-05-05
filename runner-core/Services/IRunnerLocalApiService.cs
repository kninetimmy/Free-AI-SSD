using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

public interface IRunnerLocalApiService : IAsyncDisposable
{
    event Action<string>? LogMessage;

    bool IsRunning { get; }
    string? CurrentBaseUrl { get; }

    Task StartAsync(PortableConfig config, string ollamaHost, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
