using System.Text.Json;
using FreeAiSsd.MacPrepHost.Services;
using FreeAiSsd.PrepApp;
using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.MacPrepHost;

/// <summary>
/// Owns the prep-core service instances and dispatches stdin commands to
/// them. Mirrors mac-runner-host's HostLifetime shape but with a much
/// smaller surface — prep is one-shot work, not a long-running HTTP host.
///
/// Service registration is deliberate:
///   - ArtifactStagingService, OllamaPackageService, ModelService,
///     PrereqService, ReadinessService — registered, used by commands.
///   - EncryptionService — NOT registered. Swift owns encrypted-config IO
///     via SsdEncryption.swift (MAC5 invariant). Wiring it here would
///     reintroduce a plaintext-config crossing the language boundary for
///     no MVP-scope payoff.
/// </summary>
internal sealed class HostLifetime : IAsyncDisposable
{
    private readonly string _ssdRoot;
    private readonly string _ollamaHost;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly bool _testMode;
    private readonly object _stdoutLock = new();
    private readonly SsdLogger? _logger;

    private readonly ArtifactStagingService _artifactStaging = new();
    private readonly IOllamaPackageService _ollamaPackage;
    private readonly IModelService _modelService;
    private readonly PrereqService _prereqService;
    private readonly ReadinessService _readinessService;
    private readonly ILiveModelCatalogService _liveCatalogService;

    // MAC27: lazily started on the first pull-model command and reused
    // across the batch, mirroring the Windows PrepViewModel pattern at
    // shared/ViewModels/PrepViewModel.cs:782. Without this, `ollama pull`
    // has no daemon to talk to and fails with
    // "could not connect to ollama app, is it running?".
    private IOllamaServerHandle? _ollamaServer;

    // MAC31: holds the linked CTS for the currently-running pull-model
    // command so the `cancel-pull` arm can signal it without touching
    // unrelated commands' tokens. Production wiring detaches pull-model
    // at the Program.cs loop (so the loop can read `cancel-pull` while
    // a pull is in flight); this field is the only shared state between
    // the in-flight pull task and the cancel arm.
    private readonly object _pullCtsLock = new();
    private CancellationTokenSource? _activePullCts;

    // MAC35: production resolves the staging root via
    // OllamaModelStager.ResolveMacStagingRoot. Tests inject a per-test
    // tempdir resolver so they don't pollute ~/Library/Caches and so
    // multiple test fixtures don't share staging state.
    private readonly Func<string> _stagingRootResolver;

    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public HostLifetime(string ssdRoot, string ollamaHost, TextWriter stdout, TextWriter stderr, bool testMode = false)
        : this(ssdRoot, ollamaHost, stdout, stderr, ollamaPackage: null, modelService: null, testMode: testMode, stagingRootResolver: null)
    {
    }

    /// <summary>
    /// Test seam: allows MAC27 lifecycle tests to substitute fake services
    /// for <see cref="IOllamaPackageService"/> (so the temp-server start
    /// path can be exercised without a real Ollama binary on disk) and
    /// <see cref="IModelService"/> (so PullModelAsync doesn't try to spawn
    /// the real CLI). Production wiring goes through the public ctor and
    /// always passes null for both, falling back to the concrete services.
    /// MAC35 adds <paramref name="stagingRootResolver"/> so tests can
    /// redirect the host-stage cache to a per-test tempdir.
    /// </summary>
    internal HostLifetime(
        string ssdRoot, string ollamaHost,
        TextWriter stdout, TextWriter stderr,
        IOllamaPackageService? ollamaPackage,
        IModelService? modelService,
        bool testMode = false,
        Func<string>? stagingRootResolver = null)
    {
        _ssdRoot = ssdRoot;
        _ollamaHost = ollamaHost;
        _stdout = stdout;
        _stderr = stderr;
        _testMode = testMode;

        try
        {
            _logger = new SsdLogger(_ssdRoot, "macos-prep-host");
        }
        catch (Exception ex)
        {
            // Logging on a non-existent ssdRoot during smoke tests should not
            // fatal-fail construction; the parent already gets stderr lines.
            stderr.WriteLine($"Failed to initialize SsdLogger: {ex.Message}");
            _logger = null;
        }

        var dialogService = new NoOpDialogService();
        _ollamaPackage = ollamaPackage ?? new OllamaPackageService();
        _modelService = modelService ?? new ModelService();
        _prereqService = new PrereqService(dialogService);
        _readinessService = new ReadinessService(_modelService);
        _liveCatalogService = new LiveModelCatalogService();
        _stagingRootResolver = stagingRootResolver ?? OllamaModelStager.ResolveMacStagingRoot;
    }

