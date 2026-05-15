using FreeAiSsd.PrepApp.Services;

namespace FreeAiSsd.Tests;

// MAC23: pin the bundled-file lookup behavior across both layouts.
//
// Symmetric mirror of MAC22 in a sibling helper. Windows PrepApp.exe runs
// from the cross-platform bundle root, so its AppContext.BaseDirectory is a
// sibling of payload/. The lookup finds payload/mac/Runner.app.zip on the
// second EnumerateBundledContentRoots candidate.
//
// Mac PrepApp's mac-prep-host sidecar runs from
// PrepApp.app/Contents/Resources/prep-host/, so AppContext.BaseDirectory is
// 5 levels deep inside the bundle. Pre-MAC23, the lookup only checked the
// sidecar's own directory and its payload/ child, both of which miss; the
// sidecar threw "Bundled macOS Runner.app archive was not found." every time
// even though the artifact was clearly present at the bundle root. MAC23
// extends the enumerator to walk ancestors so both layouts resolve.
public class ArtifactStagingBundledFileLookupTests : IDisposable
{
    private readonly string _tempRoot;

    public ArtifactStagingBundledFileLookupTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mac23-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ResolveBundledFile_WindowsLayout_FindsArtifactUnderPayload()
    {
        // Windows: PrepApp.exe sits at the bundle root next to payload/.
        var bundleRoot = Path.Combine(_tempRoot, "Free-AI-SSD-beta-crossplatform");
        var macDir = Path.Combine(bundleRoot, "payload", "mac");
        Directory.CreateDirectory(macDir);
        var artifact = Path.Combine(macDir, "Runner.app.zip");
        File.WriteAllBytes(artifact, new byte[] { 0x50, 0x4B, 0x05, 0x06 });

        var resolved = ArtifactStagingService.ResolveBundledFile(
            bundleRoot, Path.Combine("mac", "Runner.app.zip"));

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(artifact), Path.GetFullPath(resolved!));
    }

    [Fact]
    public void ResolveBundledFile_MacSidecarLayout_FindsArtifactFiveLevelsUp()
    {
        // Mac: AppContext.BaseDirectory points at
        //   <bundleRoot>/payload/mac/PrepApp.app/Contents/Resources/prep-host/
        // The artifact lives at <bundleRoot>/payload/mac/Runner.app.zip
        // which is 4 levels above the sidecar's BaseDirectory.
        var bundleRoot = Path.Combine(_tempRoot, "Free-AI-SSD-beta-crossplatform");
        var macDir = Path.Combine(bundleRoot, "payload", "mac");
        Directory.CreateDirectory(macDir);
        var artifact = Path.Combine(macDir, "Runner.app.zip");
        File.WriteAllBytes(artifact, new byte[] { 0x50, 0x4B, 0x05, 0x06 });

        var sidecarBaseDir = Path.Combine(macDir, "PrepApp.app", "Contents", "Resources", "prep-host");
        Directory.CreateDirectory(sidecarBaseDir);

        var resolved = ArtifactStagingService.ResolveBundledFile(
            sidecarBaseDir, Path.Combine("mac", "Runner.app.zip"));

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(artifact), Path.GetFullPath(resolved!));
    }

    [Fact]
    public void ResolveBundledFile_DependenciesLayout_FindsArtifactUnderDependencies()
    {
        // Post-restructure: the bundle uses dependencies/ instead of payload/.
        var bundleRoot = Path.Combine(_tempRoot, "Free-AI-SSD-crossplatform");
        var macDir = Path.Combine(bundleRoot, "dependencies", "mac");
        Directory.CreateDirectory(macDir);
        var artifact = Path.Combine(macDir, "tools", "ollama", "ollama-darwin.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllBytes(artifact, new byte[] { 0x50, 0x4B, 0x05, 0x06 });

        var sidecarBaseDir = Path.Combine(macDir, "PrepApp.app", "Contents", "Resources", "prep-host");
        Directory.CreateDirectory(sidecarBaseDir);

        var resolved = ArtifactStagingService.ResolveBundledFile(
            sidecarBaseDir, Path.Combine("mac", "tools", "ollama", "ollama-darwin.zip"));

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(artifact), Path.GetFullPath(resolved!));
    }

    [Fact]
    public void ResolveBundledDirectory_DependenciesLayout_FindsUnzippedRunnerApp()
    {
        // The Mac Runner now ships UNZIPPED — staging resolves it as a
        // directory (dependencies/mac/Runner.app), not a .zip file.
        var bundleRoot = Path.Combine(_tempRoot, "xplat");
        var runnerApp = Path.Combine(bundleRoot, "dependencies", "mac", "Runner.app", "Contents", "MacOS");
        Directory.CreateDirectory(runnerApp);
        File.WriteAllText(Path.Combine(runnerApp, "Runner"), "#!/bin/sh\n");

        var sidecarBaseDir = Path.Combine(
            bundleRoot, "PrepApp.app", "Contents", "Resources", "prep-host");
        Directory.CreateDirectory(sidecarBaseDir);

        var resolved = ArtifactStagingService.ResolveBundledDirectory(
            sidecarBaseDir, Path.Combine("mac", "Runner.app"));

        Assert.NotNull(resolved);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(bundleRoot, "dependencies", "mac", "Runner.app")),
            Path.GetFullPath(resolved!));
    }

    [Fact]
    public void ResolveBundledFile_NoArtifactAnywhere_ReturnsNull()
    {
        // Pristine empty tree — no Runner.app.zip anywhere up the chain.
        var sidecarBaseDir = Path.Combine(_tempRoot, "lonely", "tree", "with", "no", "bundle");
        Directory.CreateDirectory(sidecarBaseDir);

        var resolved = ArtifactStagingService.ResolveBundledFile(
            sidecarBaseDir, Path.Combine("mac", "Runner.app.zip"));

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveBundledFile_DoesNotWalkPastBoundedDepth()
    {
        // Sanity: the ancestor walk is bounded so a deep tree without a
        // bundled file doesn't accidentally escape into a parent test fixture
        // that happens to have one. Build a sidecarBaseDir that is more
        // than 6 levels deeper than the artifact and confirm we don't
        // find it.
        var bundleRoot = Path.Combine(_tempRoot, "deepcase");
        var macDir = Path.Combine(bundleRoot, "payload", "mac");
        Directory.CreateDirectory(macDir);
        File.WriteAllBytes(Path.Combine(macDir, "Runner.app.zip"),
            new byte[] { 0x50, 0x4B, 0x05, 0x06 });

        // 8 levels below the bundle root — past the bounded 6-ancestor walk.
        var sidecarBaseDir = Path.Combine(bundleRoot, "a", "b", "c", "d", "e", "f", "g", "h");
        Directory.CreateDirectory(sidecarBaseDir);

        var resolved = ArtifactStagingService.ResolveBundledFile(
            sidecarBaseDir, Path.Combine("mac", "Runner.app.zip"));

        Assert.Null(resolved);
    }
}
