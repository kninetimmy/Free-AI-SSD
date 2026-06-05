using FreeAiSsd.Shared.Discovery;

namespace FreeAiSsd.Tests;

public sealed class RunnerBeaconTests
{
    [Fact]
    public void Serialize_Then_TryParse_RoundTrips()
    {
        var beacon = new RunnerBeacon("DCS-HOST", 41555, "abc123");
        var ok = RunnerBeacon.TryParse(beacon.Serialize(), out var parsed);

        Assert.True(ok);
        Assert.NotNull(parsed);
        Assert.Equal("DCS-HOST", parsed!.Name);
        Assert.Equal(41555, parsed.Port);
        Assert.Equal("abc123", parsed.Fingerprint);
        Assert.Equal(RunnerBeacon.ServiceTag, parsed.Service);
        Assert.Equal(RunnerBeacon.CurrentVersion, parsed.Version);
    }

    [Fact]
    public void TryParse_Rejects_NonJson()
    {
        Assert.False(RunnerBeacon.TryParse(new byte[] { 1, 2, 3, 4 }, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParse_Rejects_ForeignService()
    {
        var json = """{"service":"some-other-app","v":1,"name":"x","port":41555,"fp":""}"""u8;
        Assert.False(RunnerBeacon.TryParse(json, out _));
    }

    [Fact]
    public void TryParse_Rejects_UnknownVersion()
    {
        var json = """{"service":"freeaissd-runner","v":99,"name":"x","port":41555,"fp":""}"""u8;
        Assert.False(RunnerBeacon.TryParse(json, out _));
    }

    [Fact]
    public void TryParse_Rejects_OutOfRangePort()
    {
        var json = """{"service":"freeaissd-runner","v":1,"name":"x","port":0,"fp":""}"""u8;
        Assert.False(RunnerBeacon.TryParse(json, out _));
    }

    [Fact]
    public void ComputeFingerprint_IsDeterministic_AndKeyDependent()
    {
        Assert.Equal(RunnerBeacon.ComputeFingerprint("secret"), RunnerBeacon.ComputeFingerprint("secret"));
        Assert.NotEqual(RunnerBeacon.ComputeFingerprint("secret"), RunnerBeacon.ComputeFingerprint("other"));
    }

    [Fact]
    public void ComputeFingerprint_DoesNotLeakKey_AndIsEmptyForNoKey()
    {
        var fp = RunnerBeacon.ComputeFingerprint("super-secret-key");
        Assert.DoesNotContain("super-secret-key", fp);
        Assert.Equal(string.Empty, RunnerBeacon.ComputeFingerprint(""));
        Assert.Equal(string.Empty, RunnerBeacon.ComputeFingerprint(null));
    }
}

public sealed class RunnerDiscoverySelectionTests
{
    private static RunnerDiscovery.DiscoveredRunner Runner(string host, string fp, int port = 41555)
        => new(host, port, $"name-{host}", fp);

    [Fact]
    public void SelectBestMatch_PrefersFingerprintMatch_OverOtherRunners()
    {
        var candidates = new[]
        {
            Runner("10.0.0.5", "wrongfp"),
            Runner("10.0.0.9", "myfp"),
        };

        var best = RunnerDiscovery.SelectBestMatch(candidates, "myfp");

        Assert.NotNull(best);
        Assert.Equal("10.0.0.9", best!.Host);
    }

    [Fact]
    public void SelectBestMatch_FallsBackToSoleRunner_WhenNoFingerprintMatch()
    {
        var candidates = new[] { Runner("10.0.0.5", "differentfp") };

        var best = RunnerDiscovery.SelectBestMatch(candidates, "myfp");

        Assert.NotNull(best);
        Assert.Equal("10.0.0.5", best!.Host);
    }

    [Fact]
    public void SelectBestMatch_ReturnsNull_WhenAmbiguous()
    {
        var candidates = new[]
        {
            Runner("10.0.0.5", "fpA"),
            Runner("10.0.0.6", "fpB"),
        };

        Assert.Null(RunnerDiscovery.SelectBestMatch(candidates, "myfp"));
    }

    [Fact]
    public void SelectBestMatch_ReturnsNull_WhenNoCandidates()
    {
        Assert.Null(RunnerDiscovery.SelectBestMatch(Array.Empty<RunnerDiscovery.DiscoveredRunner>(), "myfp"));
    }
}
