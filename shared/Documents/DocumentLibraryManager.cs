using System.Text.Json;
using FreeAiSsd.Shared.Io;

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
        DocumentLibraryRegistry registry;
        if (!File.Exists(RegistryPath))
        {
            registry = new DocumentLibraryRegistry();
            return ReconcileRegistryWithDisk(registry);
        }

        try
        {
            var json = File.ReadAllText(RegistryPath);
            registry = JsonSerializer.Deserialize<DocumentLibraryRegistry>(json, JsonOptions()) ?? new DocumentLibraryRegistry();
        }
        catch
        {
            registry = new DocumentLibraryRegistry();
        }

        return ReconcileRegistryWithDisk(registry);
    }

    public async Task SaveRegistryAsync(DocumentLibraryRegistry registry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
        var tempPath = RegistryPath + ".tmp";
        var json = JsonSerializer.Serialize(registry, JsonOptions());
        await File.WriteAllTextAsync(tempPath, json);
        if (File.Exists(RegistryPath))
        {
            FileOps.ReplaceWithRetry(tempPath, RegistryPath, null);
        }
        else
        {
            File.Move(tempPath, RegistryPath);
        }
    }

    public async Task<DocumentLibraryManifest> CreateLibraryAsync(string name)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new ArgumentException("Library name cannot be empty.");
        }

        var registry = LoadRegistry();
        var duplicate = registry.Libraries.Any(l =>
            string.Equals(l.Name?.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw new InvalidOperationException($"A library named '{trimmedName}' already exists. Choose a different name.");
        }

        var id = $"lib-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        var entry = new DocumentLibraryEntry
        {
            Id = id,
            Name = trimmedName,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
        registry.Libraries.Add(entry);
        registry.ActiveLibraryId = id;
        await SaveRegistryAsync(registry);

        var manifest = new DocumentLibraryManifest
        {
            Id = id,
            Name = trimmedName,
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
            return JsonSerializer.Deserialize<DocumentLibraryManifest>(json, JsonOptions()) ?? new DocumentLibraryManifest { Id = libraryId };
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
            FileOps.ReplaceWithRetry(tempPath, path, null);
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

    private static JsonSerializerOptions JsonOptions() => new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private DocumentLibraryRegistry ReconcileRegistryWithDisk(DocumentLibraryRegistry registry)
    {
        var normalized = new DocumentLibraryRegistry
        {
            ActiveLibraryId = registry.ActiveLibraryId
        };

        var byId = new Dictionary<string, DocumentLibraryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in registry.Libraries.Where(e => !string.IsNullOrWhiteSpace(e.Id)))
        {
            var key = entry.Id.Trim();
            if (!byId.TryGetValue(key, out var existing) || existing.UpdatedAtUtc < entry.UpdatedAtUtc)
            {
                byId[key] = new DocumentLibraryEntry
                {
                    Id = key,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? key : entry.Name.Trim(),
                    CreatedAtUtc = entry.CreatedAtUtc,
                    UpdatedAtUtc = entry.UpdatedAtUtc
                };
            }
        }

        var librariesDir = Path.Combine(_ssdRoot, SsdLayout.DocLibraries);
        if (Directory.Exists(librariesDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(librariesDir))
            {
                var id = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var diskManifest = LoadManifest(id);
                var displayName = string.IsNullOrWhiteSpace(diskManifest.Name)
                    ? id
                    : diskManifest.Name.Trim();

                if (!byId.TryGetValue(id, out var entry))
                {
                    byId[id] = new DocumentLibraryEntry
                    {
                        Id = id,
                        Name = displayName,
                        CreatedAtUtc = diskManifest.CreatedAtUtc,
                        UpdatedAtUtc = diskManifest.UpdatedAtUtc
                    };
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    entry.Name = displayName;
                }
            }
        }

        normalized.Libraries = byId.Values
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized;
    }
}
