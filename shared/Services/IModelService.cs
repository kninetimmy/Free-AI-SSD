namespace FreeAiSsd.Shared.Services;

public sealed record ModelPullResult(string Sha256, long SizeBytes);

public interface IModelService
{
    Task<PortableConfig> LoadConfigAsync(string configPath);
    Task SaveConfigAsync(string configPath, PortableConfig config);
    void UpsertModel(List<ModelConfigEntry> models, string name, ModelInstallStatus status);
    Task UpdateModelStatusAsync(string configPath, string modelName, ModelInstallStatus status, string? sha256 = null, long? sizeBytes = null, DateTime? lastVerifiedUtc = null);
    IReadOnlyCollection<string> DiscoverModelsOnDisk(string modelsRoot);
    Task<ModelPullResult> PullModelAsync(string ollamaExe, string modelsRoot, string modelTag, Action<string> onLog, CancellationToken ct, string? ollamaHost = null);
    Task<bool> VerifyModelAsync(string modelsRoot, string modelTag, string expectedHash, Action<string> onLog, CancellationToken ct);
    Task DeleteModelAsync(string ollamaExe, string modelsRoot, string modelTag, Action<string> onLog, CancellationToken ct, string? ollamaHost = null);
    List<string> GetSizingWarnings(string modelTag, int? freeDiskGb, int? systemRamGb, int? gpuVramGb);
    List<string> BuildPullSelectionWarnings(IReadOnlyList<string> models, string rootPath, int? systemRamGb, int? gpuVramGb);
}
