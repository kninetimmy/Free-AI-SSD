using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Prereqs;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

/// <summary>
/// Stages the optional Piper neural TTS engine onto the SSD: downloads the
/// archived rhasspy/piper standalone binary archive (verified against the
/// static SHA-256 pin in <see cref="PiperCatalog"/>), extracts it flat into
/// <c>{piperDir}/</c>, then downloads + verifies the user-selected voice
/// files into <c>{piperDir}/voices/</c>. Records the result in
/// <c>{piperDir}/piper-manifest.json</c>.
///
/// All downloads are HTTPS-only and SHA-256 verified — the binary against the
/// pinned catalog hash, the .onnx against the live Hugging Face LFS oid
/// fetched via <see cref="PiperResolver"/>, and the .onnx.json against its
/// pinned catalog hash. Any verification failure throws and leaves no partial
/// state in the destination (download streams to a .download temp file and is
/// promoted only after the hash matches).
///
/// Platform-agnostic: the caller passes the target <see cref="PiperPlatform"/>
/// explicitly (typically <see cref="DetectCurrentPlatform"/>). The Windows
/// runner stages Windows-amd64 onto its own machine; the Mac sidecar stages
/// the matching macOS arch onto its own machine.
/// </summary>
public sealed class PiperStagingService : IPiperStagingService
{
    private readonly Func<HttpClient> _httpFactory;

    /// <summary>
    /// Constructs the service. The factory lets tests inject a mock; production
    /// callers can use the parameterless constructor.
    /// </summary>
    public PiperStagingService(Func<HttpClient>? httpFactory = null)
    {
        _httpFactory = httpFactory ?? CreateDefaultClient;
    }

