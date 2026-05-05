namespace FreeAiSsd.Runner.Services;

public interface ISystemResourceProbe
{
    int? GetTotalSystemRamGb();
    int? GetGpuVramGb();
}

public sealed class UnknownSystemResourceProbe : ISystemResourceProbe
{
    public static UnknownSystemResourceProbe Instance { get; } = new();

    private UnknownSystemResourceProbe()
    {
    }

    public int? GetTotalSystemRamGb() => null;

    public int? GetGpuVramGb() => null;
}
