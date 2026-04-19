using FreeAiSsd.Runner.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Documents;
using FreeAiSsd.Shared.Services;

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
