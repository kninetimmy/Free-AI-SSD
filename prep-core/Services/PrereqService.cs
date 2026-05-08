using System.Net.Http;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Prereqs;
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

    /// <summary>
    /// Phase-A hardening: resolves the latest-stable upstream version + vendor
    /// hash at runtime via <see cref="PrereqResolver"/> instead of using the
    /// hardcoded <see cref="PrereqDefinition.SourceUrl"/> values. Every download
    /// is HTTPS-only, hash-verified (SHA-512 for .NET, SHA-256 when the upstream
    /// publishes one), and fails closed on any resolve / network / hash error.
    /// This is the same code path CI uses via tools/FreeAiSsd.PrereqFetch.
    /// </summary>
    private static async Task DownloadPrereqsAsync(string prereqDir, PrereqManifest manifest,
        Action<string> onLog, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FreeAiSsd-PrepApp/1.0 (+https://github.com/kninetimmy/free-ai-ssd)");

        foreach (var definition in PrereqCatalog.Tier1)
        {
            onLog($"Checking prerequisite update: {definition.DisplayName}");

            var resolution = await ResolveAsync(definition, client, ct);
            onLog($"Resolved {definition.Id}: version={resolution.Version} ({resolution.TrustNote})");

            var destinationPath = Path.Combine(prereqDir, definition.TargetFileName);
            var existingSha = File.Exists(destinationPath)
                ? DownloadManager.ComputeSha256(destinationPath)
                : string.Empty;

            var observedSha = await PrereqResolver.DownloadAndVerifyAsync(
                client, resolution, destinationPath, onLog, ct);

            if (string.Equals(observedSha, existingSha, StringComparison.OrdinalIgnoreCase))
            {
                onLog($"Prereq already up-to-date: {definition.DisplayName}");
            }
            else
            {
                onLog($"Updated prerequisite: {definition.DisplayName}");
            }

            var size = new FileInfo(destinationPath).Length;
            var existingEntry = manifest.Prerequisites.FirstOrDefault(
                p => string.Equals(p.Id, definition.Id, StringComparison.OrdinalIgnoreCase));

            // Use the resolved URL (not the catalog pin) so the manifest reflects
            // exactly what was downloaded and verified.
            var updated = new PrereqManifestEntry
            {
                Id = definition.Id,
                DisplayName = definition.DisplayName,
                Filename = definition.TargetFileName,
                SourceUrl = resolution.Url,
                DownloadedAtUtc = DateTime.UtcNow,
                Sha256 = observedSha,
                SizeBytes = size,
                SilentArgs = definition.SilentArgs,
                RequiresAdmin = definition.RequiresAdmin,
                IsOptional = definition.IsOptional,
            };

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

    /// <summary>
    /// Dispatches to the correct resolver by catalog id. New Tier-1 entries must
    /// be added here explicitly — we refuse to fall back to a hardcoded URL,
    /// which is the whole point of the runtime-resolve model.
    /// </summary>
    private static Task<PrereqResolution> ResolveAsync(
        PrereqDefinition definition, HttpClient client, CancellationToken ct)
    {
        return definition.Id switch
        {
            PrereqCatalog.VcRedistX64Id => Task.FromResult(PrereqResolver.ResolveVcRedistX64()),
            PrereqCatalog.DotnetDesktop8X64Id => PrereqResolver.ResolveDotnet8DesktopX64Async(client, ct),
            _ => throw new InvalidOperationException(
                $"No runtime resolver registered for prereq id '{definition.Id}'. " +
                "Add one in PrereqService.ResolveAsync before bundling this prereq."),
        };
    }

    internal static string ResolveBundledPrereqDirectory()
        => ResolveBundledPrereqDirectory(AppContext.BaseDirectory);

    internal static string ResolveBundledPrereqDirectory(string baseDirectory)
    {
        // MAC24: mirror MAC22 / MAC23. Mac PrepApp's mac-prep-host sidecar
        // runs from PrepApp.app/Contents/Resources/prep-host/, so
        // AppContext.BaseDirectory is *not* the bundle root and the bundled
        // windows/tools/prereqs/ folder lives several levels up. Walk a
        // bounded number of ancestors. Backward-compatible: Windows finds
        // the prereqs folder on the first or second candidate and never
        // enters the loop. Returns the first candidate that exists; if
        // none exist, returns the canonical "<base>/payload/<prereqs>"
        // path so the caller's Directory.Exists check produces the same
        // diagnostic error message as before.
        foreach (var contentRoot in EnumerateBundleRoots(baseDirectory))
        {
            var candidate = Path.Combine(contentRoot, SsdLayout.Prereqs);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(baseDirectory, "payload", SsdLayout.Prereqs);
    }

    private static IEnumerable<string> EnumerateBundleRoots(string baseDirectory)
    {
        yield return baseDirectory;
        yield return Path.Combine(baseDirectory, "payload");

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
