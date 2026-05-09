using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

/// <summary>
/// Tests for <see cref="MacOllamaLifecycleService"/>. We deliberately avoid
/// launching real <c>ollama</c> processes — the suite has to run on Windows
/// CI runners where the macOS binary is absent. Coverage focuses on:
/// path resolution, refusal modes (missing binary, failing trust gate), and
/// the static <see cref="MacOllamaLifecycleService.BuildStartInfo"/> seam
/// that constructs the env-var + argument-array launch surface.
/// </summary>
public sealed class MacOllamaLifecycleServiceTests
{
    private const string SampleMacUrl =
        "https://github.com/ollama/ollama/releases/download/v0.20.0/Ollama-darwin.zip";
    private const string SampleWindowsUrl =
        "https://github.com/ollama/ollama/releases/download/v0.20.0/ollama-windows-amd64.zip";
    private const string SampleSha256 =
        "1111111111111111111111111111111111111111111111111111111111111111";

    private static OllamaPackageMetadata SampleMacMetadata() =>
        new("v0.20.0", SampleMacUrl, SampleSha256);

    private static OllamaPackageMetadata SampleWindowsMetadata() =>
        new("v0.20.0", SampleWindowsUrl, SampleSha256);

    [Fact]
    public void ResolveBinaryPath_LivesInsideOllamaAppBundle()
    {
        // MAC26: the macOS Ollama package is a GUI .app bundle; the
        // self-contained CLI server is at Ollama.app/Contents/Resources/ollama.
        // Pinning the inner-Resources suffix here so a regression that flips
        // back to the top-level LaunchServices shim trips this test.
        using var ssd = new TempSsdRoot();
        var binary = MacOllamaLifecycleService.ResolveBinaryPath(ssd.Root);
        var expectedSuffix = Path.Combine(SsdLayout.MacOllama, "Ollama.app", "Contents", "Resources", "ollama");
        Assert.EndsWith(expectedSuffix, binary);
        Assert.DoesNotContain(".exe", binary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStartInfo_BindsLoopbackAndModelsRoot()
    {
        var startInfo = MacOllamaLifecycleService.BuildStartInfo(
            ollamaBinary: Path.Combine("fake", "mac", "tools", "ollama", "ollama"),
            ssdRoot: "/Volumes/FreeAiSsd",
            port: 11434);

        Assert.Equal($"127.0.0.1:11434", startInfo.Environment["OLLAMA_HOST"]);
        Assert.Equal(Path.Combine("/Volumes/FreeAiSsd", SsdLayout.Models), startInfo.Environment["OLLAMA_MODELS"]);
        Assert.Equal("http://127.0.0.1,http://localhost", startInfo.Environment["OLLAMA_ORIGINS"]);
        Assert.Contains("serve", startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
    }

    [Fact]
    public void BuildStartInfo_NeverBindsToZeroAddressOrLan()
    {
        // Loopback-only is non-negotiable: Ollama must never be reachable from
        // outside the host. If a future change accidentally widens this, the
        // test below should catch it before review.
        var startInfo = MacOllamaLifecycleService.BuildStartInfo(
            "/fake/ollama", "/fake/ssd", 11500);

        Assert.StartsWith("127.0.0.1:", startInfo.Environment["OLLAMA_HOST"]);
        Assert.DoesNotContain("0.0.0.0", startInfo.Environment["OLLAMA_HOST"]);
    }

    [Fact]
    public void Start_RefusesWhenBinaryMissing()
    {
        using var ssd = new TempSsdRoot();
        using var svc = new MacOllamaLifecycleService(logger: null);

        var result = svc.Start(new PortableConfig(), ssd.Root);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("missing", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.False(svc.IsRunning);
    }

    [Fact]
    public void ValidateTrust_FailsWhenAttestationMissing()
    {
        using var ssd = new TempSsdRoot();
        using var svc = new MacOllamaLifecycleService(logger: null);

        var (isTrusted, message) = svc.ValidateTrust(ssd.Root);

        Assert.False(isTrusted);
        Assert.Contains("attestation", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateTrust_PassesWithMatchingMacAttestation()
    {
        using var ssd = new TempSsdRoot();
        OllamaPackageTrustPolicy.WriteMacTrustAttestation(ssd.Root, SampleMacMetadata());

        using var svc = new MacOllamaLifecycleService(logger: null);
        var (isTrusted, _) = svc.ValidateTrust(ssd.Root);

        Assert.True(isTrusted);
    }

    /// <summary>
    /// Mac trust must not pass just because a Windows attestation exists on
    /// the same SSD — the validator looks at the Mac-specific path.
    /// </summary>
    [Fact]
    public void ValidateTrust_FailsWhenOnlyWindowsAttestationPresent()
    {
        using var ssd = new TempSsdRoot();
        OllamaPackageTrustPolicy.WriteTrustAttestation(ssd.Root, SampleWindowsMetadata());

        using var svc = new MacOllamaLifecycleService(logger: null);
        var (isTrusted, _) = svc.ValidateTrust(ssd.Root);

        Assert.False(isTrusted);
    }
}
