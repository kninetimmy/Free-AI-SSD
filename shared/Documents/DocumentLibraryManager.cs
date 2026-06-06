using System.Text.Json;
using System.Text.RegularExpressions;
using FreeAiSsd.Shared.Io;
using Microsoft.Data.Sqlite;

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

    public async Task<DocumentLibraryManifest> RenameLibraryAsync(string libraryId, string newName)
    {
        var trimmedName = newName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            throw new ArgumentException("Library name cannot be empty.");
        }

        var registry = LoadRegistry();
        if (!registry.Libraries.Any(l => l.Id == libraryId))
        {
            throw new InvalidOperationException($"Library '{libraryId}' not found.");
        }

        var duplicate = registry.Libraries.Any(l =>
            l.Id != libraryId &&
            string.Equals(l.Name?.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            throw new InvalidOperationException($"A library named '{trimmedName}' already exists. Choose a different name.");
        }

        var manifest = LoadManifest(libraryId);
        manifest.Name = trimmedName;
        await SaveManifestAsync(manifest); // also syncs the registry entry's name
        return manifest;
    }

    public async Task DeleteLibraryAsync(string libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            throw new ArgumentException("Library id cannot be empty.");
        }

        // Purge the on-disk folder first. Disk is the source of truth — LoadRegistry
        // re-adds any library whose folder still exists (ReconcileRegistryWithDisk), so
        // deleting the folder before saving the registry keeps both consistent even if
        // the delete throws partway.
        var libraryPath = GetLibraryPath(libraryId);
        var librariesRoot = Path.Combine(_ssdRoot, SsdLayout.DocLibraries);
        PathGuards.EnsureUnderRoot(librariesRoot, libraryPath);
        if (Directory.Exists(libraryPath))
        {
            // The index vectors.db is held open by a pooled SQLite connection; flush the
            // pool before deleting or the recursive delete fails with "file is being used
            // by another process" (same lock issue handled in RebuildIndexAsync).
            SqliteConnection.ClearAllPools();
            Directory.Delete(libraryPath, recursive: true);
        }

        var registry = LoadRegistry();
        registry.Libraries.RemoveAll(l => l.Id == libraryId);
        if (string.Equals(registry.ActiveLibraryId, libraryId, StringComparison.OrdinalIgnoreCase))
        {
            registry.ActiveLibraryId = null;
        }
        await SaveRegistryAsync(registry);
    }

    public void EnsureLibraryFolders(string libraryId)
    {
        Directory.CreateDirectory(Path.Combine(GetLibraryPath(libraryId), "files"));
        Directory.CreateDirectory(Path.Combine(GetLibraryPath(libraryId), "index"));
    }

    public string GetLibraryPath(string libraryId) => Path.Combine(_ssdRoot, SsdLayout.DocLibraries, ValidateLibraryId(libraryId));
    public string GetFilesPath(string libraryId) => Path.Combine(GetLibraryPath(libraryId), "files");
    public string GetIndexPath(string libraryId) => Path.Combine(GetLibraryPath(libraryId), "index");
    public string GetManifestPath(string libraryId) => Path.Combine(GetLibraryPath(libraryId), "library.json");

    private static readonly Regex LibraryIdPattern = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    // Defense-in-depth: libraryId arrives from the LAN route. Today's callers gate it
    // against the registry (always a safe slug or GUID), but reject anything that isn't
    // a bare slug here so the id can never compose a "..", absolute, or separator-bearing
    // path. All Get*Path helpers funnel through GetLibraryPath, so this is the chokepoint.
    private static string ValidateLibraryId(string libraryId)
    {
        if (string.IsNullOrEmpty(libraryId) || !LibraryIdPattern.IsMatch(libraryId))
        {
            throw new ArgumentException($"Invalid library id: '{libraryId}'.", nameof(libraryId));
        }
        return libraryId;
    }

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

    public IReadOnlyList<DocumentLibraryManifest> ScanProvenanceMismatches(string currentModel)
    {
        var registry = LoadRegistry();
        var mismatches = new List<DocumentLibraryManifest>();
        foreach (var entry in registry.Libraries)
        {
            var manifest = LoadManifest(entry.Id);
            if (string.IsNullOrEmpty(manifest.LastEmbeddingModel)) continue;
            if (string.Equals(manifest.LastEmbeddingModel, "unknown", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(manifest.LastEmbeddingModel, currentModel, StringComparison.OrdinalIgnoreCase))
                mismatches.Add(manifest);
        }
        return mismatches;
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
