using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Services;
using Microsoft.Data.Sqlite;

namespace FreeAiSsd.Tests;

public sealed class DocumentLibraryWorkflowTests : IDisposable
{
    private readonly string _tempRoot;

    public DocumentLibraryWorkflowTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"doclib-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        // The delete-purge test opens a real vectors.db; a pooled SQLite
        // connection can keep the file locked and block the temp-dir cleanup.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CreateLibrary_RefreshDisplay_IncludesAndSelectsNewLibrary()
    {
        var (service, _) = CreateServiceWithConfig("create-selected", out var config, out var ssdRoot);

        var manifest = await service.CreateLibraryAsync(config, ssdRoot, "My library");
        var info = service.GetLibraryDisplayInfo(config);

        Assert.Equal(manifest.Id, config.ActiveDocumentLibraryId);
        Assert.Contains("My library", info.Options);
        Assert.Equal("My library", info.Options[info.SelectedIndex]);
        Assert.Equal(manifest.Id, info.ActiveLibrary?.Id);
        Assert.Equal("My library", info.ActiveLibrary?.Name);
    }

    [Fact]
    public async Task CreateLibrary_DuplicateNameCaseInsensitive_IsRejected()
    {
        var (service, _) = CreateServiceWithConfig("duplicate", out var config, out var ssdRoot);
        await service.CreateLibraryAsync(config, ssdRoot, "Checklist");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateLibraryAsync(config, ssdRoot, "  checklist  "));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateLibrary_EmptyOrWhitespace_IsRejected()
    {
        var (service, _) = CreateServiceWithConfig("empty", out var config, out var ssdRoot);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateLibraryAsync(config, ssdRoot, "   \t  "));

        Assert.Contains("cannot be empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadRegistry_ExistingLibFolderWithoutRegistry_LoadsFriendlyName()
    {
        var ssdRoot = Path.Combine(_tempRoot, "ssd-legacy");
        SsdLayout.EnsureStructure(ssdRoot);
        var manager = new DocumentLibraryManager(ssdRoot);

        var legacyId = "lib-legacy-001";
        var legacyPath = manager.GetLibraryPath(legacyId);
        Directory.CreateDirectory(legacyPath);
        File.WriteAllText(
            Path.Combine(legacyPath, "library.json"),
            """
            {
              "id": "lib-legacy-001",
              "name": "Legacy Manuals",
              "createdAtUtc": "2026-01-01T00:00:00Z",
              "updatedAtUtc": "2026-01-01T00:00:00Z",
              "watchedFolders": [],
              "files": []
            }
            """);

        var registry = manager.LoadRegistry();

        var entry = Assert.Single(registry.Libraries);
        Assert.Equal(legacyId, entry.Id);
        Assert.Equal("Legacy Manuals", entry.Name);
    }

    [Fact]
    public async Task RenameLibrary_UpdatesManifestRegistryAndDisplayName()
    {
        var (service, manager) = CreateServiceWithConfig("rename", out var config, out var ssdRoot);
        var created = await service.CreateLibraryAsync(config, ssdRoot, "Old name");

        var renamed = await service.RenameLibraryAsync(created.Id, "New name");

        Assert.Equal("New name", renamed.Name);
        Assert.Equal("New name", manager.LoadManifest(created.Id).Name);
        Assert.Equal("New name", Assert.Single(manager.LoadRegistry().Libraries).Name);
        Assert.Contains("New name", service.GetLibraryDisplayInfo(config).Options);
    }

    [Fact]
    public async Task RenameLibrary_DuplicateNameCaseInsensitive_IsRejected()
    {
        var (service, _) = CreateServiceWithConfig("rename-dupe", out var config, out var ssdRoot);
        await service.CreateLibraryAsync(config, ssdRoot, "Alpha");
        var beta = await service.CreateLibraryAsync(config, ssdRoot, "Beta");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RenameLibraryAsync(beta.Id, "  alpha  "));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RenameLibrary_EmptyName_IsRejected()
    {
        var (service, _) = CreateServiceWithConfig("rename-empty", out var config, out var ssdRoot);
        var lib = await service.CreateLibraryAsync(config, ssdRoot, "Named");

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.RenameLibraryAsync(lib.Id, "   "));
    }

