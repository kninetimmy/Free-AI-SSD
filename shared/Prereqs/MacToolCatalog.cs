namespace FreeAiSsd.Shared;

/// <summary>
/// Immutable definition of a macOS tool to be bundled on the SSD,
/// including its download URL and expected archive filename.
/// </summary>
public sealed record MacToolDefinition(string Id, string SourceUrl, string ArchiveFileName);

/// <summary>
/// Static catalog of macOS-specific tools bundled on the SSD.
/// Currently contains only the Ollama universal binary for macOS.
/// Provides the tool definition and manifest path resolution.
/// </summary>
public static class MacToolCatalog
{
    /// <summary>Standard filename for the macOS tools manifest.</summary>
    public const string ManifestFileName = "mac-tools-manifest.json";

    /// <summary>
    /// Ollama for macOS — downloaded as a universal (ARM64 + x86_64) ZIP archive
    /// from the official GitHub releases.
    /// </summary>
    public static MacToolDefinition Ollama { get; } = new(
        "ollama_macos_universal",
        "https://github.com/ollama/ollama/releases/latest/download/ollama-darwin.zip",
        "ollama-darwin.zip");

    /// <summary>
    /// Returns the full path to the macOS tools manifest file for a given SSD root.
    /// </summary>
    public static string GetManifestPath(string rootPath)
        => Path.Combine(rootPath, SsdLayout.MacOllama, ManifestFileName);
}
