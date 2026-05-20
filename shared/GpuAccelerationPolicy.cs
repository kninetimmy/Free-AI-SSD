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
/// Maps a detected <see cref="GpuVendor"/> to the env vars Ollama needs at launch.
///
/// Vanilla ollama auto-detects CUDA (NVIDIA) and ROCm (AMD) when the appropriate
/// drivers and runtime DLLs are present, so those paths set no env var. Vulkan is
/// experimental in upstream Ollama and gated behind <c>OLLAMA_VULKAN=1</c>; we set
/// it for Intel GPUs (Arc / iGPU) where it is the only viable backend.
/// </summary>
public static class GpuAccelerationPolicy
{
    public static GpuAccelerationDecision ResolveFor(GpuVendor vendor)
    {
        return vendor switch
        {
            GpuVendor.Nvidia => new GpuAccelerationDecision(
                new Dictionary<string, string>(),
                "NVIDIA (CUDA, auto-detected)"),
            GpuVendor.Amd => new GpuAccelerationDecision(
                new Dictionary<string, string>(),
                "AMD (ROCm, auto-detected)"),
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
}
