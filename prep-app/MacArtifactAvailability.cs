using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.PrepApp;

public sealed record MacArtifactAvailabilityResult(bool MacArtifactsAvailable, string? MacArtifactsProblem)
{
    public static MacArtifactAvailabilityResult Available() => new(true, null);
    public static MacArtifactAvailabilityResult Unavailable(string problem) => new(false, problem);
}

public static class MacArtifactAvailability
{
    public const string ManifestRelativePath = "mac/mac-artifacts.manifest.json";
    private const string MissingManifestMessage = "macOS preparation is available in the Cross-platform Beta download.";
    private const string IncompleteManifestMessage = "macOS artifacts are incomplete. Re-download the beta ZIP.";

    public static MacArtifactAvailabilityResult Evaluate(string appDirectory)
    {
        var manifestPath = Path.Combine(appDirectory, "mac", "mac-artifacts.manifest.json");
        if (!File.Exists(manifestPath))
        {
            return MacArtifactAvailabilityResult.Unavailable(MissingManifestMessage);
        }

        try
        {
            var manifestJson = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<MacArtifactsManifest>(manifestJson);
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.Artifacts is null || manifest.Artifacts.Count == 0)
            {
                return MacArtifactAvailabilityResult.Unavailable(IncompleteManifestMessage);
            }

            foreach (var artifact in manifest.Artifacts)
            {
                if (string.IsNullOrWhiteSpace(artifact.RelativePath))
                {
                    return MacArtifactAvailabilityResult.Unavailable(IncompleteManifestMessage);
                }

                if (!TryResolveUnderAppDirectory(appDirectory, artifact.RelativePath, out var fullPath))
                {
                    return MacArtifactAvailabilityResult.Unavailable(IncompleteManifestMessage);
                }

                if (!File.Exists(fullPath))
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

    private static bool TryResolveUnderAppDirectory(string appDirectory, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;

        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var normalizedAppDir = Path.GetFullPath(appDirectory);
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

    private sealed class MacArtifactsManifest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("artifacts")]
        public List<MacArtifactEntry> Artifacts { get; init; } = new();
    }

    private sealed class MacArtifactEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("relativePath")]
        public string RelativePath { get; init; } = string.Empty;
    }
}
