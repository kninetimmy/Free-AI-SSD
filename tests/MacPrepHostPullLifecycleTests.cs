using FreeAiSsd.MacPrepHost;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Services;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// MAC27 lifecycle pin for the Mac sidecar's pull-model path. Field-blocker
/// behind v1.3.7: <c>ollama pull</c> on Mac fails with "could not connect to
/// ollama app, is it running?" because the sidecar never started a temporary
/// <c>ollama serve</c> daemon. The Windows PrepViewModel does this at
/// <c>shared/ViewModels/PrepViewModel.cs:782</c>; MAC27 brings the Mac
/// sidecar to parity.
///
/// These tests use the internal HostLifetime ctor that injects fake
/// <see cref="IOllamaPackageService"/> + <see cref="IModelService"/> so the
/// non-test-mode pull-model path is exercised without spawning a real
/// <c>ollama serve</c> or shelling out to <c>ollama pull</c>.
/// </summary>
public sealed class MacPrepHostPullLifecycleTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _stagingRoot;

    public MacPrepHostPullLifecycleTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "freeai-mac27-pull-lifecycle-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
        SsdLayout.EnsureStructure(_tempRoot);
        // MAC35: per-test staging tempdir keeps the lifecycle pin
        // hermetic — never reaches into ~/Library/Caches.
        _stagingRoot = Path.Combine(_tempRoot, "staging");
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task PullModel_FirstCall_StartsTemporaryServerAndPassesItsHost()
    {
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: "/fake/ollama", host: "127.0.0.1:54321");
        var modelService = new FakeModelService(_stagingRoot);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: false,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await lifetime.HandleCommandAsync("pull-model llama3.2:1b");

        Assert.Equal(1, ollamaPackage.StartTemporaryServerCallCount);
        // The pull must use the *temp server* host, not the handshake _ollamaHost.
        // Pre-MAC27 the sidecar passed _ollamaHost (no daemon there), which is
        // the field-bug shape this test pins against regression.
        Assert.Single(modelService.PullCalls);
        Assert.Equal("127.0.0.1:54321", modelService.PullCalls[0].OllamaHost);
        Assert.Equal("llama3.2:1b", modelService.PullCalls[0].ModelTag);
        Assert.Equal("/fake/ollama", modelService.PullCalls[0].OllamaExe);

        // MAC35: the temp server and the pull both target the staging
        // root, NOT the SSD models root. Direct-to-SSD pulls collapse
        // on exFAT; this assertion is the regression pin.
        var ssdModelsRoot = Path.Combine(_tempRoot, SsdLayout.Models);
        Assert.Equal(_stagingRoot, ollamaPackage.LastModelsRoot);
        Assert.Equal(_stagingRoot, modelService.PullCalls[0].ModelsRoot);
        Assert.NotEqual(ssdModelsRoot, modelService.PullCalls[0].ModelsRoot);

        // MAC35: the merge must publish the model to the SSD after the
        // pull completes — manifest at the canonical path is the
        // discoverability gate (DiscoverModelsOnDisk reads manifests).
        var ssdManifestPath = Path.Combine(ssdModelsRoot, "manifests", "registry.ollama.ai", "library", "llama3.2", "1b");
        Assert.True(File.Exists(ssdManifestPath),
            $"Expected merge to copy manifest to {ssdManifestPath}");
    }

    [Fact]
    public async Task PullModel_MultipleCallsInSameLifetime_ReuseSingleServerHandle()
    {
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: "/fake/ollama", host: "127.0.0.1:54321");
        var modelService = new FakeModelService(_stagingRoot);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: false,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await lifetime.HandleCommandAsync("pull-model llama3.2:1b");
        await lifetime.HandleCommandAsync("pull-model qwen2.5:0.5b");
        await lifetime.HandleCommandAsync("pull-model phi3:mini");

        // The Mac PrepApp pulls each selected starter model as its own
        // pull-model command. All three must share a single temp server,
        // matching the Windows pattern of one server per pull batch.
        Assert.Equal(1, ollamaPackage.StartTemporaryServerCallCount);
        Assert.Equal(3, modelService.PullCalls.Count);
        Assert.All(modelService.PullCalls, c => Assert.Equal("127.0.0.1:54321", c.OllamaHost));
        // MAC35: every pull in the batch shares the same staging root.
        Assert.All(modelService.PullCalls, c => Assert.Equal(_stagingRoot, c.ModelsRoot));
    }

    [Fact]
    public async Task DisposeAsync_DisposesTemporaryServerHandle()
    {
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: "/fake/ollama", host: "127.0.0.1:54321");
        var modelService = new FakeModelService(_stagingRoot);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: false,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await lifetime.HandleCommandAsync("pull-model llama3.2:1b");
        var handle = ollamaPackage.LastHandle;
        Assert.NotNull(handle);
        Assert.False(handle!.Disposed);

        await lifetime.DisposeAsync();

        // DisposeAsync must kill the temp `ollama serve` so a Mac sidecar
        // exit can never leak an orphan daemon — same posture the Windows
        // PrepViewModel finally block enforces.
        Assert.True(handle.Disposed);
    }

    [Fact]
    public async Task PullModel_TestMode_DoesNotStartServer()
    {
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: "/fake/ollama", host: "127.0.0.1:54321");
        var modelService = new FakeModelService(_stagingRoot);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: true,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await lifetime.HandleCommandAsync("pull-model llama3.2:1b");

        // Test mode short-circuits before the lookup + server start; the
        // existing MacPrepHostConstructionTests rely on this fast path
        // remaining server-free.
        Assert.Equal(0, ollamaPackage.StartTemporaryServerCallCount);
        Assert.Empty(modelService.PullCalls);
        Assert.Contains("\"testMode\":true", stdout.ToString());
    }

    [Fact]
    public async Task PullModel_ResolveOllamaExeReturnsNull_DoesNotStartServer()
    {
        // Defense-in-depth pin: if Ollama staging silently failed and the
        // resolver returns null, the sidecar must fail loudly *before*
        // starting a temp server. Otherwise we'd start a server with a
        // bogus exe path and the field error would change shape from
        // "binary missing" (clear) to "ollama serve crashed" (confusing).
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: null, host: "127.0.0.1:54321");
        var modelService = new FakeModelService(_stagingRoot);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: false,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => lifetime.HandleCommandAsync("pull-model llama3.2:1b"));

        Assert.Equal(0, ollamaPackage.StartTemporaryServerCallCount);
        Assert.Empty(modelService.PullCalls);
    }

    // --- Fakes ----------------------------------------------------------

    private sealed class FakeOllamaPackageService : IOllamaPackageService
    {
        private readonly string? _resolvedExe;
        private readonly string _host;

        public int StartTemporaryServerCallCount { get; private set; }
        public FakeOllamaServerHandle? LastHandle { get; private set; }
        // MAC35: pin which modelsRoot the temp server was bound to so
        // tests can confirm the OLLAMA_MODELS env var points at staging.
        public string? LastModelsRoot { get; private set; }

        public FakeOllamaPackageService(string? resolvedExe, string host)
        {
            _resolvedExe = resolvedExe;
            _host = host;
        }

        public Task<string> EnsureOllamaReadyAsync(string root, string ollamaUrl, Action<string> onLog,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
            => throw new NotImplementedException("Not exercised by MAC27 lifecycle tests.");

        public string? ResolveOllamaExe(string ollamaDir) => _resolvedExe;

        public Task<IOllamaServerHandle> StartTemporaryServerAsync(
            string ollamaExe, string modelsRoot, Action<string> onLog, CancellationToken ct)
        {
            StartTemporaryServerCallCount++;
            LastModelsRoot = modelsRoot;
            LastHandle = new FakeOllamaServerHandle(_host);
            return Task.FromResult<IOllamaServerHandle>(LastHandle);
        }
    }

    private sealed class FakeOllamaServerHandle : IOllamaServerHandle
    {
        public string Host { get; }
        public bool Disposed { get; private set; }

        public FakeOllamaServerHandle(string host) { Host = host; }

        public void Dispose() { Disposed = true; }
    }

    private sealed record PullCall(string OllamaExe, string ModelsRoot, string ModelTag, string? OllamaHost);

    private sealed class FakeModelService : IModelService
    {
        // MAC35: simulate a successful Ollama pull by writing a synthetic
        // manifest + blob into the modelsRoot the sidecar passes us
        // (which post-MAC35 is the staging root). The real
        // OllamaModelStager.MergeToSsdAsync then reads from this tree
        // and copies to the SSD modelsRoot, exercising the merge step
        // in the lifecycle pin without spawning real Ollama.
        private readonly string? _expectedStagingRoot;

        public List<PullCall> PullCalls { get; } = new();

        public FakeModelService() : this(null) { }

        public FakeModelService(string? expectedStagingRoot)
        {
            _expectedStagingRoot = expectedStagingRoot;
        }

        public Task<ModelPullResult> PullModelAsync(
            string ollamaExe, string modelsRoot, string modelTag,
            Action<string> onLog, CancellationToken ct, string? ollamaHost = null,
            Action<string>? onProgress = null)
        {
            PullCalls.Add(new PullCall(ollamaExe, modelsRoot, modelTag, ollamaHost));

            // Synthesize the on-disk shape Ollama would have produced
            // so the sidecar's subsequent MergeToSsdAsync call has
            // something to copy. Without this, the merge fails with
            // "Staging manifest missing" and the lifecycle pin can't
            // assert the post-merge SSD layout.
            if (_expectedStagingRoot is not null)
            {
                WriteSyntheticPullArtifacts(modelsRoot, modelTag);
            }

            return Task.FromResult(new ModelPullResult("0".PadRight(64, '0'), 1234));
        }

        private static void WriteSyntheticPullArtifacts(string modelsRoot, string modelTag)
        {
            var colon = modelTag.LastIndexOf(':');
            if (colon <= 0 || colon >= modelTag.Length - 1) return;
            var modelName = modelTag[..colon];
            var manifestTag = modelTag[(colon + 1)..];

            var manifestDir = Path.Combine(modelsRoot, "manifests", "registry.ollama.ai", "library", modelName);
            var blobsDir = Path.Combine(modelsRoot, "blobs");
            Directory.CreateDirectory(manifestDir);
            Directory.CreateDirectory(blobsDir);

            // Per-tag deterministic synthetic digest so multiple pulls
            // in one test don't collide on the same blob filename.
            var digest = string.Concat(System.Security.Cryptography.SHA256
                .HashData(System.Text.Encoding.UTF8.GetBytes(modelTag))
                .Select(b => b.ToString("x2")));
            File.WriteAllBytes(Path.Combine(blobsDir, "sha256-" + digest), new byte[256]);
            File.WriteAllText(Path.Combine(manifestDir, manifestTag),
                "{\"layers\":[{\"digest\":\"sha256:" + digest +
                "\",\"size\":256,\"mediaType\":\"application/vnd.ollama.image.layer.model\"}]}");
        }

        // The MAC27 path under test only touches PullModelAsync. Every
        // other IModelService method is unreachable from the pull-model
        // arm with a fake server in place — fail loudly if a future
        // refactor accidentally widens the call site.
        public Task<PortableConfig> LoadConfigAsync(string configPath) => throw new NotImplementedException();
        public Task SaveConfigAsync(string configPath, PortableConfig config) => throw new NotImplementedException();
        public void UpsertModel(List<ModelConfigEntry> models, string name, ModelInstallStatus status) => throw new NotImplementedException();
        public Task UpdateModelStatusAsync(string configPath, string modelName, ModelInstallStatus status, string? sha256 = null, long? sizeBytes = null, DateTime? lastVerifiedUtc = null) => throw new NotImplementedException();
        public IReadOnlyCollection<string> DiscoverModelsOnDisk(string modelsRoot) => throw new NotImplementedException();
        public double EstimatePartialPullProgress(string modelsRoot, string modelTag) => 0.0;
        public Task<bool> VerifyModelAsync(string modelsRoot, string modelTag, string expectedHash, Action<string> onLog, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteModelAsync(string ollamaExe, string modelsRoot, string modelTag, Action<string> onLog, CancellationToken ct, string? ollamaHost = null) => throw new NotImplementedException();
        public List<string> GetSizingWarnings(string modelTag, int? freeDiskGb, int? systemRamGb, int? gpuVramGb) => throw new NotImplementedException();
        public List<string> BuildPullSelectionWarnings(IReadOnlyList<string> models, string rootPath, int? systemRamGb, int? gpuVramGb) => throw new NotImplementedException();
    }
}
