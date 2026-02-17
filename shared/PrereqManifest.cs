using System.Text.Json;

namespace FreeAiSsd.Shared;

public sealed class PrereqManifest
{
    public List<PrereqManifestEntry> Prerequisites { get; set; } = new();

    public static PrereqManifest Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PrereqManifest();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PrereqManifest>(json, JsonOptions()) ?? new PrereqManifest();
        }
        catch
        {
            return new PrereqManifest();
        }
    }

    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonOptions());
        await File.WriteAllTextAsync(path, json);
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

public sealed class PrereqManifestEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Filename { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public DateTime DownloadedAtUtc { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string SilentArgs { get; set; } = string.Empty;
    public bool RequiresAdmin { get; set; }
    public bool IsOptional { get; set; }
}
