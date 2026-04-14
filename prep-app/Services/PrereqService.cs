using System.IO;
using System.Net.Http;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class PrereqService : IPrereqService
{
    private readonly IDialogService _dialogService;

    public PrereqService(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public async Task StagePrerequisitesAsync(string root, Action<string> onLog, CancellationToken ct)
    {
        var ssdPrereqDir = Path.Combine(root, SsdLayout.Prereqs);
        Directory.CreateDirectory(ssdPrereqDir);

        var bundledPrereqDir = ResolveBundledPrereqDirectory();
        if (!Directory.Exists(bundledPrereqDir))
            throw new DirectoryNotFoundException($"Bundled prerequisites folder is missing: {bundledPrereqDir}");

        var bundledManifestPath = Path.Combine(bundledPrereqDir, PrereqCatalog.ManifestFileName);
        var ssdManifestPath = PrereqCatalog.GetManifestPath(root);

        var manifest = File.Exists(bundledManifestPath)
            ? PrereqManifest.Load(bundledManifestPath)
            : new PrereqManifest();

        foreach (var definition in PrereqCatalog.Tier1)
        {
            var sourcePath = Path.Combine(bundledPrereqDir, definition.TargetFileName);
            var targetPath = Path.Combine(ssdPrereqDir, definition.TargetFileName);
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Bundled installer is missing: {sourcePath}");

            File.Copy(sourcePath, targetPath, overwrite: true);
            onLog($"Prereqs: bundled {definition.DisplayName}");

            var entry = manifest.Prerequisites.FirstOrDefault(p => string.Equals(p.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = PrereqCatalog.CreateManifestEntry(definition, DownloadManager.ComputeSha256(targetPath), new FileInfo(targetPath).Length);
                manifest.Prerequisites.Add(entry);
            }
        }

        await manifest.SaveAsync(ssdManifestPath);
        onLog($"Wrote prerequisite manifest: {ssdManifestPath}");

        var bundleIssues = PrereqInstallValidator.ValidateBundleHealth(ssdPrereqDir, manifest);
        if (bundleIssues.Count > 0)
        {
            foreach (var issue in bundleIssues)
                onLog($"Prereq bundle issue: {issue}");

            if (_dialogService.ConfirmPrereqRefresh())
            {
                await DownloadPrereqsAsync(ssdPrereqDir, manifest, onLog, ct);
                await manifest.SaveAsync(ssdManifestPath);
            }
            else
            {
                onLog("Continuing with warning: offline prerequisite install may fail until prereqs are refreshed.");
            }
        }

        try
        {
            await DownloadPrereqsAsync(ssdPrereqDir, manifest, onLog, ct);
            await manifest.SaveAsync(ssdManifestPath);
        }
        catch (Exception ex)
        {
            onLog($"Prereq update check failed, using bundled installers: {ex.Message}");
        }
    }

    public async Task UpdatePrereqsOnlineAsync(string root, Action<string> onLog, CancellationToken ct)
    {
        var prereqDir = Path.Combine(root, SsdLayout.Prereqs);
        var manifestPath = PrereqCatalog.GetManifestPath(root);
        var manifest = PrereqManifest.Load(manifestPath);

        await DownloadPrereqsAsync(prereqDir, manifest, onLog, ct);
        await manifest.SaveAsync(manifestPath);
        onLog("Prereq update check complete.");
    }

    private static async Task DownloadPrereqsAsync(string prereqDir, PrereqManifest manifest,
        Action<string> onLog, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        foreach (var definition in PrereqCatalog.Tier1)
        {
            var destinationPath = Path.Combine(prereqDir, definition.TargetFileName);
            var tempPath = destinationPath + ".download";

            onLog($"Checking prerequisite update: {definition.DisplayName}");
            using var response = await client.GetAsync(definition.SourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs, ct);
            }

            var downloadedSha = DownloadManager.ComputeSha256(tempPath);
            var existingSha = File.Exists(destinationPath) ? DownloadManager.ComputeSha256(destinationPath) : string.Empty;

            if (string.Equals(downloadedSha, existingSha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tempPath);
                onLog($"Prereq already up-to-date: {definition.DisplayName}");
            }
            else
            {
                File.Move(tempPath, destinationPath, overwrite: true);
                onLog($"Updated prerequisite: {definition.DisplayName}");
            }

            var size = new FileInfo(destinationPath).Length;
            var existingEntry = manifest.Prerequisites.FirstOrDefault(p => string.Equals(p.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            var updated = PrereqCatalog.CreateManifestEntry(definition, DownloadManager.ComputeSha256(destinationPath), size);

            if (existingEntry is null)
            {
                manifest.Prerequisites.Add(updated);
            }
            else
            {
                existingEntry.DisplayName = updated.DisplayName;
                existingEntry.Filename = updated.Filename;
                existingEntry.SourceUrl = updated.SourceUrl;
                existingEntry.DownloadedAtUtc = updated.DownloadedAtUtc;
                existingEntry.Sha256 = updated.Sha256;
                existingEntry.SizeBytes = updated.SizeBytes;
                existingEntry.SilentArgs = updated.SilentArgs;
                existingEntry.RequiresAdmin = updated.RequiresAdmin;
                existingEntry.IsOptional = updated.IsOptional;
            }
        }
    }

    private static string ResolveBundledPrereqDirectory()
    {
        var rootCandidate = Path.Combine(AppContext.BaseDirectory, SsdLayout.Prereqs);
        if (Directory.Exists(rootCandidate))
            return rootCandidate;

        return Path.Combine(AppContext.BaseDirectory, "payload", SsdLayout.Prereqs);
    }
}