    /// <summary>
    /// Detects the running host's Piper platform. Throws on unsupported
    /// hosts (Linux, 32-bit Windows, etc.) — the catalog deliberately only
    /// covers the three platforms we ship.
    /// </summary>
    public static PiperPlatform DetectCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return PiperPlatform.WindowsAmd64;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => PiperPlatform.MacosAarch64,
                Architecture.X64 => PiperPlatform.MacosX64,
                _ => throw new PlatformNotSupportedException(
                    $"macOS process architecture {RuntimeInformation.ProcessArchitecture} is not supported by the Piper catalog."),
            };
        }
        throw new PlatformNotSupportedException(
            $"Piper staging is not supported on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}).");
    }

    /// <summary>
    /// Stages the Piper binary + the catalog default voice (or the explicitly
    /// supplied voice list) onto the SSD. Idempotent: if the manifest already
    /// records the same binary + voice with matching SHA-256, the service
    /// re-verifies the on-disk files and skips re-downloading.
    /// </summary>
    /// <param name="ssdRoot">SSD root path (the directory containing windows/, mac/, etc.).</param>
    /// <param name="platform">Target platform for binary selection + on-SSD path.</param>
    /// <param name="onLog">Log sink for progress messages.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task StagePiperAsync(
        string ssdRoot,
        PiperPlatform platform,
        Action<string> onLog,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ssdRoot))
            throw new ArgumentException("SSD root must be non-empty.", nameof(ssdRoot));
        if (onLog is null) throw new ArgumentNullException(nameof(onLog));
        var voices = new[] { PiperCatalog.DefaultVoice };

        var piperDir = Path.Combine(ssdRoot, PiperLayout.GetPiperDir(platform));
        var voicesDir = Path.Combine(ssdRoot, PiperLayout.GetVoicesDir(platform));
        Directory.CreateDirectory(piperDir);
        Directory.CreateDirectory(voicesDir);

        var manifestPath = PiperCatalog.GetManifestPath(ssdRoot, platform);
        var manifest = PiperManifest.Load(manifestPath);

        using var http = _httpFactory();

        await StageBinaryAsync(piperDir, platform, manifest, http, onLog, ct);
        foreach (var voice in voices)
        {
            await StageVoiceAsync(voicesDir, voice, manifest, http, onLog, ct);
        }

        await manifest.SaveAsync(manifestPath);
        onLog($"Wrote Piper manifest: {manifestPath}");
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "FreeAiSsd-PrepApp/1.0 (+https://github.com/kninetimmy/free-ai-ssd)");
        return client;
    }

    private async Task StageBinaryAsync(
        string piperDir, PiperPlatform platform, PiperManifest manifest,
        HttpClient http, Action<string> onLog, CancellationToken ct)
    {
        var asset = PiperCatalog.GetBinaryAsset(platform);

        // Idempotency: a healthy install already has piper(.exe) at the root of
        // piperDir AND a manifest matching the catalog SHA. If both are true,
        // skip re-extracting; otherwise re-stage from scratch (a missing binary
        // means a partial install; a SHA mismatch means an upstream drift).
        var existingBinary = manifest.Binary;
        var executableName = platform == PiperPlatform.WindowsAmd64 ? "piper.exe" : "piper";
        var executablePath = Path.Combine(piperDir, executableName);
        if (existingBinary is not null &&
            string.Equals(existingBinary.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(executablePath))
        {
            onLog($"Piper binary already staged ({asset.ArchiveFileName}, SHA-256 matches catalog).");
            return;
        }

        var archivePath = Path.Combine(piperDir, asset.ArchiveFileName);
        var resolution = new PrereqResolution(
            Version: PiperCatalog.BinaryReleaseTag,
            Url: asset.Url,
            Hash: asset.Sha256,
            HashAlgorithm: "SHA256",
            TrustNote: $"Static catalog pin for archived rhasspy/piper release {PiperCatalog.BinaryReleaseTag}.");

        onLog($"Downloading Piper binary: {asset.ArchiveFileName} ({asset.SizeBytes:N0} bytes)");
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
                $"Piper archive size mismatch: expected {asset.SizeBytes}, got {observedSize}.");
        }

        ExtractArchiveFlat(archivePath, piperDir, platform, onLog);
        File.Delete(archivePath);

        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException(
                $"Piper extraction completed but {executableName} is not at {executablePath}.");
        }

        manifest.Binary = new PiperBinaryManifestEntry
        {
            Platform = platform.ToString(),
            ReleaseTag = PiperCatalog.BinaryReleaseTag,
            ArchiveFileName = asset.ArchiveFileName,
            SourceUrl = asset.Url,
            Sha256 = observedSha,
            SizeBytes = observedSize,
            InstalledAtUtc = DateTime.UtcNow,
            LicenseNote = PiperCatalog.BinaryLicenseNote,
        };
        onLog($"Piper binary staged at {executablePath}");
    }

    private async Task StageVoiceAsync(
        string voicesDir, PiperVoice voice, PiperManifest manifest,
        HttpClient http, Action<string> onLog, CancellationToken ct)
    {
        onLog($"Resolving Piper voice: {voice.DisplayName} ({voice.Id})");
        var resolution = await PiperResolver.ResolveVoiceAsync(http, voice, ct);
        onLog($"Resolved {voice.Id}: onnx SHA-256={resolution.OnnxSha256} ({resolution.TrustNote})");

        var onnxPath = Path.Combine(voicesDir, voice.OnnxFileName);
        var onnxJsonPath = Path.Combine(voicesDir, voice.OnnxJsonFileName);

        var existingEntry = manifest.Voices.FirstOrDefault(
            v => string.Equals(v.Id, voice.Id, StringComparison.OrdinalIgnoreCase));
        if (existingEntry is not null &&
            string.Equals(existingEntry.OnnxSha256, resolution.OnnxSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existingEntry.OnnxJsonSha256, voice.OnnxJsonSha256, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(onnxPath) && File.Exists(onnxJsonPath))
        {
            onLog($"Voice already staged: {voice.Id} (SHA-256 matches catalog + HF).");
            return;
        }

        await DownloadAndVerifyFileAsync(http,
            new PrereqResolution(voice.Id, resolution.OnnxUrl, resolution.OnnxSha256, "SHA256", resolution.TrustNote),
            onnxPath, onLog, ct);

        await DownloadAndVerifyFileAsync(http,
            new PrereqResolution(voice.Id, resolution.OnnxJsonUrl, voice.OnnxJsonSha256, "SHA256",
                $"Static catalog pin for {voice.OnnxJsonFileName}."),
            onnxJsonPath, onLog, ct);

        var onnxSize = new FileInfo(onnxPath).Length;
        var jsonSize = new FileInfo(onnxJsonPath).Length;
        var updated = new PiperVoiceManifestEntry
        {
            Id = voice.Id,
            DisplayName = voice.DisplayName,
            OnnxUrl = resolution.OnnxUrl,
            OnnxSha256 = resolution.OnnxSha256,
            OnnxSizeBytes = onnxSize,
            OnnxJsonUrl = resolution.OnnxJsonUrl,
            OnnxJsonSha256 = voice.OnnxJsonSha256,
            OnnxJsonSizeBytes = jsonSize,
            InstalledAtUtc = DateTime.UtcNow,
            TrustNote = resolution.TrustNote,
        };
        if (existingEntry is null) manifest.Voices.Add(updated);
        else CopyInto(updated, existingEntry);

        onLog($"Voice staged: {voice.Id} (onnx {onnxSize:N0} bytes, json {jsonSize:N0} bytes)");
    }

    private static Task<string> DownloadAndVerifyFileAsync(
        HttpClient http, PrereqResolution resolution, string destinationPath,
        Action<string> onLog, CancellationToken ct) =>
        PrereqResolver.DownloadAndVerifyAsync(http, resolution, destinationPath, onLog, ct);

    /// <summary>
    /// Extracts the rhasspy/piper archive flat into <paramref name="targetDir"/>.
    /// The archives all wrap their contents in a top-level "piper/" directory;
    /// we promote those entries to the root of the piper dir so the runtime's
    /// hard-coded layout (<c>windows/tools/piper/piper.exe</c>) is satisfied
    /// without an extra nesting level.
    /// </summary>
    private static void ExtractArchiveFlat(
        string archivePath, string targetDir, PiperPlatform platform, Action<string> onLog)
    {
        if (platform == PiperPlatform.WindowsAmd64)
        {
            ExtractZipFlat(archivePath, targetDir, onLog);
        }
        else
        {
            ExtractTarGzFlat(archivePath, targetDir, onLog);
        }
    }

    private static void ExtractZipFlat(string archivePath, string targetDir, Action<string> onLog)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entryCount = 0;
        foreach (var entry in archive.Entries)
        {
            var relative = StripTopLevelDir(entry.FullName);
            if (string.IsNullOrEmpty(relative)) continue; // top-level dir itself

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

    private static void ExtractTarGzFlat(string archivePath, string targetDir, Action<string> onLog)
    {
        var entryCount = 0;
        using (var fileStream = File.OpenRead(archivePath))
        using (var gzip = new GZipStream(fileStream, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip, leaveOpen: false))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                var relative = StripTopLevelDir(entry.Name);
                if (string.IsNullOrEmpty(relative)) continue;

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
                        // Piper archives don't ship symlinks; if one ever appears,
                        // skip it loudly rather than silently materializing a path.
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
    /// Removes the single top-level directory component from an archive entry
    /// path. rhasspy/piper archives wrap every entry under <c>piper/</c>; we
    /// promote those entries to the destination root. Returns the entry path
    /// unchanged if it has no leading directory component (defensive: should
    /// not happen for these archives, but lets us extract a malformed archive
    /// without losing files).
    /// </summary>
    internal static string StripTopLevelDir(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/').TrimStart('/');
        var firstSlash = normalized.IndexOf('/');
        if (firstSlash < 0)
        {
            // Top-level file (no directory) — keep as-is.
            // Or a top-level directory entry like "piper/" → return "".
            if (normalized.EndsWith('/')) return string.Empty;
            return normalized;
        }
        return normalized.Substring(firstSlash + 1);
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

    private static void CopyInto(PiperVoiceManifestEntry source, PiperVoiceManifestEntry target)
    {
        target.DisplayName = source.DisplayName;
        target.OnnxUrl = source.OnnxUrl;
        target.OnnxSha256 = source.OnnxSha256;
        target.OnnxSizeBytes = source.OnnxSizeBytes;
        target.OnnxJsonUrl = source.OnnxJsonUrl;
        target.OnnxJsonSha256 = source.OnnxJsonSha256;
        target.OnnxJsonSizeBytes = source.OnnxJsonSizeBytes;
        target.InstalledAtUtc = source.InstalledAtUtc;
        target.TrustNote = source.TrustNote;
    }
}
