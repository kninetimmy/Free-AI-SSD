using System.Formats.Tar;
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
    /// hosts (Linux, 32-bit Windows, etc.), mirroring
    /// <see cref="PiperStagingService.DetectCurrentPlatform"/>. Intel Macs map to
    /// <see cref="TesseractPlatform.MacosX64"/>, which the catalog does not yet
    /// cover — the asset lookup then fails closed with a clear error.
    /// </summary>
    public static TesseractPlatform DetectCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return TesseractPlatform.WindowsAmd64;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => TesseractPlatform.MacosAarch64,
                Architecture.X64 => TesseractPlatform.MacosX64,
                _ => throw new PlatformNotSupportedException(
                    $"macOS process architecture {RuntimeInformation.ProcessArchitecture} is not supported by the Tesseract catalog."),
            };
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

        // The archive format is the source of truth from the asset filename: Windows ships a
        // .zip, macOS a .tar.gz (the Unix convention, matching Piper). Both extracts preserve the
        // bundle's nested layout so tessdata/ — including tessdata/configs/tsv — lands intact.
        if (asset.ArchiveFileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
            asset.ArchiveFileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTarGzPreservingLayout(archivePath, tesseractDir, onLog);
        }
        else
        {
            ExtractZipPreservingLayout(archivePath, tesseractDir, onLog);
        }
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

        // macOS hardening: a bundle downloaded by our app carries com.apple.quarantine, which
        // Gatekeeper can use to block first exec of the unsigned binary + dylibs. Strip it
        // recursively (fail-soft — logged, not fatal), and re-assert the exec bit on the binary in
        // case the extract dropped it. Mirrors the quarantine risk called out in the OCR plan.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await StripQuarantineAsync(tesseractDir, onLog, ct);
            EnsureExecutable(executablePath, onLog);
        }

        manifest.Binary = new TesseractBinaryManifestEntry
        {
            Platform = platform.ToString(),
            ReleaseTag = TesseractCatalog.BinaryReleaseTag,
            TesseractVersion = asset.TesseractVersion,
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

    /// <summary>
    /// Extracts the curated Tesseract <c>.tar.gz</c> into <paramref name="targetDir"/>,
    /// preserving the archive's relative layout. Unlike Piper's flat extract this keeps the
    /// nested <c>tessdata/</c> subtree (no top-level wrapper dir is stripped — the bundle is
    /// packed with <c>tar -C stage .</c> so entries are already root-relative). Each entry is
    /// guarded against zip-slip; symlinks are skipped loudly (the bundle ships none).
    /// <see cref="TarEntry.ExtractToFile"/> restores the Unix exec bit from the entry mode on
    /// Unix, so the relocated <c>tesseract</c> binary stays executable.
    /// </summary>
    internal static void ExtractTarGzPreservingLayout(string archivePath, string targetDir, Action<string> onLog)
    {
        var entryCount = 0;
        using (var fileStream = File.OpenRead(archivePath))
        using (var gzip = new GZipStream(fileStream, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip, leaveOpen: false))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                var relative = entry.Name.Replace('\\', '/').TrimStart('/');
                // tar packed with "-C stage ." prefixes entries with "./" — normalize it away.
                if (relative.StartsWith("./", StringComparison.Ordinal)) relative = relative[2..];
                if (string.IsNullOrEmpty(relative) || relative == ".") continue;

                var destPath = Path.Combine(targetDir, relative);
                EnsureWithin(targetDir, destPath, entry.Name);

                switch (entry.EntryType)
                {
                    case TarEntryType.Directory:
                    case TarEntryType.DirectoryList:
                        Directory.CreateDirectory(destPath);
                        break;
                    case TarEntryType.RegularFile:
                    case TarEntryType.V7RegularFile:
                    case TarEntryType.ContiguousFile:
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        entry.ExtractToFile(destPath, overwrite: true);
                        entryCount++;
                        break;
                    case TarEntryType.SymbolicLink:
                        onLog($"Skipped tar symlink entry: {entry.Name} -> {entry.LinkName}");
                        break;
                    default:
                        onLog($"Skipped tar entry {entry.Name} of type {entry.EntryType}.");
                        break;
                }
            }
        }
        onLog($"Extracted {entryCount} files from {Path.GetFileName(archivePath)}.");
    }

    /// <summary>
    /// Strips <c>com.apple.quarantine</c> recursively from the staged tesseract dir so Gatekeeper
    /// does not block first exec of the unsigned bundle. Fail-soft: a non-zero exit or a missing
    /// <c>xattr</c> is logged, not thrown — staging still succeeds and any block surfaces at run
    /// time with a clearer signal than a half-staged drive.
    /// </summary>
    private static async Task StripQuarantineAsync(string tesseractDir, Action<string> onLog, CancellationToken ct)
    {
        try
        {
            var exit = await ProcessRunner.RunAsync(
                "/usr/bin/xattr",
                new[] { "-dr", "com.apple.quarantine", tesseractDir },
                tesseractDir,
                onOutput: onLog,
                ct: ct).ConfigureAwait(false);
            onLog(exit == 0
                ? "Cleared com.apple.quarantine from staged Tesseract bundle."
                : $"xattr quarantine strip exited {exit} (non-fatal).");
        }
        catch (Exception ex)
        {
            onLog($"Could not strip quarantine xattr (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Re-asserts the owner-execute bit on the tesseract binary on Unix. Defensive: the tar extract
    /// normally restores the mode, but a permissive umask or odd archive can drop it.
    /// </summary>
    private static void EnsureExecutable(string executablePath, Action<string> onLog)
    {
        if (OperatingSystem.IsWindows()) return;   // NTFS has no Unix mode bits; caller already guards
        try
        {
            var mode = File.GetUnixFileMode(executablePath);
            var withExec = mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if (mode != withExec)
            {
                File.SetUnixFileMode(executablePath, withExec);
                onLog($"Set exec bit on {Path.GetFileName(executablePath)}.");
            }
        }
        catch (Exception ex)
        {
            onLog($"Could not set exec bit on {Path.GetFileName(executablePath)} (non-fatal): {ex.Message}");
        }
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
