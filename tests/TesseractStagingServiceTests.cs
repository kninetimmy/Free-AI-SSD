using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Prereqs;

namespace FreeAiSsd.Tests;

/// <summary>
/// Unit coverage for the Tesseract staging seams that don't require network or
/// platform tooling: the catalog asset lookup, the manifest round-trip, and the
/// fail-closed behavior on an uncovered platform. The full download+extract flow
/// is exercised on a real drive (and by the integration OCR test against a real
/// binary), not in xUnit — mirroring <see cref="PiperStagingServiceTests"/>.
/// </summary>
public sealed class TesseractStagingServiceTests
{
    [Fact]
    public void Catalog_WindowsAsset_HasPinnedHashUrlAndSize()
    {
        var asset = TesseractCatalog.GetBinaryAsset(TesseractPlatform.WindowsAmd64);

        Assert.Equal(TesseractPlatform.WindowsAmd64, asset.Platform);
        Assert.StartsWith("https://", asset.Url);
        Assert.Contains(TesseractCatalog.BinaryReleaseTag, asset.Url);
        Assert.EndsWith(".zip", asset.ArchiveFileName);
        // Pinned SHA-256 is a 64-char lowercase hex string.
        Assert.Equal(64, asset.Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
        Assert.True(asset.SizeBytes > 0);
    }

    [Fact]
    public void Catalog_UnknownPlatform_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => TesseractCatalog.GetBinaryAsset((TesseractPlatform)999));
    }

    [Fact]
    public void Layout_UnknownPlatform_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TesseractLayout.GetDir((TesseractPlatform)999));
    }

    [Fact]
    public void ManifestPath_IsUnderPlatformTesseractDir()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = TesseractCatalog.GetManifestPath(root, TesseractPlatform.WindowsAmd64);

        Assert.Equal(
            Path.Combine(root, SsdLayout.WindowsTesseract, TesseractCatalog.ManifestFileName),
            path);
    }

    [Fact]
    public async Task Manifest_RoundTripsBinaryEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tesseract-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var manifestPath = Path.Combine(dir, TesseractCatalog.ManifestFileName);
            var written = new TesseractManifest
            {
                Binary = new TesseractBinaryManifestEntry
                {
                    Platform = "WindowsAmd64",
                    ReleaseTag = TesseractCatalog.BinaryReleaseTag,
                    TesseractVersion = TesseractCatalog.TesseractVersion,
                    ArchiveFileName = "tesseract-5.4.0.20240606-win-x64.zip",
                    SourceUrl = "https://example/tesseract.zip",
                    Sha256 = new string('a', 64),
                    SizeBytes = 62298545,
                    InstalledAtUtc = new DateTime(2026, 6, 4, 12, 0, 0, DateTimeKind.Utc),
                    LicenseNote = "test",
                },
            };

            await written.SaveAsync(manifestPath);
            var loaded = TesseractManifest.Load(manifestPath);

            Assert.NotNull(loaded.Binary);
            Assert.Equal("WindowsAmd64", loaded.Binary!.Platform);
            Assert.Equal(TesseractCatalog.TesseractVersion, loaded.Binary.TesseractVersion);
            Assert.Equal(62298545, loaded.Binary.SizeBytes);
            Assert.Equal(new string('a', 64), loaded.Binary.Sha256);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Manifest_LoadMissingFile_ReturnsEmpty()
    {
        var manifest = TesseractManifest.Load(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "no.json"));
        Assert.Null(manifest.Binary);
    }
}
