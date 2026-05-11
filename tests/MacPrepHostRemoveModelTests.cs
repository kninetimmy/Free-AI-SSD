using FreeAiSsd.MacPrepHost;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Services;
using Xunit;

namespace FreeAiSsd.Tests;

/// <summary>
/// C6 Stage 3 pin for the Mac sidecar's `remove-model` arm. Mirrors the
/// MacPrepHostPullLifecycleTests shape: inject fake
/// <see cref="IOllamaPackageService"/> + <see cref="IModelService"/> so the
/// path is exercised without spawning a real `ollama serve` or shelling
/// out to `ollama rm`. Pins the cross-OS contract that Remove on Mac
/// uses the same `DeleteModelAsync` code path Windows PrepApp uses
/// (single source of truth in prep-core), but with a server pinned to
/// the SSD models root rather than the staging root.
/// </summary>
public sealed class MacPrepHostRemoveModelTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _stagingRoot;

    public MacPrepHostRemoveModelTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "freeai-c6-remove-model-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
        SsdLayout.EnsureStructure(_tempRoot);
        _stagingRoot = Path.Combine(_tempRoot, "staging");
        Directory.CreateDirectory(_stagingRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task RemoveModel_DelegatesToDeleteModelAsync_AgainstSsdModelsRoot()
    {
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: "/fake/ollama", host: "127.0.0.1:54321");
        var modelService = new FakeModelService();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: false,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await lifetime.HandleCommandAsync("remove-model llama3.2:1b");

        // Pinned: the temp server bound the SSD models root, NOT staging.
        // If this regresses to staging, `ollama rm` would no-op (the
        // blobs we want to delete live on the SSD, not in staging) and
        // the user would see "removed" while the model still occupies
        // disk space — a silent data integrity bug.
        var ssdModelsRoot = Path.Combine(_tempRoot, SsdLayout.Models);
        Assert.Equal(1, ollamaPackage.StartTemporaryServerCallCount);
        Assert.Equal(ssdModelsRoot, ollamaPackage.LastModelsRoot);

        // DeleteModelAsync received the right args.
        Assert.Single(modelService.DeleteCalls);
        Assert.Equal("/fake/ollama", modelService.DeleteCalls[0].OllamaExe);
        Assert.Equal(ssdModelsRoot, modelService.DeleteCalls[0].ModelsRoot);
        Assert.Equal("llama3.2:1b", modelService.DeleteCalls[0].ModelTag);
        Assert.Equal("127.0.0.1:54321", modelService.DeleteCalls[0].OllamaHost);

        // ok=true emitted on the result channel.
        Assert.Contains("\"ok\":true", stdout.ToString());
        Assert.Contains("\"modelTag\":\"llama3.2:1b\"", stdout.ToString());
    }

    [Fact]
    public async Task RemoveModel_DisposesTempServerAfterCompletion()
    {
        // Critical to allow a subsequent pull (or another remove) to
        // claim port 11434 — the short-lived server is the whole reason
        // we don't reuse _ollamaServer.
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: "/fake/ollama", host: "127.0.0.1:54321");
        var modelService = new FakeModelService();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: false,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await lifetime.HandleCommandAsync("remove-model llama3.2:1b");

        Assert.NotNull(ollamaPackage.LastHandle);
        Assert.True(ollamaPackage.LastHandle!.Disposed,
            "Temp server must be disposed at the end of remove-model so a subsequent pull can claim port 11434.");
    }

    [Fact]
    public async Task RemoveModel_EmptyPayload_EmitsInvalidPayloadResult_WithoutTouchingServer()
    {
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: "/fake/ollama", host: "127.0.0.1:54321");
        var modelService = new FakeModelService();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: false,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await lifetime.HandleCommandAsync("remove-model");

        Assert.Equal(0, ollamaPackage.StartTemporaryServerCallCount);
        Assert.Empty(modelService.DeleteCalls);
        Assert.Contains("\"ok\":false", stdout.ToString());
        Assert.Contains("\"reason\":\"invalid-payload\"", stdout.ToString());
    }

    [Fact]
    public async Task RemoveModel_TestMode_DoesNotStartServer()
    {
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: "/fake/ollama", host: "127.0.0.1:54321");
        var modelService = new FakeModelService();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: true,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await lifetime.HandleCommandAsync("remove-model llama3.2:1b");

        Assert.Equal(0, ollamaPackage.StartTemporaryServerCallCount);
        Assert.Empty(modelService.DeleteCalls);
        Assert.Contains("\"testMode\":true", stdout.ToString());
    }

    [Fact]
    public async Task RemoveModel_ResolveOllamaExeReturnsNull_EmitsOllamaMissingReason()
    {
        // The new step's startup path doesn't re-stage Ollama; if the
        // staged binary is missing on a re-entered drive, remove must
        // fail cleanly rather than crash. Pull's parallel test asserts
        // a FileNotFoundException throws; the remove path emits a
        // structured result so the Mac UI can render the failure inline.
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: null, host: "127.0.0.1:54321");
        var modelService = new FakeModelService();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: false,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        await lifetime.HandleCommandAsync("remove-model llama3.2:1b");

        Assert.Equal(0, ollamaPackage.StartTemporaryServerCallCount);
        Assert.Empty(modelService.DeleteCalls);
        Assert.Contains("\"ok\":false", stdout.ToString());
        Assert.Contains("\"reason\":\"ollama-missing\"", stdout.ToString());
    }

    [Fact]
    public async Task RemoveModel_AfterPullStartsServerInSameLifetime_RefusesWithPullInFlight()
    {
        // C6 risk #3: a pull batch in this sidecar lifetime has already
        // bound port 11434 to _ollamaServer (pinned to the staging root).
        // Starting a second server on the same port would fail; reusing
        // the pull-time server against the SSD models root would no-op.
        // The remove arm refuses with pull-in-flight so the UI surfaces
        // "wait until the pull finishes."
        var ollamaPackage = new FakeOllamaPackageService(resolvedExe: "/fake/ollama", host: "127.0.0.1:54321");
        var modelService = new FakeModelService();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        await using var lifetime = new HostLifetime(
            _tempRoot, "http://127.0.0.1:11434", stdout, stderr,
            ollamaPackage: ollamaPackage, modelService: modelService, testMode: false,
            stagingRootResolver: () => _stagingRoot);
        lifetime.Start();

        // Run a pull first to start _ollamaServer against the staging root.
        await lifetime.HandleCommandAsync("pull-model llama3.2:1b");
        Assert.Equal(1, ollamaPackage.StartTemporaryServerCallCount);

        // Clear stdout snapshot of pull so we only inspect remove output.
        var pullOutput = stdout.ToString();

        await lifetime.HandleCommandAsync("remove-model qwen2.5:0.5b");

        // Remove must NOT start a second server.
        Assert.Equal(1, ollamaPackage.StartTemporaryServerCallCount);
        Assert.Empty(modelService.DeleteCalls);
        var removeOutput = stdout.ToString().Substring(pullOutput.Length);
        Assert.Contains("\"ok\":false", removeOutput);
        Assert.Contains("\"reason\":\"pull-in-flight\"", removeOutput);
    }

    // --- Fakes ----------------------------------------------------------

    private sealed class FakeOllamaPackageService : IOllamaPackageService
    {
        private readonly string? _resolvedExe;
        private readonly string _host;

        public int StartTemporaryServerCallCount { get; private set; }
        public FakeOllamaServerHandle? LastHandle { get; private set; }
        public string? LastModelsRoot { get; private set; }

        public FakeOllamaPackageService(string? resolvedExe, string host)
        {
            _resolvedExe = resolvedExe;
            _host = host;
        }

        public Task<string> EnsureOllamaReadyAsync(string root, Action<string> onLog,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
            => throw new NotImplementedException();

        public string? ResolveOllamaExe(string ollamaDir) => _resolvedExe;

        public Task<IOllamaServerHandle> StartTemporaryServerAsync(
            string ollamaExe,
            string modelsRoot,
            Action<string> onLog,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? extraEnv = null)
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

    private sealed record DeleteCall(string OllamaExe, string ModelsRoot, string ModelTag, string? OllamaHost);

    private sealed class FakeModelService : IModelService
    {
        public List<DeleteCall> DeleteCalls { get; } = new();

        public Task DeleteModelAsync(string ollamaExe, string modelsRoot, string modelTag,
            Action<string> onLog, CancellationToken ct, string? ollamaHost = null)
        {
            DeleteCalls.Add(new DeleteCall(ollamaExe, modelsRoot, modelTag, ollamaHost));
            return Task.CompletedTask;
        }

        // The C6 remove path doesn't exercise pull — but a pull-then-remove
        // test needs PullModelAsync to be a no-op rather than throw.
        public Task<ModelPullResult> PullModelAsync(
            string ollamaExe, string modelsRoot, string modelTag,
            Action<string> onLog, CancellationToken ct, string? ollamaHost = null,
            Action<OllamaPullProgress>? onProgress = null)
        {
            // Write a minimal staging artifact so MergeToSsdAsync's manifest
            // copy doesn't blow up the test setup.
            WriteSyntheticPullArtifacts(modelsRoot, modelTag);
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

            var digest = string.Concat(System.Security.Cryptography.SHA256
                .HashData(System.Text.Encoding.UTF8.GetBytes(modelTag))
                .Select(b => b.ToString("x2")));
            File.WriteAllBytes(Path.Combine(blobsDir, "sha256-" + digest), new byte[256]);
            File.WriteAllText(Path.Combine(manifestDir, manifestTag),
                "{\"layers\":[{\"digest\":\"sha256:" + digest +
                "\",\"size\":256,\"mediaType\":\"application/vnd.ollama.image.layer.model\"}]}");
        }

        public Task<PortableConfig> LoadConfigAsync(string configPath) => throw new NotImplementedException();
        public Task SaveConfigAsync(string configPath, PortableConfig config) => throw new NotImplementedException();
        public void UpsertModel(List<ModelConfigEntry> models, string name, ModelInstallStatus status) => throw new NotImplementedException();
        public Task UpdateModelStatusAsync(string configPath, string modelName, ModelInstallStatus status, string? sha256 = null, long? sizeBytes = null, DateTime? lastVerifiedUtc = null) => throw new NotImplementedException();
        public IReadOnlyCollection<string> DiscoverModelsOnDisk(string modelsRoot) => throw new NotImplementedException();
        public double EstimatePartialPullProgress(string modelsRoot, string modelTag) => 0.0;
        public Task<bool> VerifyModelAsync(string modelsRoot, string modelTag, string expectedHash, Action<string> onLog, CancellationToken ct) => throw new NotImplementedException();
        public List<string> GetSizingWarnings(string modelTag, int? freeDiskGb, int? systemRamGb, int? gpuVramGb) => throw new NotImplementedException();
        public List<string> BuildPullSelectionWarnings(IReadOnlyList<string> models, string rootPath, int? systemRamGb, int? gpuVramGb) => throw new NotImplementedException();
    }
}
