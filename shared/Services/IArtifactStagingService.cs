namespace FreeAiSsd.Shared.Services;

public interface IArtifactStagingService
{
    Task StageRunnerAsync(string ssdRoot, Action<string> onLog);
    Task StageMacRunnerAsync(string ssdRoot, Action<string> onLog, CancellationToken ct);
    Task StageMacOllamaAsync(string ssdRoot, Action<string> onLog, CancellationToken ct);
    bool AreMacArtifactsAvailable(out string? problem);
}