    public void Start()
    {
        WriteLineSafe("ready");
        _logger?.Info($"mac-prep-host ready at ssdRoot={_ssdRoot} (testMode={_testMode})");
    }

    /// <summary>
    /// Dispatches a single stdin command line to the matching prep-core
    /// method. Each command emits <c>log:</c> lines for progress and a
    /// final <c>result: &lt;command&gt; &lt;json&gt;</c> line on success.
    /// Failures throw — Program.cs catches and writes the message to
    /// stderr; the loop continues for follow-up commands.
    /// </summary>
    public async Task HandleCommandAsync(string line, CancellationToken ct = default)
    {
        var (command, payload) = SplitCommand(line);

        switch (command)
        {
            case "ensure-structure":
                EnsureStructure();
                break;
            case "stage-runner":
                await StageRunnerAsync(ct);
                break;
            case "stage-ollama":
                await StageOllamaAsync(ct);
                break;
            case "stage-prereqs":
                await StagePrereqsAsync(ct);
                break;
            case "discover-models":
                DiscoverModels();
                break;
            case "pull-model":
                await PullModelAsync(payload, ct);
                break;
            case "cancel-pull":
                CancelActivePull();
                EmitResult("cancel-pull", new { ok = true });
                break;
            case "verify-model":
                await VerifyModelAsync(payload, ct);
                break;
            case "readiness":
                await RunReadinessAsync(ct);
                break;
            case "refresh-catalog":
                await RefreshCatalogAsync(ct);
                break;
            case "discover-catalog":
                DiscoverCatalog();
                break;
            default:
                await _stderr.WriteLineAsync($"Unknown command: {command}");
                break;
        }
    }

    public Task StopAsync() => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        // MAC27: shut the temp Ollama server down before the sidecar
        // exits so a `ollama serve` orphan can never outlive the parent
        // process. Mirrors the `serverHandle?.Dispose()` finally on
        // shared/ViewModels/PrepViewModel.cs:820.
        try
        {
            _ollamaServer?.Dispose();
        }
        catch
        {
            // Best-effort cleanup; the process may have already exited.
        }
        _ollamaServer = null;

