using System.Net.Http;
using System.Net.Http.Json;
using FreeAiSsd.PrepApp;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

public sealed class ModelManagementService : IModelManagementService
{
    private readonly HttpClient _http;
    private readonly ISystemResourceProbe _systemResources;
    private readonly string _ssdRoot;

    public ModelManagementService(HttpClient http, string ssdRoot)
        : this(http, UnknownSystemResourceProbe.Instance, ssdRoot)
    {
    }

    public ModelManagementService(HttpClient http, ISystemResourceProbe systemResources, string ssdRoot)
    {
        _http = http;
        _systemResources = systemResources;
        _ssdRoot = ssdRoot ?? throw new ArgumentNullException(nameof(ssdRoot));
    }

    public event Action<string>? LogMessage;

    // MAC33: Reads disk truth via ModelOperations.DiscoverModelsOnDisk because
    // the Mac sidecar can't write back to the encrypted config after pulls
    // (passphrase is zeroized before pulls run), so config.Models is empty
    // on a Mac-prepped SSD even when models are present on disk.
    public List<string> GetInstalledModelNames(PortableConfig config)
    {
        var modelsRoot = Path.Combine(_ssdRoot, SsdLayout.Models);
        return ModelOperations.DiscoverModelsOnDisk(modelsRoot)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsEmbeddingModelInstalled(PortableConfig config)
    {
        var wanted = NormalizeOllamaTag(config.EmbeddingModelName);
        if (string.IsNullOrEmpty(wanted))
        {
            return false;
        }

        return GetInstalledModelNames(config)
            .Any(name => string.Equals(NormalizeOllamaTag(name), wanted, StringComparison.OrdinalIgnoreCase));
    }

    // Ollama treats "name" and "name:latest" as the same model on disk and on the wire.
    // DiscoverModelsOnDisk emits "name:latest"; the user-facing PortableConfig default is the
    // bare "nomic-embed-text". Normalize both sides before comparing so the readiness check
    // doesn't read a present embedder as missing. (Mirrors PrepViewModel.NormalizeOllamaTag.)
    private static string NormalizeOllamaTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;
        var trimmed = tag.Trim();
        return trimmed.Contains(':', StringComparison.Ordinal) ? trimmed : $"{trimmed}:latest";
    }

    public List<string> GetModelSizingWarnings(PortableConfig config)
    {
        var ramGb = _systemResources.GetTotalSystemRamGb();
        var vramGb = _systemResources.GetGpuVramGb();
        var warnings = new List<string>();

        foreach (var modelName in GetInstalledModelNames(config))
        {
            var sizing = ModelSizingCatalog.Suggest(modelName);
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
                warnings.Add($"{modelName}: {string.Join("; ", reasons)}");
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
