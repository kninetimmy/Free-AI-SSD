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

        var sourceRunnerZip = ResolveBundledFile(Path.Combine("mac", "Runner.app.zip"))
            ?? throw new FileNotFoundException("Bundled macOS Runner.app archive was not found.");
        var macRoot = Path.Combine(ssdRoot, SsdLayout.Mac);
        Directory.CreateDirectory(macRoot);
        var targetZip = Path.Combine(macRoot, "Runner.app.zip");
        File.Copy(sourceRunnerZip, targetZip, overwrite: true);

        var extractedRunner = Path.Combine(macRoot, "Runner.app");
        if (Directory.Exists(extractedRunner))
            Directory.Delete(extractedRunner, recursive: true);

        ZipFile.ExtractToDirectory(targetZip, macRoot, overwriteFiles: true);
        onLog("Staged macOS Runner.app and archive.");
        await Task.CompletedTask;
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

        var bundledArchive = ResolveBundledFile(Path.Combine("mac", "tools", "ollama", "ollama-darwin.zip"))
            ?? throw new FileNotFoundException("Bundled macOS Ollama archive was not found.");
        var cacheArchive = Path.Combine(ssdRoot, SsdLayout.Cache, "ollama-darwin.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(cacheArchive)!);
        File.Copy(bundledArchive, cacheArchive, overwrite: true);

        // SHA-256 gate on the bundled archive before extraction. Pinned to
        // OllamaPackageTrustPolicy.DefaultMacPackage so a tampered or
        // unrelated zip never lands on the SSD.
        var pinnedMac = OllamaPackageTrustPolicy.DefaultMacPackage;
        var hashValidation = OllamaPackageTrustPolicy.ValidateDownloadedPackage(cacheArchive, pinnedMac);
        if (!hashValidation.IsTrusted)
        {
            onLog($"Refused macOS Ollama staging: {hashValidation.Message} (actual={hashValidation.ActualSha256 ?? "unknown"})");
            throw new InvalidOperationException(hashValidation.Message);
        }
        var actualSha = hashValidation.ActualSha256 ?? pinnedMac.Sha256;

        var ollamaDir = Path.Combine(ssdRoot, SsdLayout.MacOllama);
        if (Directory.Exists(ollamaDir))
            Directory.Delete(ollamaDir, recursive: true);

        Directory.CreateDirectory(ollamaDir);
        ZipFile.ExtractToDirectory(cacheArchive, ollamaDir, overwriteFiles: true);

        // MAC26: the macOS Ollama distribution is a GUI app bundle (Ollama.app),
        // not a CLI server like Linux/Windows. The 119 KB binary at the zip's
        // top level is a LaunchServices shim that strips env vars (so
        // OLLAMA_MODELS doesn't propagate) and headlessly-launches Ollama.app
        // via a SIGKILL-prone path. The actual self-contained 53 MB Go server
        // lives at Ollama.app/Contents/Resources/ollama and runs cleanly as a
        // direct child process. Stage that one; delete the top-level shim so
        // it can never be invoked by accident.
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
        var pipeline = MacOllamaStagingPipeline.VerifyAndAttest(ssdRoot, cacheArchive, innerCliPath);
        if (!pipeline.Success)
        {
            var failure = pipeline.Failure!;
            onLog($"Refused macOS Ollama staging: {failure.Message}");
            try { Directory.Delete(ollamaDir, recursive: true); } catch { }
            throw new InvalidOperationException(failure.Message);
        }

        var sourceManifest = ResolveBundledFile(Path.Combine("mac", "tools", "ollama", "mac-tools-manifest.json"));
        if (sourceManifest is not null && File.Exists(sourceManifest))
            File.Copy(sourceManifest, Path.Combine(ollamaDir, "mac-tools-manifest.json"), overwrite: true);

        var manifest = JsonSerializer.Serialize(new
        {
            id = MacToolCatalog.Ollama.Id,
            sourceUrl = MacToolCatalog.Ollama.SourceUrl,
            archive = MacToolCatalog.Ollama.ArchiveFileName,
            sha256 = actualSha,
            downloadedAtUtc = DateTime.UtcNow.ToString("O")
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(MacToolCatalog.GetManifestPath(ssdRoot), manifest, ct);

        onLog("Staged macOS Ollama runtime and wrote trust attestation.");
    }

    public bool AreMacArtifactsAvailable(out string? problem)
    {
        var result = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
        problem = result.MacArtifactsProblem;
        return result.MacArtifactsAvailable;
    }

    private static string? ResolveRunnerPublishDirectory()
    {
        foreach (var contentRoot in EnumerateBundledContentRoots())
        {
            var candidate = Path.Combine(contentRoot, "runner-publish");
            if (DirectoryContainsRunner(candidate))
                return candidate;
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
        foreach (var contentRoot in EnumerateBundledContentRoots())
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
        foreach (var contentRoot in EnumerateBundledContentRoots(baseDirectory))
        {
            var candidate = Path.Combine(contentRoot, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateBundledContentRoots()
        => EnumerateBundledContentRoots(AppContext.BaseDirectory);

    private static IEnumerable<string> EnumerateBundledContentRoots(string baseDirectory)
    {
        yield return baseDirectory;
        yield return Path.Combine(baseDirectory, "payload");

        // MAC23: mirror MAC22 — Mac PrepApp's mac-prep-host sidecar runs from
        // PrepApp.app/Contents/Resources/prep-host/, so AppContext.BaseDirectory
        // is *not* the bundle root and the bundled artifacts under
        // <bundle>/payload/mac/ live several levels up. Walk a bounded number
        // of ancestors. Backward-compatible: Windows finds bundles on the
        // first or second candidate above and never enters this loop.
        DirectoryInfo? cursor;
        try { cursor = new DirectoryInfo(baseDirectory); }
        catch { yield break; }

        for (var i = 0; i < 6 && cursor?.Parent is not null; i++)
        {
            cursor = cursor.Parent;
            yield return cursor.FullName;
            yield return Path.Combine(cursor.FullName, "payload");
        }
    }
}