        if (_liveCatalogService is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    // --- Command implementations ----------------------------------------

    private void EnsureStructure()
    {
        // No _testMode short-circuit: SsdLayout.EnsureStructure is just
        // Directory.CreateDirectory calls against _ssdRoot, which the
        // caller already controls. Skipping it under test mode would
        // defeat the drift-pinning test that lives in MacPrepHostSmokeTests.
        SsdLayout.EnsureStructure(_ssdRoot);
        EmitResult("ensure-structure", new { ok = true });
    }

    private async Task StageRunnerAsync(CancellationToken ct)
    {
        if (_testMode)
        {
            EmitLog("test-mode: skipping StageMacRunnerAsync");
            EmitResult("stage-runner", new { ok = true, testMode = true });
            return;
        }

        await _artifactStaging.StageMacRunnerAsync(_ssdRoot, EmitLog, ct);
        EmitResult("stage-runner", new { ok = true });
    }

    private async Task StageOllamaAsync(CancellationToken ct)
    {
        if (_testMode)
        {
            EmitLog("test-mode: skipping StageMacOllamaAsync");
            EmitResult("stage-ollama", new { ok = true, testMode = true });
            return;
        }

        await _artifactStaging.StageMacOllamaAsync(_ssdRoot, EmitLog, ct);
        EmitResult("stage-ollama", new { ok = true });
    }

    private async Task StagePrereqsAsync(CancellationToken ct)
    {
        if (_testMode)
        {
            EmitLog("test-mode: skipping StagePrerequisitesAsync");
            EmitResult("stage-prereqs", new { ok = true, testMode = true });
            return;
        }

        await _prereqService.StagePrerequisitesAsync(_ssdRoot, EmitLog, ct);
        EmitResult("stage-prereqs", new { ok = true });
    }

    private void DiscoverModels()
    {
        var modelsRoot = Path.Combine(_ssdRoot, SsdLayout.Models);
        var models = _modelService.DiscoverModelsOnDisk(modelsRoot);
        EmitResult("discover-models", new { models = models.ToArray() });
    }

    private async Task PullModelAsync(string payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new InvalidOperationException("pull-model requires a model tag argument.");

        var modelTag = payload.Trim();

        if (_testMode)
        {
            EmitLog($"test-mode: skipping PullModelAsync for {modelTag}");
            EmitResult("pull-model", new { ok = true, testMode = true, modelTag });
            return;
        }

        var ssdModelsRoot = Path.Combine(_ssdRoot, SsdLayout.Models);
        var ollamaDir = Path.Combine(_ssdRoot, SsdLayout.MacOllama);
        var ollamaExe = _ollamaPackage.ResolveOllamaExe(ollamaDir)
            ?? throw new FileNotFoundException(
                $"Mac Ollama binary missing at the expected path under {ollamaDir}; staging may have failed silently.");

        // MAC35: pull into a host APFS staging tree, then sequentially
        // merge to the SSD. exFAT FSKit on macOS 15+ cannot sustain
        // Ollama's hardcoded 16 parallel chunk writers; pulling direct
        // to SSD collapsed to ~5 MB/s on the v1.3.14 field test of
        // qwen2.5:7b. Staging eliminates the chunk-stall storm; the
        // sequential merge writes the SSD at exFAT's actual speed.
        var stagingRoot = _stagingRootResolver();
        var sizing = ModelSizingCatalog.Suggest(modelTag);
        OllamaModelStager.EnsureStagingFreeSpace(stagingRoot, sizing.ApproxDiskGb * 1024L * 1024 * 1024);

        // MAC31: linked CTS lets the cancel-pull arm signal this pull
        // without touching any other command's token. Stored under
        // _pullCtsLock so the cancel arm can read it from another
        // command-loop iteration while this task is still in flight.
        using var pullCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_pullCtsLock)
        {
            // Defensive: Swift never issues two pulls in parallel, but
            // if it ever does, the older pull keeps its cancel slot;
            // the newer one runs uncancelable to completion. We never
            // overwrite an in-flight slot.
            _activePullCts ??= pullCts;
        }

        try
        {
            // MAC31 + MAC35: seed reads from the staging root because
            // that's where partial-* blob files now live. After a
            // cancelled pull, the staging tree retains progress and a
            // retry surfaces "Resuming from NN%…" against staging
            // bytes. SSD-side blobs are short-circuited by the merge's
            // size-match check, but they don't affect the seed.
            var seed = ModelOperations.EstimatePartialProgress(stagingRoot, modelTag);
            EmitProgress(seed > 0
                ? $"Resuming {modelTag} from {seed:P0}…"
                : $"Pulling {modelTag}…");

            // MAC27 + MAC35: temp Ollama server is OLLAMA_MODELS-pinned
            // to the staging root so `ollama pull` writes to host APFS,
            // not the SSD. Same lazy-start + reuse pattern across the
            // pull batch as before; OLLAMA_MODELS only takes effect at
            // server start, so a single sidecar lifetime is locked to
            // a single OLLAMA_MODELS — that's fine because every pull
            // through this sidecar now stages.
            if (_ollamaServer is null)
            {
                _ollamaServer = await _ollamaPackage.StartTemporaryServerAsync(
                    ollamaExe, stagingRoot, EmitLog, pullCts.Token);
            }

            // Each NDJSON frame from Ollama's /api/pull is rendered to a
            // single in-place progress line via OllamaPullProgress.ToDisplayString
            // and emitted on the dedicated `progress: ...` stdout channel
            // (the Swift side routes it to a single Text view that
            // overwrites in place rather than scrolling the log surface).
            var result = await _modelService.PullModelAsync(
                ollamaExe, stagingRoot, modelTag, EmitLog, pullCts.Token, _ollamaServer.Host,
                onProgress: progress => EmitProgress(progress.ToDisplayString()));

            // MAC35: sequential merge under the same pull CTS so a
            // user cancel between pull-finish and merge-finish still
            // tears down cleanly. The merge is content-addressed +
            // manifest-written-last so a torn merge is invisible to
            // DiscoverModelsOnDisk and a retry recovers without
            // re-copying intact blobs.
            EmitProgress($"Copying {modelTag} to SSD…");
            await OllamaModelStager.MergeToSsdAsync(
                stagingRoot, ssdModelsRoot, modelTag, EmitLog, pullCts.Token);

            // The staging-side hash IS the SSD-side hash by
            // construction (MergeToSsdAsync does byte-identical copies
            // into content-addressed paths); reusing result.Sha256
            // avoids a redundant ~30 s re-hash of a 4.7 GB blob.
            EmitResult("pull-model", new
            {
                ok = true,
                modelTag,
                sha256 = result.Sha256,
                sizeBytes = result.SizeBytes,
            });
        }
        catch (OperationCanceledException)
        {
            EmitLog($"Pull cancelled for {modelTag}.");
            EmitResult("pull-model", new { ok = false, modelTag, cancelled = true });
        }
        finally
        {
            lock (_pullCtsLock)
            {
                if (ReferenceEquals(_activePullCts, pullCts))
                {
                    _activePullCts = null;
                }
            }
        }
    }

