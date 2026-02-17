using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.Shared;

public enum ModelInstallStatus
{
    NotInstalled,
    Downloading,
    Installed,
    Failed
}

public sealed class ModelConfigEntry
{
    public string Name { get; set; } = string.Empty;
    public ModelInstallStatus Status { get; set; } = ModelInstallStatus.NotInstalled;
    public string? Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public DateTime? LastVerifiedUtc { get; set; }
}

public sealed class PortableConfig
{
    public string Version { get; set; } = "1.0";
    public int OllamaPort { get; set; } = 11434;
    public string OllamaRelativePath { get; set; } = @"tools\\ollama\\ollama.exe";
    public List<ModelConfigEntry> Models { get; set; } = new();
    public string PreferredCompute { get; set; } = "cpu";
    public DateTime PreparedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string ConfigRelativePath => @"config\\portable-config.json";


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
    public static PortableConfig Load(string path)
    {
        var (config, _) = LoadWithValidation(path);
        return config;
    }

    public static async Task<PortableConfig> LoadAsync(string path)
    {
        var (config, _) = await LoadWithValidationAsync(path);
        return config;
    }

    public void Save(string path)
    {
        SaveAsync(path).GetAwaiter().GetResult();
    }

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

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}
