using System.Text.Json;
using System.Text.Json.Serialization;

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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed class PrereqManifestEntry
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string? Sha256 { get; set; }
    public string SilentArgs { get; set; } = string.Empty;
    public bool RequiresAdmin { get; set; }
}

public static class PrereqCatalog
{
    public const string VcRedistX64Id = "vcredist_x64";
    public const string VcRedistX64Url = "https://aka.ms/vs/17/release/vc_redist.x64.exe";
    public const string VcRedistX64File = "vc_redist.x64.exe";

    public static PrereqManifestEntry CreateVcRedistEntry() => new()
    {
        Id = VcRedistX64Id,
        DisplayName = "Microsoft Visual C++ Redistributable (x64)",
        File = VcRedistX64File,
        SilentArgs = "/install /quiet /norestart",
        RequiresAdmin = true
    };
}