    /// <summary>
    /// MAC31: signals the active pull-model's linked CTS so the
    /// in-flight <c>ollama pull</c> process tree gets killed and the
    /// pull task observes <see cref="OperationCanceledException"/>.
    /// Idempotent — safe to call when no pull is in flight.
    /// </summary>
    private void CancelActivePull()
    {
        CancellationTokenSource? toCancel;
        lock (_pullCtsLock)
        {
            toCancel = _activePullCts;
        }
        if (toCancel is null)
        {
            return;
        }
        try
        {
            toCancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS may have been disposed between the read and the cancel
            // (the pull-model finally block races with us). Safe to
            // ignore — the pull is no longer in flight.
        }
    }

    private async Task VerifyModelAsync(string payload, CancellationToken ct)
    {
        // payload format: "<tag> <expected-hash>"
        var parts = payload.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            throw new InvalidOperationException("verify-model requires '<tag> <expected-hash>' arguments.");

        var modelTag = parts[0];
        var expectedHash = parts[1];

        if (_testMode)
        {
            EmitLog($"test-mode: skipping VerifyModelAsync for {modelTag}");
            EmitResult("verify-model", new { ok = true, testMode = true, modelTag });
            return;
        }

        var modelsRoot = Path.Combine(_ssdRoot, SsdLayout.Models);
        var verified = await _modelService.VerifyModelAsync(modelsRoot, modelTag, expectedHash, EmitLog, ct);
        EmitResult("verify-model", new { ok = verified, modelTag });
    }

    private async Task RunReadinessAsync(CancellationToken ct)
    {
        if (_testMode)
        {
            EmitLog("test-mode: synthetic readiness payload");
            EmitResult("readiness", new
            {
                ok = true,
                testMode = true,
                items = new[] { new { name = "test-mode", status = "Pass", detail = "synthetic" } },
            });
            return;
        }

        var items = await _readinessService.RunReadinessChecksAsync(_ssdRoot, EmitLog, ct);
        // ReadinessItem is (string Check, bool Passed, string Result). Map to
        // a name/status/detail shape the Swift ReadinessRow consumes. The
        // Pass/Fail boolean loses the Warn info that ReadinessItem.Warn(...)
        // encodes (Passed=true, Result=warning text), but for MAC17 MVP the
        // detail string surfaces the warning text in the UI either way.
        var serialized = items.Select(i => new
        {
            name = i.Check,
            status = i.Passed ? "Pass" : "Fail",
            detail = i.Result,
        }).ToArray();

        EmitResult("readiness", new { ok = true, items = serialized });
    }

