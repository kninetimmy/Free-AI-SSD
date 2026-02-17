namespace FreeAiSsd.Shared;

public sealed record MacToolDefinition(string Id, string SourceUrl, string ArchiveFileName);

public static class MacToolCatalog
{
    public const string ManifestFileName = "mac-tools-manifest.json";

    public static MacToolDefinition Ollama { get; } = new(
        "ollama_macos_universal",
        "https://github.com/ollama/ollama/releases/latest/download/ollama-darwin.zip",
        "ollama-darwin.zip");

    public static string GetManifestPath(string rootPath)
        => Path.Combine(rootPath, SsdLayout.MacOllama, ManifestFileName);
}
