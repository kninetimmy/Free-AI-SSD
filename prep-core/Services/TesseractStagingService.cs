using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Prereqs;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

/// <summary>
/// Stages the optional Tesseract OCR engine onto the SSD: downloads the curated
/// portable bundle (tesseract.exe + runtime DLLs + tessdata, verified against
/// the static SHA-256 pin in <see cref="TesseractCatalog"/>), extracts it into
/// <c>{tesseractDir}/</c> preserving the bundle's directory layout (so the
/// <c>tessdata/</c> subtree — including <c>tessdata/configs/tsv</c> used by the
/// OCR service — lands intact), and records the result in
/// <c>{tesseractDir}/tesseract-manifest.json</c>.
///
/// All downloads are HTTPS-only and SHA-256 verified against the pinned catalog
/// hash, with a defense-in-depth size check. Any verification failure throws and
/// leaves no partial state in the destination (download streams to a temp file
/// and is promoted only after the hash matches). Mirrors
/// <see cref="PiperStagingService"/> but is binary-only and structure-preserving
/// — Tesseract bundles its language data inside the same archive.
/// </summary>
public sealed class TesseractStagingService : ITesseractStagingService
{
    private readonly Func<HttpClient> _httpFactory;

    /// <summary>
    /// Constructs the service. The factory lets tests inject a mock; production
    /// callers can use the parameterless constructor.
    /// </summary>
    public TesseractStagingService(Func<HttpClient>? httpFactory = null)
    {
        _httpFactory = httpFactory ?? CreateDefaultClient;
    }

