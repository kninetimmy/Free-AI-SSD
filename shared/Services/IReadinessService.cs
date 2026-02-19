using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Shared.Services;

public interface IReadinessService
{
    Task<List<ReadinessItem>> RunReadinessChecksAsync(string root, Action<string> onLog, CancellationToken ct);
}
