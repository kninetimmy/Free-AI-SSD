using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class ModelService : IModelService
{
    private readonly ModelOperations _modelOperations = new();

    public async Task<PortableConfig> LoadConfigAsync(string configPath)
        => await PortableConfig.LoadAsync(configPath);

    public async Task SaveConfigAsync(string configPath, PortableConfig config)
        => await config.SaveAsync(configPath);

    public void UpsertModel(List<ModelConfigEntry> models, string name, ModelInstallStatus status)
    {
        var model = models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            models.Add(new ModelConfigEntry { Name = name, Status = status });
            return;
        }
        model.Status = status;
    }

    public async Task UpdateModelStatusAsync(string configPath, string modelName, ModelInstallStatus status,
        string? sha256 = null, long? sizeBytes = null, DateTime? lastVerifiedUtc = null)
    {
        var config = await PortableConfig.LoadAsync(configPath);
        var model = config.Models.FirstOrDefault(m => string.Equals(m.Name, modelName, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            model = new ModelConfigEntry { Name = modelName };
            config.Models.Add(model);
        }

        model.Status = status;
        model.Sha256 = sha256;
        model.SizeBytes = sizeBytes;
        model.LastVerifiedUtc = lastVerifiedUtc;
        await config.SaveAsync(configPath);
    }

    public IReadOnlyCollection<string> DiscoverModelsOnDisk(string modelsRoot)
        => ModelOperations.DiscoverModelsOnDisk(modelsRoot);

    public async Task<ModelPullResult> PullModelAsync(string ollamaExe, string modelsRoot, string modelTag,
        Action<string> onLog, CancellationToken ct)
    {
        var result = await _modelOperations.PullModelAsync(ollamaExe, modelsRoot, modelTag, onLog, ct);
        return new ModelPullResult(result.Sha256, result.SizeBytes);
    }

    public async Task<bool> VerifyModelAsync(string modelsRoot, string modelTag, string expectedHash,
        Action<string> onLog, CancellationToken ct)
        => await _modelOperations.VerifyModelAsync(modelsRoot, modelTag, expectedHash, onLog, ct);

    public async Task DeleteModelAsync(string ollamaExe, string modelsRoot, string modelTag,
        Action<string> onLog, CancellationToken ct)
        => await _modelOperations.DeleteModelAsync(ollamaExe, modelsRoot, modelTag, onLog, ct);

    public List<string> GetSizingWarnings(string modelTag, int? freeDiskGb, int? systemRamGb, int? gpuVramGb)
    {
        var sizing = ModelSizingCatalog.Suggest(modelTag);
        var warnings = new List<string>();

        if (systemRamGb.HasValue && systemRamGb.Value < sizing.RecommendedSystemRamGb)
            warnings.Add($"Low RAM: model recommends {sizing.RecommendedSystemRamGb} GB");

        if (sizing.RecommendedVramGb.HasValue)
        {
            if (!gpuVramGb.HasValue)
                warnings.Add($"VRAM unknown: model recommends {sizing.RecommendedVramGb.Value} GB (may run on CPU)");
            else if (gpuVramGb.Value < sizing.RecommendedVramGb.Value)
                warnings.Add($"Low VRAM: model recommends {sizing.RecommendedVramGb.Value} GB (may run on CPU)");
        }

        if (freeDiskGb.HasValue && freeDiskGb.Value < sizing.ApproxDiskGb)
            warnings.Add($"Low disk space: needs ~{sizing.ApproxDiskGb} GB");

        return warnings;
    }

    public List<string> BuildPullSelectionWarnings(IReadOnlyList<string> models, string rootPath,
        int? systemRamGb, int? gpuVramGb)
    {
        var warnings = new List<string>();
        var freeDiskGb = SystemResources.GetFreeDiskSpaceGb(rootPath);
        var estimatedDiskGb = 0;

        foreach (var model in models)
        {
            var modelWarnings = GetSizingWarnings(model, freeDiskGb, systemRamGb, gpuVramGb);
            if (modelWarnings.Count > 0)
                warnings.Add($"{model}: {string.Join("; ", modelWarnings)}");
            estimatedDiskGb += ModelSizingCatalog.Suggest(model).ApproxDiskGb;
        }

        if (freeDiskGb.HasValue && freeDiskGb.Value < estimatedDiskGb)
            warnings.Add($"Selection total needs ~{estimatedDiskGb} GB, but only ~{freeDiskGb.Value} GB is free.");

        return warnings;
    }
}
