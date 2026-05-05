using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner.Services;

public sealed class WindowsSystemResourceProbe : ISystemResourceProbe
{
    public int? GetTotalSystemRamGb() => SystemResources.GetTotalSystemRamGb();

    public int? GetGpuVramGb() => SystemResources.GetGpuVramGb();
}