    /// <summary>
    /// Detects the running host's Tesseract platform. Throws on unsupported
    /// hosts — the catalog currently only covers Windows-amd64 (macOS is a
    /// fast-follow), so this fails closed rather than guessing.
    /// </summary>
    public static TesseractPlatform DetectCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return TesseractPlatform.WindowsAmd64;
        }
        throw new PlatformNotSupportedException(
            $"Tesseract staging is not supported on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}).");
    }

    /// <summary>
    /// Stages the Tesseract bundle for <paramref name="platform"/> onto the SSD.
    /// Idempotent: if the manifest already records the same bundle with a
    /// matching SHA-256 and <c>tesseract.exe</c> is present, the service skips
    /// re-downloading.
    /// </summary>
    /// <param name="ssdRoot">SSD root path (the directory containing windows/, mac/, etc.).</param>
    /// <param name="platform">Target platform for asset selection + on-SSD path.</param>
    /// <param name="onLog">Log sink for progress messages.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StageTesseractAsync(
        string ssdRoot,
        TesseractPlatform platform,
        Action<string> onLog,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ssdRoot))
            throw new ArgumentException("SSD root must be non-empty.", nameof(ssdRoot));
        if (onLog is null) throw new ArgumentNullException(nameof(onLog));

        var tesseractDir = Path.Combine(ssdRoot, TesseractLayout.GetDir(platform));
        Directory.CreateDirectory(tesseractDir);

        var manifestPath = TesseractCatalog.GetManifestPath(ssdRoot, platform);
        var manifest = TesseractManifest.Load(manifestPath);

        using var http = _httpFactory();
        await StageBinaryAsync(tesseractDir, platform, manifest, http, onLog, ct);

        await manifest.SaveAsync(manifestPath);
        onLog($"Wrote Tesseract manifest: {manifestPath}");
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "FreeAiSsd-PrepApp/1.0 (+https://github.com/kninetimmy/free-ai-ssd)");
        return client;
    }

    private async Task StageBinaryAsync(
        string tesseractDir, TesseractPlatform platform, TesseractManifest manifest,
        HttpClient http, Action<string> onLog, CancellationToken ct)
    {
        var asset = TesseractCatalog.GetBinaryAsset(platform);

        // Idempotency: a healthy install has tesseract(.exe) at the root of the
        // tesseract dir AND a manifest matching the catalog SHA. If both hold,
        // skip re-extracting; otherwise re-stage (a missing exe means a partial
        // install; a SHA mismatch means an upstream/catalog drift).
        var existingBinary = manifest.Binary;
        var executableName = platform == TesseractPlatform.WindowsAmd64 ? "tesseract.exe" : "tesseract";
        var executablePath = Path.Combine(tesseractDir, executableName);
        if (existingBinary is not null &&
            string.Equals(existingBinary.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(executablePath))
        {
            onLog($"Tesseract bundle already staged ({asset.ArchiveFileName}, SHA-256 matches catalog).");
            return;
        }

        var archivePath = Path.Combine(tesseractDir, asset.ArchiveFileName);
        var resolution = new PrereqResolution(
            Version: TesseractCatalog.BinaryReleaseTag,
            Url: asset.Url,
            Hash: asset.Sha256,
            HashAlgorithm: "SHA256",
            TrustNote: $"Static catalog pin for frozen Tesseract bundle {TesseractCatalog.BinaryReleaseTag}.");

        onLog($"Downloading Tesseract bundle: {asset.ArchiveFileName} ({asset.SizeBytes:N0} bytes)");
        var observedSha = await PrereqResolver.DownloadAndVerifyAsync(
            http, resolution, archivePath, onLog, ct);

        // Defense-in-depth: file size must match the catalog. The SHA already
        // covers integrity, but a size delta against the pin is a fast,
        // human-readable signal that something is off.
        var observedSize = new FileInfo(archivePath).Length;
        if (observedSize != asset.SizeBytes)
        {
            File.Delete(archivePath);
            throw new InvalidOperationException(
                $"Tesseract archive size mismatch: expected {asset.SizeBytes}, got {observedSize}.");
        }

        ExtractZipPreservingLayout(archivePath, tesseractDir, onLog);
        File.Delete(archivePath);

        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException(
                $"Tesseract extraction completed but {executableName} is not at {executablePath}.");
        }
        var tessdataDir = Path.Combine(tesseractDir, "tessdata");
        if (!Directory.Exists(tessdataDir))
        {
            throw new InvalidOperationException(
                $"Tesseract extraction completed but the tessdata directory is missing at {tessdataDir}.");
        }

        manifest.Binary = new TesseractBinaryManifestEntry
        {
            Platform = platform.ToString(),
            ReleaseTag = TesseractCatalog.BinaryReleaseTag,
            TesseractVersion = TesseractCatalog.TesseractVersion,
            ArchiveFileName = asset.ArchiveFileName,
            SourceUrl = asset.Url,
            Sha256 = observedSha,
            SizeBytes = observedSize,
            InstalledAtUtc = DateTime.UtcNow,
            LicenseNote = TesseractCatalog.BinaryLicenseNote,
        };
        onLog($"Tesseract bundle staged at {executablePath}");
    }

    /// <summary>
    /// Extracts the curated Tesseract zip into <paramref name="targetDir"/>,
    /// preserving the archive's relative layout (unlike the Piper flat extract):
    /// the bundle has no top-level wrapper directory, and its <c>tessdata/</c>
    /// subtree must remain nested for Tesseract to find its language data and
    /// configs. Each entry is guarded against zip-slip before extraction.
    /// </summary>
    private static void ExtractZipPreservingLayout(string archivePath, string targetDir, Action<string> onLog)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entryCount = 0;
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrEmpty(relative)) continue;

            var destPath = Path.Combine(targetDir, relative);
            EnsureWithin(targetDir, destPath, entry.FullName);

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(destPath);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
            entryCount++;
        }
        onLog($"Extracted {entryCount} files from {Path.GetFileName(archivePath)}.");
    }

    private static void EnsureWithin(string baseDir, string candidatePath, string archiveEntryName)
    {
        var fullBase = Path.GetFullPath(baseDir + Path.DirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidatePath);
        if (!fullCandidate.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to extract archive entry outside destination: {archiveEntryName}");
        }
    }
}