    [Fact]
    public async Task RenameLibrary_ToItsOwnName_IsAllowed()
    {
        var (service, manager) = CreateServiceWithConfig("rename-self", out var config, out var ssdRoot);
        var lib = await service.CreateLibraryAsync(config, ssdRoot, "Same");

        var renamed = await service.RenameLibraryAsync(lib.Id, "Same");

        Assert.Equal("Same", renamed.Name);
        Assert.Equal("Same", Assert.Single(manager.LoadRegistry().Libraries).Name);
    }

    [Fact]
    public async Task DeleteLibrary_PurgesFolderEvenWithOpenVectorIndex()
    {
        var (_, manager) = CreateServiceWithConfig("delete-purge", out _, out _);
        var created = await manager.CreateLibraryAsync("Purge me");

        // Materialize a real vectors.db and leave a pooled SQLite connection holding
        // the OS file lock — DeleteLibraryAsync must ClearAllPools before deleting.
        _ = new VectorIndex(manager.GetIndexPath(created.Id));
        var vectorsDb = Path.Combine(manager.GetIndexPath(created.Id), "vectors.db");
        Assert.True(File.Exists(vectorsDb));

        var libraryPath = manager.GetLibraryPath(created.Id);
        await manager.DeleteLibraryAsync(created.Id);

        Assert.False(Directory.Exists(libraryPath));
        Assert.Empty(manager.LoadRegistry().Libraries);
    }

    [Fact]
    public async Task DeleteLibrary_ActiveLibrary_ClearsActiveConfigAndRegistry()
    {
        var (service, manager) = CreateServiceWithConfig("delete-active", out var config, out var ssdRoot);
        var created = await service.CreateLibraryAsync(config, ssdRoot, "Doomed");
        Assert.Equal(created.Id, config.ActiveDocumentLibraryId);
        var libraryPath = manager.GetLibraryPath(created.Id);

        await service.DeleteLibraryAsync(config, ssdRoot, created.Id);

        Assert.Null(config.ActiveDocumentLibraryId);
        Assert.Null(manager.LoadRegistry().ActiveLibraryId);
        Assert.Empty(manager.LoadRegistry().Libraries);
        Assert.False(Directory.Exists(libraryPath));
    }

    [Fact]
    public async Task DeleteLibrary_NonActive_LeavesActiveSelectionIntact()
    {
        var (service, manager) = CreateServiceWithConfig("delete-nonactive", out var config, out var ssdRoot);
        var keep = await service.CreateLibraryAsync(config, ssdRoot, "Keep");
        var drop = await service.CreateLibraryAsync(config, ssdRoot, "Drop");
        await service.SetActiveLibraryAsync(config, ssdRoot, keep.Id);
        Assert.Equal(keep.Id, config.ActiveDocumentLibraryId);

        await service.DeleteLibraryAsync(config, ssdRoot, drop.Id);

        Assert.Equal(keep.Id, config.ActiveDocumentLibraryId);
        Assert.Equal("Keep", Assert.Single(manager.LoadRegistry().Libraries).Name);
    }

    [Fact]
    public async Task RemoveWatchedFolder_AddThenRemove_RoundTrips()
    {
        var (service, manager) = CreateServiceWithConfig("remove-folder", out var config, out var ssdRoot);
        var lib = await service.CreateLibraryAsync(config, ssdRoot, "Watcher");
        var folder = Path.Combine(_tempRoot, "wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        Assert.True(await service.AddWatchedFolderAsync(lib, folder));
        Assert.Contains(folder, manager.LoadManifest(lib.Id).WatchedFolders);

        Assert.True(await service.RemoveWatchedFolderAsync(lib, folder));
        Assert.DoesNotContain(folder, manager.LoadManifest(lib.Id).WatchedFolders);

        // Removing an absent folder is a no-op.
        Assert.False(await service.RemoveWatchedFolderAsync(lib, folder));
    }

    private (DocumentOperationsService Service, DocumentLibraryManager Manager) CreateServiceWithConfig(
        string scenarioFolder,
        out PortableConfig config,
        out string ssdRoot)
    {
        ssdRoot = Path.Combine(_tempRoot, scenarioFolder);
        SsdLayout.EnsureStructure(ssdRoot);

        var manager = new DocumentLibraryManager(ssdRoot);
        var embeddingClient = new EmbeddingClient(new HttpClient(new StubEmbeddingHandler()));
        var ingestor = new DocumentIngestor(manager, embeddingClient);
        var service = new DocumentOperationsService(manager, ingestor, new ConfigStore());
        config = new PortableConfig();
        return (service, manager);
    }

    private sealed class StubEmbeddingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Embedding API should not be called in document library workflow tests.");
        }
    }
}
