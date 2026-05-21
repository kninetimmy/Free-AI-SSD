using FreeAiSsd.Shared;
using FreeAiSsd.PrepApp.Services;

namespace FreeAiSsd.Tests;

/// <summary>
/// Unit coverage for the <see cref="PiperStagingService"/> internals that do
/// not require network or platform-specific tooling: archive-entry path
/// normalization and manifest round-trip. The full download+extract flow is
/// exercised by hand on each platform (and by build.ps1 publish), not in
/// xUnit — the network-touching seam is the resolver layer, which has its
/// own ParseVoiceTree tests.
/// </summary>
public sealed class PiperStagingServiceTests
{
    [Theory]
    [InlineData("piper/piper.exe", "piper.exe")]
    [InlineData("piper/espeak-ng-data/lang", "espeak-ng-data/lang")]
    [InlineData("piper/", "")]
    [InlineData("piper", "piper")] // top-level file, no slash → keep as-is
    [InlineData("piper\\piper.exe", "piper.exe")] // Windows-style sep normalized
    [InlineData("/piper/piper.exe", "piper.exe")] // tolerate leading slash
    public void StripTopLevelDir_PromotesEntriesUnderTopFolder(string input, string expected)
    {
        Assert.Equal(expected, PiperStagingService.StripTopLevelDir(input));
    }

    [Fact]
    public async Task Manifest_RoundTripsBinaryAndVoiceEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "piper-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var manifestPath = Path.Combine(dir, "piper-manifest.json");
            var written = new PiperManifest
            {
                Binary = new PiperBinaryManifestEntry
                {
                    Platform = "WindowsAmd64",
                    ReleaseTag = "2023.11.14-2",
                    ArchiveFileName = "piper_windows_amd64.zip",
                    SourceUrl = "https://example/piper_windows_amd64.zip",
                    Sha256 = new string('a', 64),
                    SizeBytes = 12345,
                    InstalledAtUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
                    LicenseNote = "test",
                },
                Voices =
                {
                    new PiperVoiceManifestEntry
                    {
                        Id = "en_US-amy-medium",
                        DisplayName = "Amy",
                        OnnxUrl = "https://example/amy.onnx",
                        OnnxSha256 = new string('b', 64),
                        OnnxSizeBytes = 100,
                        OnnxJsonUrl = "https://example/amy.onnx.json",
                        OnnxJsonSha256 = new string('c', 64),
                        OnnxJsonSizeBytes = 20,
                        InstalledAtUtc = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc),
                        TrustNote = "test",
                    },
                },
            };

            await written.SaveAsync(manifestPath);
            var loaded = PiperManifest.Load(manifestPath);

            Assert.NotNull(loaded.Binary);
            Assert.Equal("WindowsAmd64", loaded.Binary!.Platform);
            Assert.Equal("piper_windows_amd64.zip", loaded.Binary.ArchiveFileName);
            Assert.Equal(12345, loaded.Binary.SizeBytes);
            Assert.Single(loaded.Voices);
            Assert.Equal("en_US-amy-medium", loaded.Voices[0].Id);
            Assert.Equal(new string('b', 64), loaded.Voices[0].OnnxSha256);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Manifest_LoadMissingFile_ReturnsEmpty()
    {
        var manifest = PiperManifest.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no.json"));
        Assert.Null(manifest.Binary);
        Assert.Empty(manifest.Voices);
    }
}
