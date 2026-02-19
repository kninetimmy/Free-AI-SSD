namespace FreeAiSsd.Shared.Services;

public interface IPrereqService
{
    Task StagePrerequisitesAsync(string root, Action<string> onLog, CancellationToken ct);
    Task UpdatePrereqsOnlineAsync(string root, Action<string> onLog, CancellationToken ct);
}
