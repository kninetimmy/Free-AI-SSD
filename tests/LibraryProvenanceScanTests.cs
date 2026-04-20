using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

public sealed class LibraryProvenanceScanTests : IDisposable
{
    private readonly string _root;
    private readonly DocumentLibraryManager _manager;

    public LibraryProvenanceScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"prov-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _manager = new DocumentLibraryManager(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ScanProvenanceMismatches_NoLibraries_ReturnsEmpty()
    {
        var result = _manager.ScanProvenanceMismatches("nomic-embed-text");
        Assert.Empty(result);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ScanProvenanceMismatches_ModelMatches_ExcludesLibrary()
    {
        var manifest = await _manager.CreateLibraryAsync("Matching");
        manifest.LastEmbeddingModel = "nomic-embed-text";
        await _manager.SaveManifestAsync(manifest);

        var result = _manager.ScanProvenanceMismatches("nomic-embed-text");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanProvenanceMismatches_ModelMismatch_IncludesLibrary()
    {
        var manifest = await _manager.CreateLibraryAsync("Mismatched");
        manifest.LastEmbeddingModel = "mxbai-embed-large";
        await _manager.SaveManifestAsync(manifest);

        var result = _manager.ScanProvenanceMismatches("nomic-embed-text");

        Assert.Single(result);
        Assert.Equal("Mismatched", result[0].Name);
        Assert.Equal("mxbai-embed-large", result[0].LastEmbeddingModel);
    }

    [Fact]
    public async Task ScanProvenanceMismatches_NullModel_ExcludesLibrary()
    {
        var manifest = await _manager.CreateLibraryAsync("NoModel");
        // LastEmbeddingModel is null by default — library not yet indexed
        await _manager.SaveManifestAsync(manifest);

        var result = _manager.ScanProvenanceMismatches("nomic-embed-text");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanProvenanceMismatches_UnknownModel_ExcludesLibrary()
    {
        var manifest = await _manager.CreateLibraryAsync("Unknown");
        manifest.LastEmbeddingModel = "unknown";
        await _manager.SaveManifestAsync(manifest);

        var result = _manager.ScanProvenanceMismatches("nomic-embed-text");

        Assert.Empty(result);
    }

    [Fact]
    public async Task ScanProvenanceMismatches_MixedLibraries_ReturnsOnlyMismatches()
    {
        var m1 = await _manager.CreateLibraryAsync("Match");
        m1.LastEmbeddingModel = "nomic-embed-text";
        await _manager.SaveManifestAsync(m1);

        var m2 = await _manager.CreateLibraryAsync("Mismatch");
        m2.LastEmbeddingModel = "mxbai-embed-large";
        await _manager.SaveManifestAsync(m2);

        var m3 = await _manager.CreateLibraryAsync("NotIndexed");
        // LastEmbeddingModel null
        await _manager.SaveManifestAsync(m3);

        var result = _manager.ScanProvenanceMismatches("nomic-embed-text");

        Assert.Single(result);
        Assert.Equal("Mismatch", result[0].Name);
    }

    [Fact]
    public async Task ScanProvenanceMismatches_CaseInsensitiveModelComparison_Matches()
    {
        var manifest = await _manager.CreateLibraryAsync("CaseCheck");
        manifest.LastEmbeddingModel = "Nomic-Embed-Text";
        await _manager.SaveManifestAsync(manifest);

        var result = _manager.ScanProvenanceMismatches("nomic-embed-text");

        Assert.Empty(result);
    }
}
