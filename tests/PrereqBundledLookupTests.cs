using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared;

namespace FreeAiSsd.Tests;

// MAC24: pin the bundled prereqs-folder resolution behavior across both
// layouts. Third occurrence of the AppContext.BaseDirectory ancestor-walk
// pattern (after MAC22 in MacArtifactAvailability and MAC23 in
// ArtifactStagingService).
//
// Windows PrepApp.exe runs from the cross-platform bundle root, so its
// AppContext.BaseDirectory is a sibling of payload/. The lookup finds
// payload/windows/tools/prereqs on the second EnumerateBundleRoots
// candidate.
//
// Mac PrepApp's mac-prep-host sidecar runs from
// PrepApp.app/Contents/Resources/prep-host/, so AppContext.BaseDirectory
// is 5 levels deep inside the bundle. Pre-MAC24, the lookup only checked
// the sidecar's own directory and its payload/ child, both of which miss;
// the sidecar threw "Bundled prerequisites folder is missing: …/prep-host/
// windows/tools/prereqs" every time even though the folder was clearly
// present at the bundle root. MAC24 extends the resolver to walk
// ancestors so both layouts resolve.
public class PrereqBundledLookupTests : IDisposable
{
    private readonly string _tempRoot;

    public PrereqBundledLookupTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mac24-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ResolveBundledPrereqDirectory_WindowsLayout_FindsFolderUnderPayload()
    {
        // Windows: PrepApp.exe sits at the bundle root next to payload/.
        var bundleRoot = Path.Combine(_tempRoot, "Free-AI-SSD-beta-crossplatform");
        var prereqDir = Path.Combine(bundleRoot, "payload", SsdLayout.Prereqs);
        Directory.CreateDirectory(prereqDir);

        var resolved = PrereqService.ResolveBundledPrereqDirectory(bundleRoot);

        Assert.Equal(Path.GetFullPath(prereqDir), Path.GetFullPath(resolved));
        Assert.True(Directory.Exists(resolved));
    }

    [Fact]
    public void ResolveBundledPrereqDirectory_MacSidecarLayout_FindsFolderFiveLevelsUp()
    {
        // Mac: AppContext.BaseDirectory points at
        //   <bundleRoot>/payload/mac/PrepApp.app/Contents/Resources/prep-host/
        // The prereqs folder lives at
        //   <bundleRoot>/payload/windows/tools/prereqs
        // which sits across the bundle root from the sidecar's BaseDirectory.
        var bundleRoot = Path.Combine(_tempRoot, "Free-AI-SSD-beta-crossplatform");
        var prereqDir = Path.Combine(bundleRoot, "payload", SsdLayout.Prereqs);
        Directory.CreateDirectory(prereqDir);

        var sidecarBaseDir = Path.Combine(
            bundleRoot, "payload", "mac", "PrepApp.app", "Contents", "Resources", "prep-host");
        Directory.CreateDirectory(sidecarBaseDir);

        var resolved = PrereqService.ResolveBundledPrereqDirectory(sidecarBaseDir);

        Assert.Equal(Path.GetFullPath(prereqDir), Path.GetFullPath(resolved));
        Assert.True(Directory.Exists(resolved),
            $"Expected sidecar lookup to find prereqs folder at bundle root; resolved={resolved}");
    }

    [Fact]
    public void ResolveBundledPrereqDirectory_NoFolderAnywhere_ReturnsConventionalPathForDiagnostic()
    {
        // When no candidate exists, the resolver returns "<base>/payload/<prereqs>"
        // so the caller's Directory.Exists(...) -> DirectoryNotFoundException
        // surfaces the conventional path users recognize from prior versions.
        var sidecarBaseDir = Path.Combine(_tempRoot, "lonely", "tree");
        Directory.CreateDirectory(sidecarBaseDir);

        var resolved = PrereqService.ResolveBundledPrereqDirectory(sidecarBaseDir);

        Assert.False(Directory.Exists(resolved));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(sidecarBaseDir, "payload", SsdLayout.Prereqs)),
            Path.GetFullPath(resolved));
    }

    [Fact]
    public void ResolveBundledPrereqDirectory_DoesNotWalkPastBoundedDepth()
    {
        // Sanity: the ancestor walk is bounded so a deep tree without a
        // prereqs folder doesn't accidentally escape into a parent fixture
        // that happens to have one. Build a sidecarBaseDir that is more
        // than 6 levels deeper than the prereqs folder and confirm we
        // don't find it.
        var bundleRoot = Path.Combine(_tempRoot, "deepcase");
        var prereqDir = Path.Combine(bundleRoot, "payload", SsdLayout.Prereqs);
        Directory.CreateDirectory(prereqDir);

        // 8 levels below the bundle root — past the bounded 6-ancestor walk.
        var sidecarBaseDir = Path.Combine(bundleRoot, "a", "b", "c", "d", "e", "f", "g", "h");
        Directory.CreateDirectory(sidecarBaseDir);

        var resolved = PrereqService.ResolveBundledPrereqDirectory(sidecarBaseDir);

        // Resolver fell through to the conventional path, which doesn't exist.
        Assert.False(Directory.Exists(resolved));
    }
}
