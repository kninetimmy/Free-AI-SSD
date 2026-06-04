using System.Formats.Tar;
using System.IO.Compression;
using FreeAiSsd.PrepApp.Services;
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
    public void Catalog_MacArm64Asset_HasPinnedTarGzHashUrlAndVersion()
    {
        var asset = TesseractCatalog.GetBinaryAsset(TesseractPlatform.MacosAarch64);

        Assert.Equal(TesseractPlatform.MacosAarch64, asset.Platform);
        Assert.StartsWith("https://", asset.Url);
        Assert.Contains(TesseractCatalog.BinaryReleaseTag, asset.Url);
        // macOS ships a .tar.gz (the staging service infers format from this extension).
        Assert.EndsWith(".tar.gz", asset.ArchiveFileName);
        Assert.Equal(64, asset.Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
        Assert.True(asset.SizeBytes > 0);
        // Per-asset version: mac is curated from Homebrew 5.5.x, not the Windows 5.4.0 const.
        Assert.StartsWith("5.5", asset.TesseractVersion);
        Assert.NotEqual(TesseractCatalog.TesseractVersion, asset.TesseractVersion);
    }

    [Fact]
    public void Catalog_MacX64_NotYetCovered_FailsClosed()
    {
        // Intel Macs map to MacosX64 in DetectCurrentPlatform, but no bundle ships yet —
        // the lookup must throw (no placeholder hash) rather than silently degrade.
        Assert.Throws<InvalidOperationException>(
            () => TesseractCatalog.GetBinaryAsset(TesseractPlatform.MacosX64));
    }

    [Theory]
    [InlineData(TesseractPlatform.MacosAarch64)]
    [InlineData(TesseractPlatform.MacosX64)]
    public void Layout_MacPlatforms_MapToMacTesseractDir(TesseractPlatform platform)
    {
        Assert.Equal(SsdLayout.MacTesseract, TesseractLayout.GetDir(platform));
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

    [Fact]
    public void ExtractTarGz_PreservesNestedTessdataLayout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tess-targz-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Build a synthetic bundle like the real one: root-relative entries (packed with
            // `tar -C stage .`, so a leading "./") and a nested tessdata/configs/tsv that MUST
            // survive — Tesseract can't find its language data or the tsv config otherwise.
            var archive = Path.Combine(dir, "bundle.tar.gz");
            WriteTarGz(archive, new (string Name, string Body)[]
            {
                ("./tesseract", "#!binary"),
                ("./libleptonica.6.dylib", "dylib"),
                ("./tessdata/eng.traineddata", "eng"),
                ("./tessdata/configs/tsv", "tessedit_create_tsv 1"),
            });

            var dest = Path.Combine(dir, "out");
            Directory.CreateDirectory(dest);
            TesseractStagingService.ExtractTarGzPreservingLayout(archive, dest, _ => { });

            Assert.True(File.Exists(Path.Combine(dest, "tesseract")));
            Assert.True(File.Exists(Path.Combine(dest, "libleptonica.6.dylib")));
            Assert.True(File.Exists(Path.Combine(dest, "tessdata", "eng.traineddata")));
            Assert.True(File.Exists(Path.Combine(dest, "tessdata", "configs", "tsv")));
            Assert.Equal("tessedit_create_tsv 1",
                File.ReadAllText(Path.Combine(dest, "tessdata", "configs", "tsv")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ExtractTarGz_RejectsZipSlipEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tess-targz-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var archive = Path.Combine(dir, "evil.tar.gz");
            // A path-traversal entry that would escape the destination dir if extracted naively.
            WriteTarGz(archive, new (string Name, string Body)[]
            {
                ("../escape.txt", "pwned"),
            });

            var dest = Path.Combine(dir, "out");
            Directory.CreateDirectory(dest);

            Assert.Throws<InvalidOperationException>(
                () => TesseractStagingService.ExtractTarGzPreservingLayout(archive, dest, _ => { }));
            Assert.False(File.Exists(Path.Combine(dir, "escape.txt")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void WriteTarGz(string archivePath, IReadOnlyList<(string Name, string Body)> entries)
    {
        using var fs = File.Create(archivePath);
        using var gz = new GZipStream(fs, CompressionMode.Compress);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var (name, body) in entries)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name);
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            entry.DataStream = new MemoryStream(bytes);
            tar.WriteEntry(entry);
        }
    }
}
