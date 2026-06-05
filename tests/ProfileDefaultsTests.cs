using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

public sealed class ProfileDefaultsTests
{
    [Fact]
    public void FlightSim_EnablesPtt()
    {
        var config = new PortableConfig();
        ProfileDefaults.Apply(config, UserProfile.FlightSim);
        Assert.True(config.PttEnabled);
    }

    [Fact]
    public void FlightSim_EnablesTts()
    {
        var config = new PortableConfig();
        ProfileDefaults.Apply(config, UserProfile.FlightSim);
        Assert.True(config.TtsEnabled);
    }

    [Fact]
    public void FlightSim_EnablesAutoSendVoiceInput()
    {
        var config = new PortableConfig();
        ProfileDefaults.Apply(config, UserProfile.FlightSim);
        Assert.True(config.AutoSendVoiceInput);
    }

    [Fact]
    public void FlightSim_EnablesPttOverlayAndActivationSound()
    {
        var config = new PortableConfig();
        ProfileDefaults.Apply(config, UserProfile.FlightSim);
        Assert.True(config.PttOverlayEnabled);
        Assert.True(config.PttActivationSoundEnabled);
    }

    [Fact]
    public void FlightSim_EnablesRemoteVoiceQuery()
    {
        // The VR companion's only protocol is /api/voice/query, gated off by
        // default. A FlightSim drive bundles the companion, so the runner must
        // accept remote voice queries or the companion 403s out of the box.
        var config = new PortableConfig();
        Assert.False(config.NetworkAllowRemoteVoiceQuery);
        ProfileDefaults.Apply(config, UserProfile.FlightSim);
        Assert.True(config.NetworkAllowRemoteVoiceQuery);
    }

    [Fact]
    public void FlightSim_DoesNotEnableRemoteRawStt()
    {
        // voice/query is the only endpoint the companion uses; raw remote STT
        // stays off (no need to widen that surface).
        var config = new PortableConfig();
        ProfileDefaults.Apply(config, UserProfile.FlightSim);
        Assert.False(config.NetworkAllowRemoteStt);
    }

    [Fact]
    public void GeneralAssistant_DisablesPtt()
    {
        var config = new PortableConfig { PttEnabled = true };
        ProfileDefaults.Apply(config, UserProfile.GeneralAssistant);
        Assert.False(config.PttEnabled);
    }

    [Fact]
    public void GeneralAssistant_DisablesTts()
    {
        var config = new PortableConfig { TtsEnabled = true };
        ProfileDefaults.Apply(config, UserProfile.GeneralAssistant);
        Assert.False(config.TtsEnabled);
    }

    [Fact]
    public void GeneralAssistant_DisablesAutoSendVoiceInput()
    {
        var config = new PortableConfig();
        ProfileDefaults.Apply(config, UserProfile.GeneralAssistant);
        Assert.False(config.AutoSendVoiceInput);
    }

    [Fact]
    public void GeneralAssistant_DisablesPttOverlayAndActivationSound()
    {
        var config = new PortableConfig
        {
            PttOverlayEnabled = true,
            PttActivationSoundEnabled = true
        };
        ProfileDefaults.Apply(config, UserProfile.GeneralAssistant);
        Assert.False(config.PttOverlayEnabled);
        Assert.False(config.PttActivationSoundEnabled);
    }

    [Fact]
    public async Task ActiveProfile_RoundTripsAsFlightSim()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var path = Path.Combine(root, SsdLayout.Config, "portable-config.json");

            var original = new PortableConfig { ActiveProfile = UserProfile.FlightSim };
            await original.SaveAsync(path);

            var loaded = await PortableConfig.LoadAsync(path);
            Assert.Equal(UserProfile.FlightSim, loaded.ActiveProfile);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task ActiveProfile_RoundTripsAsGeneralAssistant()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var path = Path.Combine(root, SsdLayout.Config, "portable-config.json");

            var original = new PortableConfig { ActiveProfile = UserProfile.GeneralAssistant };
            await original.SaveAsync(path);

            var loaded = await PortableConfig.LoadAsync(path);
            Assert.Equal(UserProfile.GeneralAssistant, loaded.ActiveProfile);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    [Fact]
    public async Task ActiveProfile_NullByDefault_RoundTrips()
    {
        var root = CreateTempRoot();
        try
        {
            SsdLayout.EnsureStructure(root);
            var path = Path.Combine(root, SsdLayout.Config, "portable-config.json");

            var original = new PortableConfig();
            Assert.Null(original.ActiveProfile);

            await original.SaveAsync(path);
            var loaded = await PortableConfig.LoadAsync(path);
            Assert.Null(loaded.ActiveProfile);
        }
        finally
        {
            CleanupTempRoot(root);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "free-ai-ssd-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CleanupTempRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
