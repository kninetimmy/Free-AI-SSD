using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.Runner.Services;

public sealed class DocumentOperationsService : IDocumentOperationsService
{
    private readonly DocumentLibraryManager _libraryManager;
    private readonly DocumentIngestor _documentIngestor;
    private readonly IConfigStore _configStore;

    public DocumentOperationsService(
        DocumentLibraryManager libraryManager,
        DocumentIngestor documentIngestor,
        IConfigStore configStore)
    {
        _libraryManager = libraryManager;
        _documentIngestor = documentIngestor;
        _configStore = configStore;
    }

    public event Action<string>? LogMessage;

    public LibraryDisplayInfo GetLibraryDisplayInfo(PortableConfig config)
    {
        var registry = _libraryManager.LoadRegistry();
        var options = new List<string> { "None" };
        options.AddRange(registry.Libraries.Select(l =>
            string.IsNullOrWhiteSpace(l.Name) ? l.Id : l.Name.Trim()));

        DocumentLibraryManifest? activeLibrary = null;
        var selectedIndex = 0;

        if (!string.IsNullOrWhiteSpace(config.ActiveDocumentLibraryId))
        {
            var matchIndex = registry.Libraries.FindIndex(
                x => x.Id == config.ActiveDocumentLibraryId);
            if (matchIndex >= 0)
            {
                selectedIndex = matchIndex + 1;
                activeLibrary = _libraryManager.LoadManifest(config.ActiveDocumentLibraryId!);
            }
        }

        return new LibraryDisplayInfo(options, selectedIndex, activeLibrary);
    }

    public string? GetLibraryIdByIndex(int selectedIndex)
    {
        if (selectedIndex <= 0) return null;

        var registry = _libraryManager.LoadRegistry();
        var idx = selectedIndex - 1;
        return idx >= 0 && idx < registry.Libraries.Count
            ? registry.Libraries[idx].Id
            : null;
    }

    public async Task<DocumentLibraryManifest?> SetActiveLibraryAsync(
        PortableConfig config, string ssdRoot, string? libraryId)
    {
        if (string.IsNullOrWhiteSpace(libraryId))
        {
            config.ActiveDocumentLibraryId = null;
            var regNone = _libraryManager.LoadRegistry();
            regNone.ActiveLibraryId = null;
            await _libraryManager.SaveRegistryAsync(regNone);
            await SaveConfigAsync(config, ssdRoot);
            return null;
        }

        config.ActiveDocumentLibraryId = libraryId;
        var reg = _libraryManager.LoadRegistry();
        reg.ActiveLibraryId = libraryId;
        await _libraryManager.SaveRegistryAsync(reg);
        await SaveConfigAsync(config, ssdRoot);
        return _libraryManager.LoadManifest(libraryId);
    }

    public async Task<DocumentLibraryManifest> CreateLibraryAsync(
        PortableConfig config, string ssdRoot, string name)
    {
        LogMessage?.Invoke($"Create library requested: '{name}'");
        var manifest = await _libraryManager.CreateLibraryAsync(name);
        config.ActiveDocumentLibraryId = manifest.Id;
        var reg = _libraryManager.LoadRegistry();
        reg.ActiveLibraryId = manifest.Id;
        await _libraryManager.SaveRegistryAsync(reg);
        await SaveConfigAsync(config, ssdRoot);
        var path = _libraryManager.GetLibraryPath(manifest.Id);
        LogMessage?.Invoke($"Created library: name='{manifest.Name}', id='{manifest.Id}', path='{path}'");
        LogMessage?.Invoke($"Selected library: {manifest.Name} ({manifest.Id})");
        return manifest;
    }

    public async Task IngestFilesAsync(
        DocumentLibraryManifest library, string[] filePaths, string host,
        PortableConfig config, Action<IndexingProgress>? progress = null)
    {
        await _documentIngestor.IngestFilesAsync(library, filePaths, host, config, progress);
    }

    public async Task<bool> AddWatchedFolderAsync(
        DocumentLibraryManifest library, string folderPath)
    {
        if (library.WatchedFolders.Contains(folderPath, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        library.WatchedFolders.Add(folderPath);
        await _libraryManager.SaveManifestAsync(library);
        return true;
    }

    public async Task SweepFoldersAsync(
        DocumentLibraryManifest library, string host,
        PortableConfig config, Action<IndexingProgress>? progress = null)
    {
        await _documentIngestor.SweepFoldersAsync(library, host, config, progress);
    }

    public async Task RebuildIndexAsync(
        DocumentLibraryManifest library, string host,
        PortableConfig config, Action<IndexingProgress>? progress = null)
    {
        await _documentIngestor.RebuildIndexAsync(library, host, config, progress);
    }

    public async Task RemoveFileAsync(DocumentLibraryManifest library, string storedRelativePath)
    {
        await _documentIngestor.RemoveFileAsync(library, storedRelativePath);
    }

    public async Task SaveConfigAsync(PortableConfig config, string ssdRoot)
    {
        await _configStore.SaveAsync(ssdRoot, config, CancellationToken.None);
    }
}
