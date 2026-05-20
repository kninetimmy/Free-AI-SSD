namespace FreeAiSsd.Shared.Services;

public sealed record ModelPullResult(string Sha256, long SizeBytes);

public interface IModelService
{
    Task<PortableConfig> LoadConfigAsync(string configPath);
    Task SaveConfigAsync(string configPath, PortableConfig config);
    void UpsertModel(List<ModelConfigEntry> models, string name, ModelInstallStatus status);
    Task UpdateModelStatusAsync(string configPath, string modelName, ModelInstallStatus status, string? sha256 = null, long? sizeBytes = null, DateTime? lastVerifiedUtc = null);
    IReadOnlyCollection<string> DiscoverModelsOnDisk(string modelsRoot);
    /// <summary>
    /// Pulls <paramref name="modelTag"/> via the running Ollama server at
    /// <paramref name="ollamaHost"/>, then computes the model blob's
    /// SHA-256 for integrity verification.
    /// <paramref name="onFinalize"/> (task #48) fires once the NDJSON
    /// stream completes but before the multi-second SHA compute, giving
    /// the UI a hook to swap into an explicit "Finalizing…" state so the
    /// hash gap doesn't read as a hang on large (12B+) models.
    /// </summary>
    Task<ModelPullResult> PullModelAsync(string ollamaExe, string modelsRoot, string modelTag, Action<string> onLog, CancellationToken ct, string? ollamaHost = null, Action<OllamaPullProgress>? onProgress = null, Action? onFinalize = null);
    /// <summary>
    /// MAC31: estimates the fraction (0.0–1.0) of <paramref name="modelTag"/>'s
    /// expected blob payload already on disk under <paramref name="modelsRoot"/>.
    /// Used to seed the PrepApp's pull progress display so a retry after a
    /// cancelled or interrupted pull surfaces "Resuming from NN%..." instead
    /// of starting at 0% (Ollama IS resumable; we just weren't surfacing it).
    /// Returns 0.0 if no manifest exists, the manifest is malformed, or the
    /// blobs directory is missing — the pull still works in those cases.
    /// </summary>
    double EstimatePartialPullProgress(string modelsRoot, string modelTag);
    Task<bool> VerifyModelAsync(string modelsRoot, string modelTag, string expectedHash, Action<string> onLog, CancellationToken ct);
    Task DeleteModelAsync(string ollamaExe, string modelsRoot, string modelTag, Action<string> onLog, CancellationToken ct, string? ollamaHost = null);
    List<string> GetSizingWarnings(string modelTag, int? freeDiskGb, int? systemRamGb, int? gpuVramGb);
    List<string> BuildPullSelectionWarnings(IReadOnlyList<string> models, string rootPath, int? systemRamGb, int? gpuVramGb);
}
