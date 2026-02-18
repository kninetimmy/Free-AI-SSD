using System.Reflection;

namespace FreeAiSsd.PrepApp;

public sealed record StarterModelCatalog
{
    public int SchemaVersion { get; init; }
    public List<StarterModelEntry> Models { get; init; } = new();
}

public sealed record StarterModelEntry
{
    public string Tag { get; init; } = string.Empty;
    public string Params { get; init; } = string.Empty;
    public string SizeTier { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> UseCases { get; init; } = new();
}

public sealed record StarterModelCatalogLoadResult(StarterModelCatalog Catalog, string? Warning);

public static class StarterModelCatalogLoader
{
    private const string RelativeCatalogPath = "Resources/starter-models.json";
    private const string EmbeddedCatalogResourceName = "FreeAiSsd.PrepApp.Resources.starter-models.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static StarterModelCatalogLoadResult Load(string baseDirectory)
    {
        var warningReasons = new List<string>();
        var filePath = Path.Combine(baseDirectory, RelativeCatalogPath);

        if (File.Exists(filePath))
        {
            try
            {
                var fileCatalog = DeserializeCatalog(File.ReadAllText(filePath));
                if (fileCatalog is not null)
                {
                    return new StarterModelCatalogLoadResult(fileCatalog, null);
                }

                warningReasons.Add($"Catalog file is empty or invalid at '{filePath}'.");
            }
            catch (Exception ex)
            {
                warningReasons.Add($"Catalog file failed to parse at '{filePath}' ({ex.Message}).");
            }
        }
        else
        {
            warningReasons.Add($"Catalog file not found at '{filePath}'.");
        }

        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedCatalogResourceName);
            if (stream is null)
            {
                warningReasons.Add($"Embedded fallback resource '{EmbeddedCatalogResourceName}' not found.");
            }
            else
            {
                using var reader = new StreamReader(stream);
                var fallbackCatalog = DeserializeCatalog(reader.ReadToEnd());
                if (fallbackCatalog is not null)
                {
                    var warning = "Starter catalog loaded from embedded fallback. " + string.Join(" ", warningReasons);
                    return new StarterModelCatalogLoadResult(fallbackCatalog, warning);
                }

                warningReasons.Add("Embedded fallback catalog is invalid.");
            }
        }
        catch (Exception ex)
        {
            warningReasons.Add($"Embedded fallback failed to load ({ex.Message}).");
        }

        var failedWarning = "Starter model catalog unavailable. Free-form model entry remains available. " + string.Join(" ", warningReasons);
        return new StarterModelCatalogLoadResult(new StarterModelCatalog { SchemaVersion = 1 }, failedWarning);
    }

    private static StarterModelCatalog? DeserializeCatalog(string json)
    {
        var catalog = JsonSerializer.Deserialize<StarterModelCatalog>(json, JsonOptions);
        if (catalog is null)
        {
            return null;
        }

        var validModels = catalog.Models
            .Where(m => !string.IsNullOrWhiteSpace(m.Tag)
                        && !string.IsNullOrWhiteSpace(m.Params)
                        && !string.IsNullOrWhiteSpace(m.SizeTier)
                        && !string.IsNullOrWhiteSpace(m.Description))
            .Select(m => m with
            {
                Tag = m.Tag.Trim(),
                Params = m.Params.Trim(),
                SizeTier = m.SizeTier.Trim(),
                Description = m.Description.Trim(),
                UseCases = m.UseCases
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .ToList();

        if (validModels.Count == 0)
        {
            return null;
        }

        return catalog with { Models = validModels };
    }
}
