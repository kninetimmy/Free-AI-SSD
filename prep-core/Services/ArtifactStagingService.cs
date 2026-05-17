using System.IO.Compression;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class ArtifactStagingService : IArtifactStagingService
{
    public async Task StageRunnerAsync(string ssdRoot, Action<string> onLog)
    {
        var sourceRunnerDir = ResolveRunnerPublishDirectory();
        var targetRunnerDir = Path.Combine(ssdRoot, SsdLayout.Runner);
        Directory.CreateDirectory(targetRunnerDir);

        if (sourceRunnerDir is null)
        {
            var hint = "Runner publish folder not found. Re-download the ZIP and ensure runner-publish is next to FreeAiSsd.PrepApp.exe (or under payload/runner-publish), or run ./build.ps1 to stage runner artifacts for local development.";
            onLog(hint);
            throw new DirectoryNotFoundException(hint);
        }

        onLog($"Using runner payload from: {sourceRunnerDir}");
        foreach (var file in Directory.EnumerateFiles(sourceRunnerDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRunnerDir, file);
            var destination = Path.Combine(targetRunnerDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        onLog("Runner artifacts staged.");
        await Task.CompletedTask;
    }


    public async Task StageCompanionAsync(string ssdRoot, Action<string> onLog)
    {
        var sourceCompanionDir = ResolveCompanionPublishDirectory();
        var targetCompanionDir = Path.Combine(ssdRoot, "companion");
        Directory.CreateDirectory(targetCompanionDir);

        if (sourceCompanionDir is null)
        {
            var hint = "Companion publish folder not found. Ensure companion-publish is next to FreeAiSsd.PrepApp.exe (or under payload/companion-publish).";
            onLog(hint);
            throw new DirectoryNotFoundException(hint);
        }

        onLog($"Using companion payload from: {sourceCompanionDir}");
        foreach (var file in Directory.EnumerateFiles(sourceCompanionDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceCompanionDir, file);
            var destination = Path.Combine(targetCompanionDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        onLog("Companion artifacts staged.");
        await Task.CompletedTask;
    }

    public async Task StageMacRunnerAsync(string ssdRoot, Action<string> onLog, CancellationToken ct)
    {
        var macAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        if (!macAvailability.MacArtifactsAvailable)
        {
            var message = macAvailability.MacArtifactsProblem ?? "macOS artifacts are unavailable.";
            onLog($"Skipped macOS runner staging: {message}");
            throw new InvalidOperationException(message);
        }

        // Runner.app is staged at the SSD ROOT (SsdLayout.MacRunner), not
        // under mac/, so the user double-clicks it without digging. The
        // mac-runner-host sidecar travels inside the bundle's Resources/, so
        // there is no separate on-SSD host directory anymore.
        var targetRunner = Path.Combine(ssdRoot, SsdLayout.MacRunner);

        if (Directory.Exists(targetRunner))
            Directory.Delete(targetRunner, recursive: true);

        // Re-prepping a drive staged by an older release: scrub the legacy
        // <root>/mac/Runner.app, the leftover <root>/mac/Runner.app.zip, and
        // the now-defunct <root>/mac/runner-host so nothing is doubled.
        CleanupLegacyMacRunnerArtifacts(ssdRoot, onLog);

        // The download now ships Runner.app UNZIPPED under <bundle>/mac/.
        // A legacy Runner.app.zip is still accepted for older/mixed bundles.
        var sourceRunnerDir = ResolveBundledDirectory(Path.Combine("mac", "Runner.app"));
        if (sourceRunnerDir is not null)
        {
            onLog($"Using macOS Runner.app from: {sourceRunnerDir}");
            await CopyAppBundleAsync(sourceRunnerDir, targetRunner, onLog, ct);
        }
        else
        {
            var sourceRunnerZip = ResolveBundledFile(Path.Combine("mac", "Runner.app.zip"))
                ?? throw new FileNotFoundException(
                    "Bundled macOS Runner.app not found — looked for an unzipped mac/Runner.app directory or a legacy mac/Runner.app.zip.");
            onLog($"Using macOS Runner.app archive from: {sourceRunnerZip}");
            await ExtractAppBundleAsync(sourceRunnerZip, ssdRoot, onLog, ct);
        }

        if (!Directory.Exists(targetRunner))
            throw new DirectoryNotFoundException($"Runner.app was not present at {targetRunner} after staging.");

        onLog("Staged macOS Runner.app at SSD root.");
    }

    /// <summary>
    /// Best-effort removal of pre-restructure macOS artifacts so a re-prep of
    /// an old-layout drive does not leave a doubled/zipped Runner.app or the
    /// orphaned mac/runner-host directory behind.
    /// </summary>
    private static void CleanupLegacyMacRunnerArtifacts(string ssdRoot, Action<string> onLog)
    {
        var macDir = Path.Combine(ssdRoot, SsdLayout.Mac);
        foreach (var stale in new[]
                 {
                     Path.Combine(macDir, "Runner.app"),
                     Path.Combine(macDir, "Runner.app.zip"),
                     Path.Combine(macDir, "runner-host"),
                 })
        {
            try
            {
                if (Directory.Exists(stale))
                {
                    Directory.Delete(stale, recursive: true);
                    onLog($"Removed legacy artifact: {stale}");
                }
                else if (File.Exists(stale))
                {
                    File.Delete(stale);
                    onLog($"Removed legacy artifact: {stale}");
                }
            }
            catch (Exception ex)
            {
                onLog($"Could not remove legacy artifact {stale}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Copies an .app bundle preserving symlinks, the executable bit, and
    /// extended attributes. macOS bundles contain framework symlinks and a
    /// Mach-O that must stay +x; .NET's recursive file copy flattens both, so
    /// on macOS we delegate to <c>ditto</c>. Off macOS (Windows staging a
    /// cross-platform drive) symlinks/perms cannot be preserved anyway — fall
    /// back to a recursive copy; the Mac side of a Windows-prepped drive is
    /// best-effort and documented as such.
    /// </summary>
    private static async Task CopyAppBundleAsync(string sourceDir, string targetDir, Action<string> onLog, CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            // Paths are absolute, so the working directory is irrelevant; use
            // the bundle's parent (the SSD root) for tidy log context.
            var workDir = Path.GetDirectoryName(targetDir) ?? Directory.GetCurrentDirectory();
            var exit = await ProcessRunner.RunAsync(
                "/usr/bin/ditto",
                new[] { sourceDir, targetDir },
                workDir,
                onOutput: onLog,
                ct: ct);
            if (exit != 0)
                throw new InvalidOperationException($"ditto exited {exit} copying {sourceDir} -> {targetDir}.");
            return;
        }

        RecursiveCopy(sourceDir, targetDir);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Extracts a ditto/zip archive of an .app bundle into
    /// <paramref name="destParentDir"/> preserving symlinks + perms on macOS
    /// (<c>ditto -x -k</c>). Off macOS falls back to <see cref="ZipFile"/>
    /// (best-effort; same caveat as <see cref="CopyAppBundleAsync"/>).
    /// </summary>
    private static async Task ExtractAppBundleAsync(string archivePath, string destParentDir, Action<string> onLog, CancellationToken ct)
    {
        Directory.CreateDirectory(destParentDir);
        if (OperatingSystem.IsMacOS())
        {
            var exit = await ProcessRunner.RunAsync(
                "/usr/bin/ditto",
                new[] { "-x", "-k", archivePath, destParentDir },
                destParentDir,
                onOutput: onLog,
                ct: ct);
            if (exit != 0)
                throw new InvalidOperationException($"ditto -x -k exited {exit} extracting {archivePath}.");
            return;
        }

        ZipFile.ExtractToDirectory(archivePath, destParentDir, overwriteFiles: true);
        await Task.CompletedTask;
    }

    private static void RecursiveCopy(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    public async Task StageMacOllamaAsync(string ssdRoot, Action<string> onLog, CancellationToken ct)
    {
        var macAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        if (!macAvailability.MacArtifactsAvailable)
        {
            var message = macAvailability.MacArtifactsProblem ?? "macOS artifacts are unavailable.";
            onLog($"Skipped macOS Ollama staging: {message}");
            throw new InvalidOperationException(message);
        }

        var bundledArchive = ResolveBundledFile(Path.Combine("mac", "tools", "ollama", MacToolCatalog.Ollama.ArchiveFileName))
            ?? throw new FileNotFoundException("Bundled macOS Ollama archive was not found.");

        // MAC38: load the bundled mac-tools-manifest.json that PrereqFetch
        // wrote at CI time. It carries the dynamically-resolved version + URL
        // + vendor-published SHA-256 (from the upstream sha256sum.txt) for
        // the bundled archive. There is no static pin here anymore.
        var bundledManifestPath = ResolveBundledFile(Path.Combine("mac", "tools", "ollama", MacToolCatalog.ManifestFileName))
            ?? throw new FileNotFoundException("Bundled mac-tools-manifest.json was not found alongside the Ollama archive.");
        var bundledMetadata = LoadBundledMacOllamaMetadata(bundledManifestPath);

        var cacheArchive = Path.Combine(ssdRoot, SsdLayout.Cache, MacToolCatalog.Ollama.ArchiveFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(cacheArchive)!);
        File.Copy(bundledArchive, cacheArchive, overwrite: true);

        // SHA-256 gate on the bundled archive before extraction. Compares the
        // bytes on disk against the manifest's vendor-published hash; a
        // tampered or substituted zip is rejected before extraction.
        var hashValidation = OllamaPackageTrustPolicy.ValidateDownloadedPackage(cacheArchive, bundledMetadata);
        if (!hashValidation.IsTrusted)
        {
            onLog($"Refused macOS Ollama staging: {hashValidation.Message} (actual={hashValidation.ActualSha256 ?? "unknown"})");
            throw new InvalidOperationException(hashValidation.Message);
        }
        var actualSha = hashValidation.ActualSha256 ?? bundledMetadata.Sha256;

        var ollamaDir = Path.Combine(ssdRoot, SsdLayout.MacOllama);
        if (Directory.Exists(ollamaDir))
            Directory.Delete(ollamaDir, recursive: true);

        Directory.CreateDirectory(ollamaDir);
        ZipFile.ExtractToDirectory(cacheArchive, ollamaDir, overwriteFiles: true);

        // MAC26: the macOS Ollama distribution is a GUI app bundle (Ollama.app),
        // not a CLI server like Linux/Windows. The self-contained Go server
        // lives at Ollama.app/Contents/Resources/ollama and runs cleanly as a
        // direct child process. Newer upstream archives (v0.20.7+) no longer
        // ship a top-level LaunchServices shim, but older archives did, so
        // best-effort delete it if present.
        var innerCliPath = Path.Combine(ollamaDir, "Ollama.app", "Contents", "Resources", "ollama");
        if (!File.Exists(innerCliPath))
            throw new FileNotFoundException(
                $"Expected macOS Ollama server binary at {innerCliPath} after extraction; the upstream archive layout may have changed.");

        var topLevelShim = Path.Combine(ollamaDir, "ollama");
        if (File.Exists(topLevelShim))
        {
            try { File.Delete(topLevelShim); } catch { /* best effort */ }
        }

        // Verify the staged payload (SHA-256 + arm64 slice) and write the
        // trust attestation that the runtime gate (Swift mac-runner +
        // MacOllamaLifecycleService) checks at launch. On failure, the
        // partially-staged directory is scrubbed so the next attempt starts
        // from a clean slate and the runtime gate keeps refusing to launch.
        var pipeline = MacOllamaStagingPipeline.VerifyAndAttest(ssdRoot, cacheArchive, innerCliPath, bundledMetadata);
        if (!pipeline.Success)
        {
            var failure = pipeline.Failure!;
            onLog($"Refused macOS Ollama staging: {failure.Message}");
            try { Directory.Delete(ollamaDir, recursive: true); } catch { }
            throw new InvalidOperationException(failure.Message);
        }

        // Copy the bundled manifest alongside the binary on the SSD so the
        // runtime + auditors can see version/sourceUrl/sha256 without going
        // back to the bundle.
        File.Copy(bundledManifestPath, Path.Combine(ollamaDir, MacToolCatalog.ManifestFileName), overwrite: true);

        var manifest = JsonSerializer.Serialize(new
        {
            id = MacToolCatalog.Ollama.Id,
            version = bundledMetadata.Version,
            sourceUrl = bundledMetadata.Url,
            archive = MacToolCatalog.Ollama.ArchiveFileName,
            sha256 = actualSha,
            downloadedAtUtc = DateTime.UtcNow.ToString("O")
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(MacToolCatalog.GetManifestPath(ssdRoot), manifest, ct);

        onLog($"Staged macOS Ollama {bundledMetadata.Version} and wrote trust attestation.");
    }

    private static OllamaPackageMetadata LoadBundledMacOllamaMetadata(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        var url = root.TryGetProperty("sourceUrl", out var u) ? u.GetString() : null;
        var sha = root.TryGetProperty("sha256", out var s) ? s.GetString() : null;

        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException($"Bundled mac-tools-manifest.json missing version: {manifestPath}");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException($"Bundled mac-tools-manifest.json missing sourceUrl: {manifestPath}");
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64)
            throw new InvalidOperationException($"Bundled mac-tools-manifest.json missing or malformed sha256: {manifestPath}");

        return new OllamaPackageMetadata(version, url, sha.ToLowerInvariant());
    }

    public bool AreMacArtifactsAvailable(out string? problem)
    {
        var result = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        problem = result.MacArtifactsProblem;
        return result.MacArtifactsAvailable;
    }

    private static string? ResolveRunnerPublishDirectory()
    {
        foreach (var contentRoot in BundleContentRoots.Enumerate(AppContext.BaseDirectory))
        {
            // "runner" is the clean name under the new dependencies/ tree;
            // "runner-publish" is the legacy name (build.ps1 local dev + any
            // pre-restructure bundle).
            foreach (var folder in new[] { "runner", "runner-publish" })
            {
                var candidate = Path.Combine(contentRoot, folder);
                if (DirectoryContainsRunner(candidate))
                    return candidate;
            }
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
            return null;

        var buildConfigurations = new[] { "Release", "Debug" };
        foreach (var configuration in buildConfigurations)
        {
            var candidate = Path.Combine(repoRoot, "prep-app", "bin", configuration, "net8.0-windows", "runner-publish");
            if (DirectoryContainsRunner(candidate))
                return candidate;
        }

        return null;
    }


    private static string? ResolveCompanionPublishDirectory()
    {
        foreach (var contentRoot in BundleContentRoots.Enumerate(AppContext.BaseDirectory))
        {
            foreach (var folder in new[] { "companion-publish", "companion" })
            {
                var candidate = Path.Combine(contentRoot, folder);
                if (DirectoryContainsCompanion(candidate))
                    return candidate;
            }
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
        if (repoRoot is null)
            return null;

        var buildConfigurations = new[] { "Release", "Debug" };
        foreach (var configuration in buildConfigurations)
        {
            var candidate = Path.Combine(repoRoot, "prep-app", "bin", configuration, "net8.0-windows", "companion-publish");
            if (DirectoryContainsCompanion(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FindRepoRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FreeAiSsd.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    private static bool DirectoryContainsRunner(string path)
        => Directory.Exists(path) && File.Exists(Path.Combine(path, "FreeAiSsd.Runner.exe"));

    private static bool DirectoryContainsCompanion(string path)
        => Directory.Exists(path) && File.Exists(Path.Combine(path, "FreeAiSsd.Companion.exe"));

    internal static string? ResolveBundledFile(string relativePath)
        => ResolveBundledFile(AppContext.BaseDirectory, relativePath);

    internal static string? ResolveBundledFile(string baseDirectory, string relativePath)
    {
        foreach (var contentRoot in BundleContentRoots.Enumerate(baseDirectory))
        {
            var candidate = Path.Combine(contentRoot, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    internal static string? ResolveBundledDirectory(string relativePath)
        => ResolveBundledDirectory(AppContext.BaseDirectory, relativePath);

    internal static string? ResolveBundledDirectory(string baseDirectory, string relativePath)
    {
        foreach (var contentRoot in BundleContentRoots.Enumerate(baseDirectory))
        {
            var candidate = Path.Combine(contentRoot, relativePath);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

}
