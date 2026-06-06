using FreeAiSsd.Shared.Documents;

namespace FreeAiSsd.Tests;

/// #115/#11: DocumentLibraryManager builds on-disk paths from a libraryId that
/// originates at the LAN route. GetLibraryPath is the chokepoint every Get*Path
/// helper funnels through, so it must reject any id that isn't a bare slug/GUID
/// before it can compose a "..", absolute, or separator-bearing path.
public sealed class DocumentLibraryManagerPathGuardTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DocumentLibraryManager _manager;

    public DocumentLibraryManagerPathGuardTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"doclib-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _manager = new DocumentLibraryManager(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("sub/dir")]
    [InlineData("sub\\dir")]
    [InlineData("C:\\absolute")]
    [InlineData("with space")]
    [InlineData("with.dot")]
    [InlineData("")]
    public void GetLibraryPath_RejectsNonSlugId(string libraryId)
    {
        Assert.Throws<ArgumentException>(() => _manager.GetLibraryPath(libraryId));
        // Every derived path funnels through GetLibraryPath, so they reject too.
        Assert.Throws<ArgumentException>(() => _manager.GetFilesPath(libraryId));
        Assert.Throws<ArgumentException>(() => _manager.GetIndexPath(libraryId));
        Assert.Throws<ArgumentException>(() => _manager.GetManifestPath(libraryId));
    }

    [Theory]
    [InlineData("checklist")]
    [InlineData("My_Library-2")]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e")] // GUID
    public void GetLibraryPath_AcceptsBareSlugAndGuid(string libraryId)
    {
        var path = _manager.GetLibraryPath(libraryId);
        Assert.EndsWith(libraryId, path);
        Assert.StartsWith(_tempRoot, path);
    }
}
