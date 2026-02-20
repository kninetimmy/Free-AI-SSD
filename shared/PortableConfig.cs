using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.Shared;

/// <summary>
/// Tracks the installation lifecycle of a model on the portable SSD.
/// </summary>
public enum ModelInstallStatus
{
    NotInstalled,
    Downloading,
    Installed,
    Failed
}

/// <summary>
/// Configuration entry for a single LLM model on the SSD, tracking its
/// installation status, integrity hash, file size, and last verification timestamp.
/// </summary>
public sealed class ModelConfigEntry
{
    public string Name { get; set; } = string.Empty;
    public ModelInstallStatus Status { get; set; } = ModelInstallStatus.NotInstalled;
    /// <summary>SHA-256 hash of the model's primary blob, used for integrity verification.</summary>
    public string? Sha256 { get; set; }
    /// <summary>Total size of the model in bytes, if known.</summary>
    public long? SizeBytes { get; set; }
    /// <summary>UTC timestamp of the last successful integrity verification.</summary>
    public DateTime? LastVerifiedUtc { get; set; }
}

/// <summary>
/// Root configuration file for the portable SSD, stored at config/portable-config.json.
/// Contains the Ollama server settings, list of installed models, encryption state,
/// and preparation metadata. Uses atomic file writes (write-to-temp then rename)
/// to prevent corruption during unexpected shutdowns.
/// </summary>
public sealed class PortableConfig
{
    /// <summary>Config schema version for forward compatibility.</summary>
    public string Version { get; set; } = "1.0";
    /// <summary>Preferred TCP port for the local Ollama server (default: 11434).</summary>
    public int OllamaPort { get; set; } = 11434;
    /// <summary>Relative path from SSD root to the Ollama executable.</summary>
    public string OllamaRelativePath { get; set; } = @"windows\tools\ollama\ollama.exe";
    /// <summary>List of model entries with their status and integrity data.</summary>
    public List<ModelConfigEntry> Models { get; set; } = new();
    /// <summary>Preferred compute mode: "cpu", "cuda", or "rocm".</summary>
    public string PreferredCompute { get; set; } = "cpu";
    /// <summary>UTC timestamp when the SSD was initially prepared.</summary>
    public DateTime PreparedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Whether the config has been encrypted with a user password.</summary>
    public bool IsEncrypted { get; set; }
    /// <summary>Encryption algorithm identifier (e.g., "AES-256-GCM") when encrypted.</summary>
    public string? EncryptionScheme { get; set; }

    /// <summary>Active reference document library ID (or null for disabled RAG).</summary>
    public string? ActiveDocumentLibraryId { get; set; }
    /// <summary>Number of chunks to retrieve per query.</summary>
    public int RetrievalTopK { get; set; } = 5;
    /// <summary>Chunk size (characters) used during indexing.</summary>
    public int ChunkSize { get; set; } = 1200;
    /// <summary>Chunk overlap (characters) used during indexing.</summary>
    public int ChunkOverlap { get; set; } = 200;
    /// <summary>Embedding model name served by local Ollama.</summary>
    public string EmbeddingModelName { get; set; } = "nomic-embed-text";

    /// <summary>Standard relative path for the config file within the SSD structure.</summary>
    [JsonIgnore]
    public string ConfigRelativePath => @"config\\portable-config.json";

    /// <summary>
    /// Loads a config from disk with explicit validity reporting.
    /// Returns a default config with IsValid=false if the file is missing or corrupt.
    /// </summary>
    public static (PortableConfig Config, bool IsValid) LoadWithValidation(string path)
    {
        if (!File.Exists(path))
        {
            return (new PortableConfig(), false);
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<PortableConfig>(json, JsonOptions());
            return (config ?? new PortableConfig(), config is not null);
        }
        catch
        {
            return (new PortableConfig(), false);
        }
    }

    /// <summary>
    /// Async version of LoadWithValidation for use in UI-bound contexts.
    /// </summary>
    public static async Task<(PortableConfig Config, bool IsValid)> LoadWithValidationAsync(string path)
    {
        if (!File.Exists(path))
        {
            return (new PortableConfig(), false);
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var config = JsonSerializer.Deserialize<PortableConfig>(json, JsonOptions());
            return (config ?? new PortableConfig(), config is not null);
        }
        catch
        {
            return (new PortableConfig(), false);
        }
    }

    /// <summary>
    /// Convenience loader that discards validation status and returns defaults on failure.
    /// </summary>
    public static PortableConfig Load(string path)
    {
        var (config, _) = LoadWithValidation(path);
        return config;
    }

    /// <summary>
    /// Async convenience loader that discards validation status.
    /// </summary>
    public static async Task<PortableConfig> LoadAsync(string path)
    {
        var (config, _) = await LoadWithValidationAsync(path);
        return config;
    }

    /// <summary>
    /// Synchronous save wrapper. Blocks the calling thread.
    /// </summary>
    public void Save(string path)
    {
        SaveAsync(path).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Persists the config to disk using an atomic write pattern:
    /// 1. Serialize to a temporary ".tmp" file.
    /// 2. Replace the original file atomically (or move if new).
    /// This prevents partial writes from corrupting the config.
    /// </summary>
    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonOptions());
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, json);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    /// <summary>
    /// Standard JSON serialization options: indented, camelCase properties, string enums.
    /// </summary>
    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
