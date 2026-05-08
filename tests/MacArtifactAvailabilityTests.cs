using FreeAiSsd.PrepApp;

namespace FreeAiSsd.Tests;

// MAC22: pin the manifest-discovery behavior across both layouts.
//
// Windows PrepApp.exe runs from the cross-platform bundle root, so its
// AppContext.BaseDirectory is a sibling of payload/. The lookup finds
// payload/mac/mac-artifacts.manifest.json on the second EnumerateContentRoots
// candidate.
//
// Mac PrepApp's mac-prep-host sidecar runs from
// PrepApp.app/Contents/Resources/prep-host/, so AppContext.BaseDirectory is
// 5 levels deep inside the bundle. Pre-MAC22, the lookup only checked the
// sidecar's own directory and its payload/ child, both of which miss; the
// sidecar reported "macOS preparation is available in the Cross-platform
// Beta download." every time even though the manifest was clearly present
// at the bundle root. MAC22 extends the enumerator to walk ancestors so
// both layouts resolve.
public class MacArtifactAvailabilityTests : IDisposable
{
    private readonly string _tempRoot;

    public MacArtifactAvailabilityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mac22-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Evaluate_WindowsLayout_FindsManifestUnderPayload()
    {
        // Windows: PrepApp.exe sits at the bundle root next to payload/.
        var bundleRoot = Path.Combine(_tempRoot, "Free-AI-SSD-beta-crossplatform");
        var macDir = Path.Combine(bundleRoot, "payload", "mac");
        Directory.CreateDirectory(macDir);
        WriteManifest(macDir, includeArtifact: true);

        var result = MacArtifactAvailability.Evaluate(bundleRoot);
        Assert.True(result.MacArtifactsAvailable,
            $"Expected available; got problem={result.MacArtifactsProblem}");
    }

    [Fact]
    public void Evaluate_MacSidecarLayout_FindsManifestFiveLevelsUp()
    {
        // Mac: AppContext.BaseDirectory points at
        //   <bundleRoot>/payload/mac/PrepApp.app/Contents/Resources/prep-host/
        // The manifest lives at <bundleRoot>/payload/mac/mac-artifacts.manifest.json
        // which is 4 levels above the sidecar's BaseDirectory.
        var bundleRoot = Path.Combine(_tempRoot, "Free-AI-SSD-beta-crossplatform");
        var macDir = Path.Combine(bundleRoot, "payload", "mac");
        Directory.CreateDirectory(macDir);
        WriteManifest(macDir, includeArtifact: true);

        var sidecarBaseDir = Path.Combine(macDir, "PrepApp.app", "Contents", "Resources", "prep-host");
        Directory.CreateDirectory(sidecarBaseDir);

        var result = MacArtifactAvailability.Evaluate(sidecarBaseDir);
        Assert.True(result.MacArtifactsAvailable,
            $"Expected available from sidecar BaseDirectory; got problem={result.MacArtifactsProblem}");
    }

    [Fact]
    public void Evaluate_NoManifestAnywhere_ReturnsUnavailable()
    {
        // Pristine empty tree — no manifest anywhere up the chain.
        var sidecarBaseDir = Path.Combine(_tempRoot, "lonely", "tree", "with", "no", "manifest");
        Directory.CreateDirectory(sidecarBaseDir);

        var result = MacArtifactAvailability.Evaluate(sidecarBaseDir);
        Assert.False(result.MacArtifactsAvailable);
        Assert.Equal("macOS preparation is available in the Cross-platform Beta download.",
            result.MacArtifactsProblem);
    }

    [Fact]
    public void Evaluate_ManifestPresentButReferencedArtifactMissing_ReturnsIncomplete()
    {
        // Manifest references Runner.app.zip but it isn't on disk.
        var bundleRoot = Path.Combine(_tempRoot, "Free-AI-SSD-beta-crossplatform");
        var macDir = Path.Combine(bundleRoot, "payload", "mac");
        Directory.CreateDirectory(macDir);
        WriteManifest(macDir, includeArtifact: false);

        var result = MacArtifactAvailability.Evaluate(bundleRoot);
        Assert.False(result.MacArtifactsAvailable);
        Assert.Equal("macOS artifacts are incomplete. Re-download the beta ZIP.",
            result.MacArtifactsProblem);
    }

    [Fact]
    public void Evaluate_DoesNotWalkPastBoundedDepth()
    {
        // Sanity: the ancestor walk is bounded so a deep tree without a
        // manifest doesn't accidentally escape into a parent test fixture
        // that happens to have one. Build a sidecarBaseDir that is more
        // than 6 levels deeper than the manifest and confirm we don't
        // find it.
        var bundleRoot = Path.Combine(_tempRoot, "deepcase");
        var macDir = Path.Combine(bundleRoot, "payload", "mac");
        Directory.CreateDirectory(macDir);
        WriteManifest(macDir, includeArtifact: true);

        // 8 levels below the bundle root — past the bounded 6-ancestor walk.
        var sidecarBaseDir = Path.Combine(bundleRoot, "a", "b", "c", "d", "e", "f", "g", "h");
        Directory.CreateDirectory(sidecarBaseDir);

        var result = MacArtifactAvailability.Evaluate(sidecarBaseDir);
        Assert.False(result.MacArtifactsAvailable);
    }

    private static void WriteManifest(string macDir, bool includeArtifact)
    {
        var artifactPath = Path.Combine(macDir, "Runner.app.zip");
        if (includeArtifact)
        {
            File.WriteAllBytes(artifactPath, new byte[] { 0x50, 0x4B, 0x05, 0x06 }); // empty-zip magic
        }

        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            artifacts = new[]
            {
                new { id = "macos-runner", relativePath = Path.Combine("mac", "Runner.app.zip").Replace('\\', '/') }
            }
        });
        File.WriteAllText(Path.Combine(macDir, "mac-artifacts.manifest.json"), manifestJson);
    }
}
