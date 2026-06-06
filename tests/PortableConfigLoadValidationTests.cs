using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// #115/#9: LoadWithValidation must report corruption via IsValid=false (so
/// write-adjacent callers don't persist over a config they couldn't load) while
/// only swallowing genuine "unreadable/corrupt" failures — unexpected exceptions
/// propagate rather than silently degrading real settings to all-defaults.
public sealed class PortableConfigLoadValidationTests : IDisposable
{
    private readonly string _dir;

    public PortableConfigLoadValidationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"pcfg-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void LoadWithValidation_MissingFile_ReturnsDefaultsInvalid()
    {
        var (config, isValid) = PortableConfig.LoadWithValidation(Path.Combine(_dir, "nope.json"));

        Assert.NotNull(config);
        Assert.False(isValid);
    }

    [Theory]
    [InlineData("{ this is not valid json")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void LoadWithValidation_CorruptJson_ReturnsDefaultsInvalid_DoesNotThrow(string contents)
    {
        var path = Path.Combine(_dir, "corrupt.json");
        File.WriteAllText(path, contents);

        var (config, isValid) = PortableConfig.LoadWithValidation(path);

        Assert.NotNull(config);
        Assert.False(isValid);
        // Degraded to defaults, but the caller can see it via IsValid and refuse to save.
        Assert.Equal(new PortableConfig().NetworkBindAddress, config.NetworkBindAddress);
    }

    [Fact]
    public void LoadWithValidation_ValidConfig_RoundTripsAndReportsValid()
    {
        var path = Path.Combine(_dir, "good.json");
        // Save() uses the same serializer LoadWithValidation reads with. RetrievalTopK=12
        // is non-default (default 8), so a true round-trip is observable.
        new PortableConfig { RetrievalTopK = 12 }.Save(path);

        var (config, isValid) = PortableConfig.LoadWithValidation(path);

        Assert.True(isValid);
        Assert.Equal(12, config.RetrievalTopK);
    }
}
