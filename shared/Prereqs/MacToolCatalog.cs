namespace FreeAiSsd.Shared;

/// <summary>
/// Immutable definition of a macOS tool to be bundled on the SSD. The source
/// URL is no longer pinned here — it's resolved from the upstream release at
/// CI bundling time via <c>PrereqResolver.ResolveLatestOllamaMacAsync</c> and
/// recorded in the bundled <c>mac-tools-manifest.json</c>.
/// </summary>
public sealed record MacToolDefinition(string Id, string ArchiveFileName);

/// <summary>
/// Static catalog of macOS-specific tools bundled on the SSD.
/// Currently contains only the Ollama universal binary for macOS.
/// </summary>
public static class MacToolCatalog
{
    /// <summary>Standard filename for the macOS tools manifest.</summary>
    public const string ManifestFileName = "mac-tools-manifest.json";

    /// <summary>
    /// Ollama for macOS — downloaded as a universal (ARM64 + x86_64) ZIP
    /// archive from the official GitHub releases. The exact version + URL
    /// + SHA-256 is resolved dynamically at CI bundling time and persisted
    /// alongside the archive in <c>mac-tools-manifest.json</c>.
    /// </summary>
    public static MacToolDefinition Ollama { get; } = new(
        "ollama_macos_universal",
        "ollama-darwin.zip");

    /// <summary>
    /// Returns the full path to the macOS tools manifest file for a given SSD root.
    /// </summary>
    public static string GetManifestPath(string rootPath)
        => Path.Combine(rootPath, SsdLayout.MacOllama, ManifestFileName);
}
