using System.Net.Http.Json;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

public sealed class ModelManagementService : IModelManagementService
{
    private readonly HttpClient _http;

    public ModelManagementService(HttpClient http)
    {
        _http = http;
    }

    public event Action<string>? LogMessage;

    public List<string> GetInstalledModelNames(PortableConfig config)
    {
        return config.Models
            .Where(m => m.Status == ModelInstallStatus.Installed)
            .Select(m => m.Name)
            .ToList();
    }

    public List<string> GetModelSizingWarnings(PortableConfig config)
    {
        var ramGb = SystemResources.GetTotalSystemRamGb();
        var vramGb = SystemResources.GetGpuVramGb();
        var warnings = new List<string>();

        foreach (var model in config.Models.Where(m => m.Status == ModelInstallStatus.Installed))
        {
            var sizing = ModelSizingCatalog.Suggest(model.Name);
            var reasons = new List<string>();

            if (ramGb.HasValue && ramGb.Value < sizing.RecommendedSystemRamGb)
            {
                reasons.Add($"RAM {ramGb.Value} GB < recommended {sizing.RecommendedSystemRamGb} GB");
            }

            if (sizing.RecommendedVramGb.HasValue)
            {
                if (!vramGb.HasValue)
                {
                    reasons.Add($"VRAM unknown; recommends {sizing.RecommendedVramGb.Value} GB (may run on CPU)");
                }
                else if (vramGb.Value < sizing.RecommendedVramGb.Value)
                {
                    reasons.Add($"VRAM {vramGb.Value} GB < recommended {sizing.RecommendedVramGb.Value} GB (may run on CPU)");
                }
            }

            if (reasons.Count > 0)
            {
                warnings.Add($"{model.Name}: {string.Join("; ", reasons)}");
            }
        }

        return warnings;
    }

    public bool IsSizingWarningDismissed(string ssdRoot)
    {
        var statePath = Path.Combine(ssdRoot, SsdLayout.Config, "runner-first-run.json");
        var state = RunnerFirstRunState.Load(statePath);
        return state.SizingWarningDismissed;
    }

    public async Task DismissSizingWarningAsync(string ssdRoot)
    {
        var statePath = Path.Combine(ssdRoot, SsdLayout.Config, "runner-first-run.json");
        var state = RunnerFirstRunState.Load(statePath);
        state.SizingWarningDismissed = true;
        state.LastCheckedUtc = DateTime.UtcNow;
        await state.SaveAsync(statePath);
    }

    public async Task<bool> PullEmbeddingModelAsync(string host, string modelName)
    {
        try
        {
            var request = new { name = modelName, stream = false };
            using var response = await _http.PostAsJsonAsync($"http://{host}/api/pull", request);
            response.EnsureSuccessStatusCode();
            LogMessage?.Invoke($"Pulled embedding model: {modelName}");
            return true;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Embedding model pull failed: {ex.Message}");
            return false;
        }
    }
}