    /// <summary>
    /// Return the bundled starter-model catalog (the same JSON Windows
    /// PrepApp loads via <see cref="StarterModelCatalogLoader"/>) so the
    /// Mac picker has parity with Windows immediately after staging.
    /// Live refresh is a separate arm (<c>refresh-catalog</c>) so the
    /// user-visible distinction between bundled and live remains clear.
    /// </summary>
    private void DiscoverCatalog()
    {
        var loadResult = StarterModelCatalogLoader.Load(AppContext.BaseDirectory);
        EmitResult("discover-catalog", new
        {
            ok = true,
            warning = loadResult.Warning,
            entries = loadResult.Catalog.Models.Select(m => new
            {
                tag = m.Tag,
                @params = m.Params,
                sizeTier = m.SizeTier,
                description = m.Description,
                useCases = m.UseCases.ToArray(),
                pullCount = m.PullCount,
            }).ToArray(),
        });
    }

    /// <summary>
    /// Live-fetch the starter catalog from a public source. Soft-failure
    /// model: parser/network errors emit <c>result: refresh-catalog
    /// {ok:false, error:...}</c> rather than throwing — matches the
    /// <c>verify-model</c> pattern so the Swift caller always gets a
    /// result line and can fall back to the bundled catalog without
    /// timing out on <c>awaitCommandResult</c>.
    /// </summary>
    private async Task RefreshCatalogAsync(CancellationToken ct)
    {
        if (_testMode)
        {
            EmitLog("test-mode: skipping live catalog fetch");
            EmitResult("refresh-catalog", new
            {
                ok = true,
                testMode = true,
                fetchedAt = DateTimeOffset.UtcNow,
                sourceUrl = "test-mode",
                entries = Array.Empty<object>(),
            });
            return;
        }

        try
        {
            EmitLog("Fetching live model catalog…");
            var result = await _liveCatalogService.FetchAsync(ct);
            EmitLog($"Fetched {result.Catalog.Models.Count} models from {result.SourceUrl}");

            EmitResult("refresh-catalog", new
            {
                ok = true,
                fetchedAt = result.FetchedAt,
                sourceUrl = result.SourceUrl,
                entries = result.Catalog.Models.Select(m => new
                {
                    tag = m.Tag,
                    @params = m.Params,
                    sizeTier = m.SizeTier,
                    description = m.Description,
                    useCases = m.UseCases.ToArray(),
                    pullCount = m.PullCount,
                }).ToArray(),
            });
        }
        catch (LiveCatalogFetchException ex)
        {
            EmitLog($"Catalog refresh failed: {ex.Message}");
            EmitResult("refresh-catalog", new
            {
                ok = false,
                reason = ex.Reason.ToString(),
                error = ex.Message,
                statusCode = ex.StatusCode,
            });
        }
    }

    // --- Output helpers --------------------------------------------------

    private static (string Command, string Payload) SplitCommand(string line)
    {
        var spaceIdx = line.IndexOf(' ');
        if (spaceIdx < 0) return (line.Trim().ToLowerInvariant(), string.Empty);
        return (line[..spaceIdx].Trim().ToLowerInvariant(), line[(spaceIdx + 1)..].TrimStart());
    }

    private void EmitLog(string message)
    {
        _logger?.Info(message);
        WriteLineSafe($"log: {message}");
    }

    /// <summary>
    /// MAC31: writes a <c>progress: ...</c> line that the Swift side
    /// routes to a dedicated single-line UI element. Separate channel
    /// from <c>log:</c> so the PrepApp can render the latest progress
    /// state in one Text view that overwrites in place, rather than
    /// scrolling the log surface with per-tick rewrites. Logged at
    /// Info level so the SSD log captures the full sequence for
    /// post-mortem.
    /// </summary>
    private void EmitProgress(string message)
    {
        _logger?.Info($"progress: {message}");
        WriteLineSafe($"progress: {message}");
    }

    private void EmitResult(string command, object payload)
    {
        var json = JsonSerializer.Serialize(payload, ResultOptions);
        WriteLineSafe($"result: {command} {json}");
    }

    private void WriteLineSafe(string line)
    {
        // prep-core service log callbacks may fire from background threads.
        // Serialize through a local lock so the Swift parent always sees
        // whole lines. Mirrors mac-runner-host's WriteLineSafe.
        lock (_stdoutLock)
        {
            try
            {
                _stdout.WriteLine(line);
                _stdout.Flush();
            }
            catch
            {
                // stdout closed (parent gone). Nothing useful to do — the
                // command loop will observe stdin EOF and exit.
            }
        }
    }
}
