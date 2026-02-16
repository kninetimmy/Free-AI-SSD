using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeAiSsd.Shared;

public sealed class PortableConfig
{
    public string Version { get; set; } = "1.0";
    public int OllamaPort { get; set; } = 11434;
    public string OllamaRelativePath { get; set; } = @"tools\\ollama\\ollama.exe";
    public List<string> Models { get; set; } = new();
    public string PreferredCompute { get; set; } = "cpu";
    public DateTime PreparedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string ConfigRelativePath => @"config\\portable-config.json";

    public static PortableConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<PortableConfig>(json, JsonOptions())
               ?? throw new InvalidOperationException("Invalid portable config JSON.");
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonOptions());
        File.WriteAllText(path, json);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
