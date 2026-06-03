using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Shared.Services;

public interface IReadinessService
{
    Task<List<ReadinessItem>> RunReadinessChecksAsync(string root, Action<string> onLog, CancellationToken ct);

    /// <summary>
    /// Cheap, presence-only inspection used by the PrepApp launch-time
    /// "resume setup" prompt (#2). No decrypt, no SHA hashing — see
    /// <c>SsdSetupCompletionProbe</c>. Safe on any candidate drive.
    /// </summary>
    SsdSetupCompletionResult InspectSetupCompletion(string root);
}
