using System.Text.Json.Serialization;

namespace FreeAiSsd.PrepApp;

/// <summary>
/// Result of checking whether macOS preparation artifacts are available.
/// Contains a boolean flag and an optional problem description when unavailable.
/// </summary>
public sealed record MacArtifactAvailabilityResult(bool MacArtifactsAvailable, string? MacArtifactsProblem)
{
    public static MacArtifactAvailabilityResult Available() => new(true, null);
    public static MacArtifactAvailabilityResult Unavailable(string problem) => new(false, problem);
}

/// <summary>
/// Checks whether the macOS preparation artifacts (Runner.app, Ollama binary)
/// are present alongside the PrepApp. These artifacts are only included in
/// the "Cross-platform Beta" download bundle.
///
/// Validation steps:
/// 1. Check for mac/mac-artifacts.manifest.json presence.
/// 2. Parse the manifest and validate its schema version.
/// 3. Verify each referenced artifact file exists at its declared relative path.
/// 4. Ensure all relative paths are safe (no directory traversal).
/// </summary>
public static class MacArtifactAvailability
{
    public const string ManifestRelativePath = "mac/mac-artifacts.manifest.json";
    private const string MissingManifestMessage = "macOS preparation is available in the Cross-platform Beta download.";
    private const string IncompleteManifestMessage = "macOS artifacts are incomplete. Re-download the beta ZIP.";

    /// <summary>
    /// Evaluates whether macOS artifacts are available in the given app directory.
    /// Returns Available if all referenced files exist; Unavailable with a reason otherwise.
    /// </summary>
    /// <param name="appDirectory">The PrepApp's base directory (AppContext.BaseDirectory).</param>
    /// <returns>Availability result with problem description if unavailable.</returns>
    public static MacArtifactAvailabilityResult Evaluate(string appDirectory)
    {
        string? contentRoot = null;
        string? manifestPath = null;
        foreach (var candidateRoot in BundleContentRoots.Enumerate(appDirectory))
        {
            var candidateManifest = Path.Combine(candidateRoot, ManifestRelativePath);
            if (File.Exists(candidateManifest))
            {
                contentRoot = candidateRoot;
                manifestPath = candidateManifest;
                break;
            }
        }

        if (manifestPath is null || contentRoot is null)
            return MacArtifactAvailabilityResult.Unavailable(MissingManifestMessage);

        try
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<MacArtifactsManifest>(manifestJson);
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.Artifacts is null || manifest.Artifacts.Count == 0)
            {
                return MacArtifactAvailabilityResult.Unavailable(IncompleteManifestMessage);
            }

            // Verify each artifact file exists and its path is safe.
            foreach (var artifact in manifest.Artifacts)
            {
                if (string.IsNullOrWhiteSpace(artifact.RelativePath))
                {
                    return MacArtifactAvailabilityResult.Unavailable(IncompleteManifestMessage);
                }

                // Reject paths that escape the app directory (path traversal protection).
                if (!TryResolveUnderContentRoot(contentRoot, artifact.RelativePath, out var fullPath))
                {
                    return MacArtifactAvailabilityResult.Unavailable(IncompleteManifestMessage);
                }

                // Post-restructure the Mac apps ship UNZIPPED, so an
                // artifact entry can be a directory (Runner.app / PrepApp.app)
                // as well as a file (Ollama archive, manifest).
                if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
                {
                    return MacArtifactAvailabilityResult.Unavailable(IncompleteManifestMessage);
                }
            }
        }
        catch
        {
            return MacArtifactAvailabilityResult.Unavailable(IncompleteManifestMessage);
        }

        return MacArtifactAvailabilityResult.Available();
    }

    /// <summary>
    /// Safely resolves a relative path under the app directory.
    /// Rejects absolute paths and paths that escape the app directory
    /// via ".." traversal (after full path normalization).
    /// </summary>
    private static bool TryResolveUnderContentRoot(string contentRoot, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var normalizedAppDir = Path.GetFullPath(contentRoot);
        var normalizedAppDirWithSeparator = normalizedAppDir.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedAppDir
            : normalizedAppDir + Path.DirectorySeparatorChar;

        var combinedPath = Path.Combine(normalizedAppDir, relativePath);
        var normalizedArtifactPath = Path.GetFullPath(combinedPath);

        if (!normalizedArtifactPath.StartsWith(normalizedAppDirWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = normalizedArtifactPath;
        return true;
    }

    /// <summary>JSON schema for the macOS artifacts manifest file.</summary>
    private sealed class MacArtifactsManifest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("artifacts")]
        public List<MacArtifactEntry> Artifacts { get; init; } = new();
    }

    /// <summary>A single artifact entry in the manifest, with its ID and relative file path.</summary>
    private sealed class MacArtifactEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("relativePath")]
        public string RelativePath { get; init; } = string.Empty;
    }
}
