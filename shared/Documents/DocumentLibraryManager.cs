using System.Text.Json;

namespace FreeAiSsd.Shared.Documents;

public sealed class DocumentLibraryManager
{
    private readonly string _ssdRoot;

    public DocumentLibraryManager(string ssdRoot)
    {
        _ssdRoot = ssdRoot;
        Directory.CreateDirectory(Path.Combine(_ssdRoot, SsdLayout.Docs));
        Directory.CreateDirectory(Path.Combine(_ssdRoot, SsdLayout.DocLibraries));
    }

    public string RegistryPath => Path.Combine(_ssdRoot, SsdLayout.DocLibrariesRegistry);

    public DocumentLibraryRegistry LoadRegistry()
    {
        if (!File.Exists(RegistryPath))
        {
            return new DocumentLibraryRegistry();
        }

        try
        {
            var json = File.ReadAllText(RegistryPath);
            return JsonSerializer.Deserialize<DocumentLibraryRegistry>(json) ?? new DocumentLibraryRegistry();
        }
        catch
        {
            return new DocumentLibraryRegistry();
        }
    }

    public async Task SaveRegistryAsync(DocumentLibraryRegistry registry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        var tempPath = RegistryPath + ".tmp";
        var json = JsonSerializer.Serialize(registry, JsonOptions());
        await File.WriteAllTextAsync(tempPath, json);
        if (File.Exists(RegistryPath))
        {
            File.Replace(tempPath, RegistryPath, null);
        }
        else
        {
            File.Move(tempPath, RegistryPath);
        }
    }

    public async Task<DocumentLibraryManifest> CreateLibraryAsync(string name)
    {
        var registry = LoadRegistry();
        var id = $"lib-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var entry = new DocumentLibraryEntry
        {
            Id = id,
            Name = name,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        registry.Libraries.Add(entry);
        registry.ActiveLibraryId = id;
        await SaveRegistryAsync(registry);

        var manifest = new DocumentLibraryManifest
        {
            Id = id,
            Name = name,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        await SaveManifestAsync(manifest);
        EnsureLibraryFolders(id);
        return manifest;
    }

    public void EnsureLibraryFolders(string libraryId)
    {
        Directory.CreateDirectory(Path.Combine(GetLibraryPath(libraryId), "files"));
        Directory.CreateDirectory(Path.Combine(GetLibraryPath(libraryId), "index"));
    }

    public string GetLibraryPath(string libraryId) => Path.Combine(_ssdRoot, SsdLayout.DocLibraries, libraryId);
    public string GetFilesPath(string libraryId) => Path.Combine(GetLibraryPath(libraryId), "files");
    public string GetIndexPath(string libraryId) => Path.Combine(GetLibraryPath(libraryId), "index");
    public string GetManifestPath(string libraryId) => Path.Combine(GetLibraryPath(libraryId), "library.json");

    public DocumentLibraryManifest LoadManifest(string libraryId)
    {
        var path = GetManifestPath(libraryId);
        if (!File.Exists(path))
        {
            return new DocumentLibraryManifest { Id = libraryId };
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DocumentLibraryManifest>(json) ?? new DocumentLibraryManifest { Id = libraryId };
        }
        catch
        {
            return new DocumentLibraryManifest { Id = libraryId };
        }
    }

    public async Task SaveManifestAsync(DocumentLibraryManifest manifest)
    {
        EnsureLibraryFolders(manifest.Id);
        var path = GetManifestPath(manifest.Id);
        var tempPath = path + ".tmp";
        manifest.UpdatedAtUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(manifest, JsonOptions());
        await File.WriteAllTextAsync(tempPath, json);
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
        }
        else
        {
            File.Move(tempPath, path);
        }

        var registry = LoadRegistry();
        var existing = registry.Libraries.FirstOrDefault(x => x.Id == manifest.Id);
        if (existing is not null)
        {
            existing.Name = manifest.Name;
            existing.UpdatedAtUtc = DateTime.UtcNow;
        }
        await SaveRegistryAsync(registry);
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
}
