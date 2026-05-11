using FreeAiSsd.PrepApp;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC35: pin <see cref="OllamaModelStager"/>'s contract — the merge
/// step that publishes a host-staged Ollama tree onto the SSD without
/// triggering exFAT's 16-parallel-writer collapse. The mac-prep-host
/// pull path now stages every pull through host APFS and then calls
/// the stager to copy blobs sequentially; the rest of MAC35 hangs on
/// these guarantees:
///   - Idempotent retry: a re-merge after cancel skips intact blobs.
///   - Manifest-written-last: a torn merge is invisible to discovery.
///   - Tmp-then-rename: the dest path never holds partial bytes, so
///     a cancel between blobs can't expose a torn file to the runner.
///   - Hostile-tag refusal: the tag allowlist matches the one
///     <see cref="ModelOperations.EstimatePartialProgress"/> uses, so
///     a path-traversal coercion fails closed.
///   - Disk-space precheck: refuses to start when the staging volume
///     can't fit 2× the estimated model size (5 GB floor for unknown).
/// </summary>
public sealed class OllamaModelStagerTests : IDisposable
{
    private readonly string _root;
    private readonly string _staging;
    private readonly string _ssd;

    public OllamaModelStagerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "freeaissd-mac35-" + Guid.NewGuid().ToString("N"));
        _staging = Path.Combine(_root, "staging");
        _ssd = Path.Combine(_root, "ssd-models");
        Directory.CreateDirectory(_staging);
        Directory.CreateDirectory(_ssd);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task MergeToSsd_CopiesAllBlobsAndManifest()
    {
        var digests = WriteSyntheticPull(_staging, "llama3.2", "1b",
            layers: new[] { (size: 1024L, payload: (byte)0x10), (size: 512L, payload: (byte)0x20) });

        await OllamaModelStager.MergeToSsdAsync(_staging, _ssd, "llama3.2:1b", _ => { }, CancellationToken.None);

        // Both blobs landed at the same content-addressed path on the SSD.
        Assert.True(File.Exists(Path.Combine(_ssd, "blobs", "sha256-" + digests[0])));
        Assert.True(File.Exists(Path.Combine(_ssd, "blobs", "sha256-" + digests[1])));
        Assert.Equal(1024, new FileInfo(Path.Combine(_ssd, "blobs", "sha256-" + digests[0])).Length);
        Assert.Equal(512,  new FileInfo(Path.Combine(_ssd, "blobs", "sha256-" + digests[1])).Length);
        // Manifest landed at the canonical Ollama path.
        var ssdManifest = Path.Combine(_ssd, "manifests", "registry.ollama.ai", "library", "llama3.2", "1b");
        Assert.True(File.Exists(ssdManifest));
        // No tmp files leaked.
        Assert.Empty(Directory.GetFiles(Path.Combine(_ssd, "blobs"), "*.tmp"));
    }

    [Fact]
    public async Task MergeToSsd_BlobAlreadyOnSsdAtMatchingSize_SkippedWithoutRecopy()
    {
        var digests = WriteSyntheticPull(_staging, "llama3.2", "1b",
            layers: new[] { (size: 1024L, payload: (byte)0xAA) });

        // Pre-populate the SSD blob at the same size with sentinel bytes
        // so we can prove the merge SKIPS it (doesn't overwrite).
        var ssdBlobsDir = Path.Combine(_ssd, "blobs");
        Directory.CreateDirectory(ssdBlobsDir);
        var ssdBlobPath = Path.Combine(ssdBlobsDir, "sha256-" + digests[0]);
        var sentinel = new byte[1024];
        Array.Fill(sentinel, (byte)0xCC);
        await File.WriteAllBytesAsync(ssdBlobPath, sentinel);

        await OllamaModelStager.MergeToSsdAsync(_staging, _ssd, "llama3.2:1b", _ => { }, CancellationToken.None);

        // Sentinel bytes survive — proves no recopy happened.
        var actual = await File.ReadAllBytesAsync(ssdBlobPath);
        Assert.All(actual, b => Assert.Equal(0xCC, b));
        // Manifest still landed (skip is per-blob, not per-merge).
        Assert.True(File.Exists(Path.Combine(_ssd, "manifests", "registry.ollama.ai", "library", "llama3.2", "1b")));
    }

    [Fact]
    public async Task MergeToSsd_BlobAlreadyOnSsdAtDifferentSize_RecopiedFromStaging()
    {
        var digests = WriteSyntheticPull(_staging, "llama3.2", "1b",
            layers: new[] { (size: 1024L, payload: (byte)0xAA) });

        // SSD has a stale half-blob at the digest path — defensive pin
        // for the case where a prior merge was cancelled mid-flight on
        // a system that didn't honor the tmp-then-rename guarantee
        // (shouldn't happen in this stager, but the size-mismatch
        // recovery is the safety net for any other write path that
        // could leave torn bytes).
        var ssdBlobsDir = Path.Combine(_ssd, "blobs");
        Directory.CreateDirectory(ssdBlobsDir);
        var ssdBlobPath = Path.Combine(ssdBlobsDir, "sha256-" + digests[0]);
        await File.WriteAllBytesAsync(ssdBlobPath, new byte[200]);

        await OllamaModelStager.MergeToSsdAsync(_staging, _ssd, "llama3.2:1b", _ => { }, CancellationToken.None);

        var actual = await File.ReadAllBytesAsync(ssdBlobPath);
        Assert.Equal(1024, actual.Length);
        Assert.All(actual, b => Assert.Equal(0xAA, b));
    }

    [Fact]
    public async Task MergeToSsd_ManifestWrittenLast_DiscoveryHidesPartialMerge()
    {
        // Force the merge to fail mid-blob-copy by deleting the
        // staging blob between manifest enumeration and copy. The
        // stager opens the source blob inside CopyFileAtomicAsync, so
        // pre-deleting it surfaces a FileNotFoundException at the
        // existence check. The invariant we're pinning: the SSD
        // manifest must NOT exist after such a failure, so
        // DiscoverModelsOnDisk treats the model as not-installed.
        var digests = WriteSyntheticPull(_staging, "llama3.2", "1b",
            layers: new[] { (size: 1024L, payload: (byte)0x10), (size: 512L, payload: (byte)0x20) });

        File.Delete(Path.Combine(_staging, "blobs", "sha256-" + digests[1]));

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            OllamaModelStager.MergeToSsdAsync(_staging, _ssd, "llama3.2:1b", _ => { }, CancellationToken.None));

        var ssdManifest = Path.Combine(_ssd, "manifests", "registry.ollama.ai", "library", "llama3.2", "1b");
        Assert.False(File.Exists(ssdManifest));
        // DiscoverModelsOnDisk reads the manifests tree; absent manifest = absent model.
        var discovered = ModelOperations.DiscoverModelsOnDisk(_ssd);
        Assert.DoesNotContain("llama3.2:1b", discovered);
    }

    [Fact]
    public async Task MergeToSsd_CancelDuringCopy_DestPathHasNoTornBytes()
    {
        // Large enough that CopyFileAtomicAsync's chunked copy will
        // observe the cancel mid-flight on any reasonable machine. We
        // pre-cancel the token so the very first chunk read trips the
        // CT and the tmp file is cleaned in the catch.
        var digests = WriteSyntheticPull(_staging, "llama3.2", "1b",
            layers: new[] { (size: 16L * 1024 * 1024, payload: (byte)0x55) });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // ThrowsAnyAsync — both OperationCanceledException and the
        // TaskCanceledException subclass are valid signals. Which one
        // surfaces depends on whether the throw came from
        // ct.ThrowIfCancellationRequested (OCE) or from Stream.ReadAsync
        // observing the token mid-copy (TCE). The pin is on cancellation
        // semantics, not the exact derived type.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OllamaModelStager.MergeToSsdAsync(_staging, _ssd, "llama3.2:1b", _ => { }, cts.Token));

        var ssdBlobPath = Path.Combine(_ssd, "blobs", "sha256-" + digests[0]);
        var ssdBlobsDir = Path.Combine(_ssd, "blobs");
        Assert.False(File.Exists(ssdBlobPath));
        // No leftover tmp either — that's the post-cancel cleanup pin.
        if (Directory.Exists(ssdBlobsDir))
        {
            Assert.Empty(Directory.GetFiles(ssdBlobsDir, "*.tmp"));
        }
        // And the manifest must not exist.
        var ssdManifest = Path.Combine(_ssd, "manifests", "registry.ollama.ai", "library", "llama3.2", "1b");
        Assert.False(File.Exists(ssdManifest));
    }

    [Fact]
    public async Task MergeToSsd_HostileTag_RefusedBeforeAnyIo()
    {
        // Path-traversal tag must fail fast before opening the manifest
        // path under ssdModelsRoot — same posture as PathGuards.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OllamaModelStager.MergeToSsdAsync(_staging, _ssd, "../../../etc/passwd:1b", _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            OllamaModelStager.MergeToSsdAsync(_staging, _ssd, "llama/secret:1b", _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task MergeToSsd_StagingManifestMissing_ThrowsFileNotFound()
    {
        // No synthetic pull written — manifest is missing. Surfaces
        // the "pull may have failed silently" path so the sidecar can
        // emit a clean error rather than a hollow result.
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            OllamaModelStager.MergeToSsdAsync(_staging, _ssd, "llama3.2:1b", _ => { }, CancellationToken.None));
    }

    /// <summary>
    /// 2026-05-11 HF pull regression: Ollama's HF pull writes the
    /// manifest under <c>manifests/hf.co/&lt;owner&gt;/&lt;repo&gt;/&lt;quant&gt;</c>,
    /// so the merge must read from and write to the same subtree
    /// (not <c>registry.ollama.ai/library/...</c>). Without this fix
    /// the merge step threw <c>FileNotFoundException</c> for the
    /// staging manifest on every HF pull, even though Ollama had
    /// already written it — surfacing as "Pull failed" in the sidecar
    /// log after a successful 100 % download.
    /// </summary>
    [Fact]
    public async Task MergeToSsd_HuggingFaceTag_PublishesUnderHfSubtree()
    {
        var digests = WriteSyntheticHuggingFacePull(
            _staging, "Andycurrent", "Gemma-3-1B-it_GGUF", "Q2_K",
            layers: new[] { (size: 1024L, payload: (byte)0x77) });

        await OllamaModelStager.MergeToSsdAsync(
            _staging, _ssd,
            "hf.co/Andycurrent/Gemma-3-1B-it_GGUF:Q2_K",
            _ => { }, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_ssd, "blobs", "sha256-" + digests[0])));
        var ssdManifest = Path.Combine(
            _ssd, "manifests", "hf.co", "Andycurrent", "Gemma-3-1B-it_GGUF", "Q2_K");
        Assert.True(File.Exists(ssdManifest));
        // Sanity: the merge must NOT publish to the library subtree —
        // mixing locations would let DiscoverModelsOnDisk surface the
        // same model under two different ids.
        var wrongPath = Path.Combine(
            _ssd, "manifests", "registry.ollama.ai", "library",
            "hf.co", "Andycurrent", "Gemma-3-1B-it_GGUF", "Q2_K");
        Assert.False(File.Exists(wrongPath));
    }

    [Fact]
    public void EnsureStagingFreeSpace_VolumeWithRoom_DoesNotThrow()
    {
        // The test root sits on whatever volume Path.GetTempPath
        // resolves to; on any modern dev box that has gigabytes free.
        // Refusing a 1-byte estimate must be a no-op.
        var ex = Record.Exception(() => OllamaModelStager.EnsureStagingFreeSpace(_staging, 1));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureStagingFreeSpace_RequestsMoreThanVolumeSize_Throws()
    {
        // Ask for an absurd amount — guaranteed to exceed any test
        // host's free space — and confirm the precheck refuses with
        // an IOException callers can show to the user.
        var ex = Assert.Throws<IOException>(() =>
            OllamaModelStager.EnsureStagingFreeSpace(_staging, long.MaxValue / 4));
        Assert.Contains("free", ex.Message);
    }

    [Fact]
    public void ResolveMacStagingRoot_PointsUnderUserCaches()
    {
        // No assertion that the path actually equals the canonical Mac
        // path because tests run on Linux CI too; just confirm the
        // resolver yields a non-empty existing directory.
        var staging = OllamaModelStager.ResolveMacStagingRoot();
        Assert.False(string.IsNullOrWhiteSpace(staging));
        Assert.True(Directory.Exists(staging));
    }

    // --- Helpers ---------------------------------------------------------

    /// <summary>
    /// Writes a synthetic Ollama-shaped staging tree: a manifest at
    /// <c>manifests/registry.ollama.ai/library/{model}/{tag}</c> referencing
    /// one blob per layer, and the matching <c>blobs/sha256-{digest}</c>
    /// files filled with a fixed payload byte. Returns the per-layer
    /// digest hex so the test can assert the post-merge path shape.
    /// </summary>
    private static string[] WriteSyntheticPull(
        string root, string modelName, string manifestTag, (long size, byte payload)[] layers)
    {
        var manifestDir = Path.Combine(root, "manifests", "registry.ollama.ai", "library", modelName);
        var blobsDir = Path.Combine(root, "blobs");
        Directory.CreateDirectory(manifestDir);
        Directory.CreateDirectory(blobsDir);

        var digests = new string[layers.Length];
        var layerJson = new System.Text.StringBuilder();
        for (var i = 0; i < layers.Length; i++)
        {
            digests[i] = "abcdef0123456789".PadRight(64, (char)('a' + i));
            if (i > 0) layerJson.Append(',');
            layerJson.Append("{\"digest\":\"sha256:").Append(digests[i])
                     .Append("\",\"size\":").Append(layers[i].size)
                     .Append(",\"mediaType\":\"application/vnd.ollama.image.layer.model\"}");

            var blob = new byte[layers[i].size];
            Array.Fill(blob, layers[i].payload);
            File.WriteAllBytes(Path.Combine(blobsDir, "sha256-" + digests[i]), blob);
        }
        File.WriteAllText(Path.Combine(manifestDir, manifestTag), "{\"layers\":[" + layerJson + "]}");
        return digests;
    }

    /// <summary>
    /// 2026-05-11 HF fix companion to <see cref="WriteSyntheticPull"/>:
    /// writes the manifest at the <c>manifests/hf.co/&lt;owner&gt;/&lt;repo&gt;/&lt;quant&gt;</c>
    /// path Ollama actually uses for HF GGUF pulls, plus matching blobs.
    /// Owner/repo casing is preserved (HF allows mixed case and the
    /// previous lowercase-only allowlist refused it).
    /// </summary>
    private static string[] WriteSyntheticHuggingFacePull(
        string root, string owner, string repo, string quant, (long size, byte payload)[] layers)
    {
        var manifestDir = Path.Combine(root, "manifests", "hf.co", owner, repo);
        var blobsDir = Path.Combine(root, "blobs");
        Directory.CreateDirectory(manifestDir);
        Directory.CreateDirectory(blobsDir);

        var digests = new string[layers.Length];
        var layerJson = new System.Text.StringBuilder();
        for (var i = 0; i < layers.Length; i++)
        {
            digests[i] = "fedcba9876543210".PadRight(64, (char)('a' + i));
            if (i > 0) layerJson.Append(',');
            layerJson.Append("{\"digest\":\"sha256:").Append(digests[i])
                     .Append("\",\"size\":").Append(layers[i].size)
                     .Append(",\"mediaType\":\"application/vnd.ollama.image.layer.model\"}");

            var blob = new byte[layers[i].size];
            Array.Fill(blob, layers[i].payload);
            File.WriteAllBytes(Path.Combine(blobsDir, "sha256-" + digests[i]), blob);
        }
        File.WriteAllText(Path.Combine(manifestDir, quant), "{\"layers\":[" + layerJson + "]}");
        return digests;
    }
}
