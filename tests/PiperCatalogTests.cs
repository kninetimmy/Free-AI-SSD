using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Prereqs;

namespace FreeAiSsd.Tests;

/// <summary>
/// Catalog invariants for the Piper TTS staging. These are deliberately
/// strict — the binary assets are static-pinned because the upstream is
/// archived, so any drift in the catalog (URL, hash, asset name) without
/// updating the corresponding pins must trip a test.
/// </summary>
public sealed class PiperCatalogTests
{
    [Fact]
    public void Catalog_HasAllThreeBinaryAssets()
    {
        var assets = PiperCatalog.BinaryAssets;
        Assert.Equal(3, assets.Count);
        Assert.Contains(assets, a => a.Platform == PiperPlatform.WindowsAmd64);
        Assert.Contains(assets, a => a.Platform == PiperPlatform.MacosAarch64);
        Assert.Contains(assets, a => a.Platform == PiperPlatform.MacosX64);
    }

    [Theory]
    [InlineData(PiperPlatform.WindowsAmd64, "piper_windows_amd64.zip")]
    [InlineData(PiperPlatform.MacosAarch64, "piper_macos_aarch64.tar.gz")]
    [InlineData(PiperPlatform.MacosX64, "piper_macos_x64.tar.gz")]
    public void GetBinaryAsset_ReturnsExpectedArchiveForPlatform(PiperPlatform platform, string expectedName)
    {
        var asset = PiperCatalog.GetBinaryAsset(platform);
        Assert.Equal(expectedName, asset.ArchiveFileName);
        Assert.StartsWith("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/", asset.Url);
        Assert.Equal(64, asset.Sha256.Length);
        Assert.Matches("^[a-f0-9]{64}$", asset.Sha256);
        Assert.True(asset.SizeBytes > 0, "asset size must be positive");
    }

    [Fact]
    public void BinaryUrls_AllPointAtFrozenReleaseTag()
    {
        foreach (var asset in PiperCatalog.BinaryAssets)
        {
            Assert.Contains($"/releases/download/{PiperCatalog.BinaryReleaseTag}/", asset.Url);
            Assert.EndsWith(asset.ArchiveFileName, asset.Url);
        }
    }

    [Fact]
    public void DefaultVoice_IsAmyMedium_WithPinnedJsonHash()
    {
        var v = PiperCatalog.DefaultVoice;
        Assert.Equal("en_US-amy-medium", v.Id);
        Assert.Equal("rhasspy/piper-voices", v.HfRepo);
        Assert.Equal("en/en_US/amy/medium", v.HfPath);
        Assert.Equal("en_US-amy-medium.onnx", v.OnnxFileName);
        Assert.Equal("en_US-amy-medium.onnx.json", v.OnnxJsonFileName);
        Assert.Equal(64, v.OnnxJsonSha256.Length);
        Assert.Matches("^[a-f0-9]{64}$", v.OnnxJsonSha256);
    }

    [Fact]
    public void Voices_ContainsDefault()
    {
        Assert.Contains(PiperCatalog.Voices, v => v.Id == PiperCatalog.DefaultVoice.Id);
    }

    [Fact]
    public void GetManifestPath_LandsUnderPlatformPiperDir()
    {
        var winPath = PiperCatalog.GetManifestPath("/ssd", PiperPlatform.WindowsAmd64);
        var macPath = PiperCatalog.GetManifestPath("/ssd", PiperPlatform.MacosAarch64);
        Assert.EndsWith(Path.Combine(SsdLayout.WindowsPiper, "piper-manifest.json"), winPath);
        Assert.EndsWith(Path.Combine(SsdLayout.MacPiper, "piper-manifest.json"), macPath);
    }

    [Fact]
    public void GetBinaryAsset_UnknownPlatform_Throws()
    {
        var unknown = (PiperPlatform)999;
        Assert.Throws<InvalidOperationException>(() => PiperCatalog.GetBinaryAsset(unknown));
    }

    [Fact]
    public void Layout_PiperPathsAreUnderToolsDir()
    {
        Assert.Equal("windows/tools/piper", SsdLayout.WindowsPiper);
        Assert.Equal("windows/tools/piper/voices", SsdLayout.WindowsPiperVoices);
        Assert.Equal("mac/tools/piper", SsdLayout.MacPiper);
        Assert.Equal("mac/tools/piper/voices", SsdLayout.MacPiperVoices);
    }
}
