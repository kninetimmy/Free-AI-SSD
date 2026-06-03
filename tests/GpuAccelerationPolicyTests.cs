using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public class GpuAccelerationPolicyTests
{
    [Fact]
    public void ResolveFor_Nvidia_SetsNoEnvVars()
    {
        var decision = GpuAccelerationPolicy.ResolveFor(GpuVendor.Nvidia);
        Assert.Empty(decision.EnvironmentVariables);
        Assert.Contains("NVIDIA", decision.BackendDescription);
    }

    [Fact]
    public void ResolveFor_Amd_EnablesVulkan()
    {
        // No ROCm runtime ships in the bundled Ollama on Windows, so AMD runs on Vulkan
        // (Ollama 0.30.x default-on); we set it explicitly to make the intent durable.
        var decision = GpuAccelerationPolicy.ResolveFor(GpuVendor.Amd);
        Assert.True(decision.EnvironmentVariables.TryGetValue("OLLAMA_VULKAN", out var value));
        Assert.Equal("1", value);
        Assert.Contains("Vulkan", decision.BackendDescription);
    }

    [Fact]
    public void ResolveFor_CpuMode_HidesGpuFromEveryBackend()
    {
        // Even with a GPU present, PreferredCompute="cpu" must hide it from CUDA, ROCm,
        // and Vulkan so Ollama loads on CPU.
        var decision = GpuAccelerationPolicy.ResolveFor(GpuVendor.Amd, "cpu");
        Assert.Equal("-1", decision.EnvironmentVariables["CUDA_VISIBLE_DEVICES"]);
        Assert.Equal("-1", decision.EnvironmentVariables["HIP_VISIBLE_DEVICES"]);
        Assert.Equal("-1", decision.EnvironmentVariables["ROCR_VISIBLE_DEVICES"]);
        Assert.Equal("-1", decision.EnvironmentVariables["GGML_VK_VISIBLE_DEVICES"]);
        Assert.False(decision.EnvironmentVariables.ContainsKey("OLLAMA_VULKAN"));
        Assert.Contains("CPU", decision.BackendDescription);
    }

    [Fact]
    public void ResolveFor_CpuMode_IsCaseInsensitiveAndTrimmed()
    {
        var decision = GpuAccelerationPolicy.ResolveFor(GpuVendor.Nvidia, "  CPU ");
        Assert.Equal("-1", decision.EnvironmentVariables["CUDA_VISIBLE_DEVICES"]);
        Assert.Contains("CPU", decision.BackendDescription);
    }

    [Fact]
    public void ResolveFor_AutoMode_UsesVendorGpu()
    {
        // "auto" (and any non-cpu value) selects the vendor backend, not CPU.
        var decision = GpuAccelerationPolicy.ResolveFor(GpuVendor.Nvidia, "auto");
        Assert.Empty(decision.EnvironmentVariables);
        Assert.Contains("NVIDIA", decision.BackendDescription);
    }

    [Fact]
    public void ResolveFor_Intel_EnablesVulkan()
    {
        var decision = GpuAccelerationPolicy.ResolveFor(GpuVendor.Intel);
        Assert.True(decision.EnvironmentVariables.TryGetValue("OLLAMA_VULKAN", out var value));
        Assert.Equal("1", value);
        Assert.Contains("Vulkan", decision.BackendDescription);
    }

    [Fact]
    public void ResolveFor_None_SetsNoEnvVarsAndReportsCpu()
    {
        var decision = GpuAccelerationPolicy.ResolveFor(GpuVendor.None);
        Assert.Empty(decision.EnvironmentVariables);
        Assert.Contains("CPU", decision.BackendDescription);
    }

    [Fact]
    public void ResolveFor_Other_FallsBackToCpuWithNoEnvVars()
    {
        var decision = GpuAccelerationPolicy.ResolveFor(GpuVendor.Other);
        Assert.Empty(decision.EnvironmentVariables);
        Assert.Contains("CPU", decision.BackendDescription);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", "NVIDIA", GpuVendor.Nvidia)]
    [InlineData("AMD Radeon RX 7900 XTX", "Advanced Micro Devices, Inc.", GpuVendor.Amd)]
    [InlineData("Radeon (TM) Graphics", "", GpuVendor.Amd)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Intel Corporation", GpuVendor.Intel)]
    [InlineData("Intel(R) UHD Graphics 770", "Intel Corporation", GpuVendor.Intel)]
    [InlineData("Microsoft Basic Display Adapter", "(Standard display types)", GpuVendor.Other)]
    [InlineData("", "", GpuVendor.None)]
    public void ClassifyVendor_RecognizesCommonControllerNames(string name, string adapter, GpuVendor expected)
    {
        Assert.Equal(expected, SystemResources.ClassifyVendor(name, adapter));
    }
}
