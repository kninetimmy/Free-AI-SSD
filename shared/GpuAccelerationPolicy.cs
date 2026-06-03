namespace FreeAiSsd.Shared;

/// <summary>
/// Classification of the host GPU for Ollama acceleration purposes.
/// </summary>
public enum GpuVendor
{
    None,
    Nvidia,
    Amd,
    Intel,
    Other
}

/// <summary>
/// Outcome of <see cref="GpuAccelerationPolicy.ResolveFor"/>.
/// </summary>
/// <param name="EnvironmentVariables">Env vars to set on the Ollama process before launch.
/// Empty when no override is needed (Ollama auto-detects, or no GPU is present).</param>
/// <param name="BackendDescription">Short human-readable label for log/UI surface,
/// e.g. "NVIDIA (CUDA, auto)", "Intel (Vulkan)", "CPU".</param>
public readonly record struct GpuAccelerationDecision(
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string BackendDescription);

/// <summary>
/// Maps a detected <see cref="GpuVendor"/> plus the user's preferred compute mode to
/// the env vars Ollama needs at launch.
///
/// When the user selects CPU (<c>PreferredCompute == "cpu"</c>) we hide every GPU from
/// all backends so Ollama loads on CPU — the stable escape hatch when a GPU/driver
/// combination is unreliable. Any other value (<c>"auto"</c>, empty, or the legacy
/// <c>"cuda"/"rocm"</c>) selects the vendor's GPU backend.
///
/// On Windows the bundled Ollama ships no ROCm runtime, so AMD GPUs run on Vulkan,
/// which Ollama 0.30.x enables by default; we set <c>OLLAMA_VULKAN=1</c> explicitly to
/// make that intent durable. NVIDIA uses CUDA auto-detection (no env). Intel uses Vulkan.
/// </summary>
public static class GpuAccelerationPolicy
{
    /// <summary>Compute-mode token (case-insensitive) that forces CPU-only inference.</summary>
    public const string CpuComputeMode = "cpu";

    /// <param name="vendor">The detected GPU vendor.</param>
    /// <param name="preferredCompute">
    /// <see cref="PortableConfig.PreferredCompute"/>. <c>"cpu"</c> forces CPU; anything else
    /// (including <c>null</c>/<c>"auto"</c>) uses the vendor's GPU backend.
    /// </param>
    public static GpuAccelerationDecision ResolveFor(GpuVendor vendor, string? preferredCompute = null)
    {
        if (string.Equals(preferredCompute?.Trim(), CpuComputeMode, StringComparison.OrdinalIgnoreCase))
        {
            return new GpuAccelerationDecision(CpuOnlyEnvironment(), "CPU (forced by setting)");
        }

        return vendor switch
        {
            GpuVendor.Nvidia => new GpuAccelerationDecision(
                new Dictionary<string, string>(),
                "NVIDIA (CUDA, auto-detected)"),
            GpuVendor.Amd => new GpuAccelerationDecision(
                new Dictionary<string, string> { ["OLLAMA_VULKAN"] = "1" },
                "AMD (Vulkan)"),
            GpuVendor.Intel => new GpuAccelerationDecision(
                new Dictionary<string, string> { ["OLLAMA_VULKAN"] = "1" },
                "Intel (Vulkan)"),
            GpuVendor.Other => new GpuAccelerationDecision(
                new Dictionary<string, string>(),
                "Unknown GPU (CPU fallback)"),
            _ => new GpuAccelerationDecision(
                new Dictionary<string, string>(),
                "CPU (no GPU detected)"),
        };
    }

    /// <summary>
    /// Env that hides every GPU device from each backend, so Ollama's scheduler reports
    /// only the CPU device. Verified against bundled Ollama 0.30.2 on an RX 9070 XT
    /// (Vulkan): the scheduler then logs <c>inference compute id=cpu library=cpu</c>.
    /// </summary>
    private static Dictionary<string, string> CpuOnlyEnvironment() => new()
    {
        ["CUDA_VISIBLE_DEVICES"] = "-1",
        ["HIP_VISIBLE_DEVICES"] = "-1",
        ["ROCR_VISIBLE_DEVICES"] = "-1",
        ["GGML_VK_VISIBLE_DEVICES"] = "-1",
    };
}
