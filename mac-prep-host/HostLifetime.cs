using System.Text.Json;
using FreeAiSsd.MacPrepHost.Services;
using FreeAiSsd.PrepApp;
using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Prereqs;
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
    private readonly PiperStagingService _piperStaging = new();
    private readonly TesseractStagingService _tesseractStaging = new();
    private readonly ReadinessService _readinessService;
    private readonly ILiveModelCatalogService _liveCatalogService;
    private readonly IHuggingFaceCatalogService _hfCatalogService;

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
        _hfCatalogService = new HuggingFaceCatalogService();
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
            case "stage-piper":
                await StagePiperAsync(ct);
                break;
            case "stage-tesseract":
                await StageTesseractAsync(ct);
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
            case "remove-model":
                await RemoveModelAsync(payload, ct);
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
            case "discover-hf-catalog":
                await DiscoverHuggingFaceCatalogAsync(ct);
                break;
            case "search-hf":
                await SearchHuggingFaceAsync(payload, ct);
                break;
            case "set-hf-token":
                // C27 Stage 3: install (or clear) the Bearer token on
                // the sidecar's catalog service so subsequent search /
                // siblings / pull-model HF arms carry the header.
                SetHuggingFaceToken(payload);
                break;
            case "hf-siblings":
                // C27 Stage 4: fetch + project per-quant rows for a
                // single HF repo. Payload: bare repoId (owner/repo).
                await FetchHuggingFaceSiblingsAsync(payload, ct);
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
        if (_hfCatalogService is IDisposable hfDisposable)
        {
            hfDisposable.Dispose();
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

    /// <summary>
    /// Stages Piper for the local Mac arch. Optional payload — failures are
    /// caught and reported as <c>ok=false</c> rather than thrown, mirroring
    /// the Windows PrepViewModel posture: a Piper download failure should not
    /// block the rest of finalize. The Swift UI surfaces the failure to the
    /// user and the Runner falls back to system TTS.
    /// </summary>
    private async Task StagePiperAsync(CancellationToken ct)
    {
        if (_testMode)
        {
            EmitLog("test-mode: skipping StagePiperAsync");
            EmitResult("stage-piper", new { ok = true, testMode = true });
            return;
        }

        try
        {
            var platform = PiperStagingService.DetectCurrentPlatform();
            await _piperStaging.StagePiperAsync(_ssdRoot, platform, EmitLog, ct);
            EmitResult("stage-piper", new { ok = true, platform = platform.ToString() });
        }
        catch (Exception ex)
        {
            EmitLog($"Piper staging failed: {ex.Message}");
            EmitResult("stage-piper", new { ok = false, reason = "stage-exception", message = ex.Message });
        }
    }

    /// <summary>
    /// Stages the Tesseract OCR bundle for the local Mac arch. Optional payload —
    /// failures are caught and reported as <c>ok=false</c> rather than thrown,
    /// mirroring <see cref="StagePiperAsync"/> and the Windows PrepViewModel posture:
    /// an OCR download failure should not block the rest of finalize. The Swift UI
    /// surfaces the failure; the Runner keeps OCR disabled-with-hint until the bundle
    /// is present.
    /// </summary>
    private async Task StageTesseractAsync(CancellationToken ct)
    {
        if (_testMode)
        {
            EmitLog("test-mode: skipping StageTesseractAsync");
            EmitResult("stage-tesseract", new { ok = true, testMode = true });
            return;
        }

        try
        {
            var platform = TesseractStagingService.DetectCurrentPlatform();
            await _tesseractStaging.StageTesseractAsync(_ssdRoot, platform, EmitLog, ct);
            EmitResult("stage-tesseract", new { ok = true, platform = platform.ToString() });
        }
        catch (Exception ex)
        {
            EmitLog($"Tesseract staging failed: {ex.Message}");
            EmitResult("stage-tesseract", new { ok = false, reason = "stage-exception", message = ex.Message });
        }
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

        // C27 Stage 2: for HF tags (`hf.co/owner/repo`) the parameter-
        // count heuristic in ModelSizingCatalog under-counts wildly
        // (defaults to 10 GB regardless of repo size). Fetch siblings
        // up front so EnsureStagingFreeSpace gets real bytes and the
        // user sees the picked filename + size before the pull starts.
        // Gated/private repos refuse early — token auth lands in Stage 3.
        long estimatedBytes;
        if (modelTag.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase))
        {
            // 2026-05-12 field test: a quant-child tag arrives here as
            // `hf.co/owner/repo:Q4_K_M`; HF's /api/models endpoint takes
            // bare `owner/repo` only — the `:tag` suffix yields a 404.
            // Strip it so the sibling fetch hits the right URL.
            var bareRepoId = ExtractHuggingFaceBareRepoId(modelTag);
            estimatedBytes = await EstimateHuggingFaceSizeAsync(bareRepoId, modelTag, ct);
            if (estimatedBytes < 0)
            {
                // EstimateHuggingFaceSizeAsync already emitted the
                // failure result; bail without touching the staging
                // precheck (the user has already seen a clear refusal).
                return;
            }
        }
        else
        {
            var sizing = ModelSizingCatalog.Suggest(modelTag);
            estimatedBytes = sizing.ApproxDiskGb * 1024L * 1024 * 1024;
        }
        OllamaModelStager.EnsureStagingFreeSpace(stagingRoot, estimatedBytes);

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
                // C27 Stage 3: if the user installed an HF token via
                // set-hf-token, propagate it into the Ollama server env
                // so gated / private GGUF pulls can authenticate. Ollama
                // reads HF_TOKEN (and the older HUGGING_FACE_HUB_TOKEN
                // alias) at /api/pull request time. The server starts
                // ONCE per sidecar lifetime and is reused, so the token
                // installed before the first pull persists across the
                // batch — token rotation during a session requires
                // restarting the sidecar (acceptable per Stage 3 scope).
                var extraEnv = BuildHuggingFaceEnv(_hfCatalogService.AuthToken);
                _ollamaServer = await _ollamaPackage.StartTemporaryServerAsync(
                    ollamaExe, stagingRoot, EmitLog, pullCts.Token, extraEnv);
            }

            // Each NDJSON frame from Ollama's /api/pull is rendered to a
            // single in-place progress line via OllamaPullProgress.ToDisplayString
            // and emitted on the dedicated `progress: ...` stdout channel
            // (the Swift side routes it to a single Text view that
            // overwrites in place rather than scrolling the log surface).
            //
            // #49: per-pull layer order so the opaque blob-hash labels
            // become "pulling <model> (layer N of M)" — same fix surface
            // as the Windows PrepViewModel takes for the in-place line.
            var layerOrder = new List<string>();
            var result = await _modelService.PullModelAsync(
                ollamaExe, stagingRoot, modelTag, EmitLog, pullCts.Token, _ollamaServer.Host,
                onProgress: progress =>
                {
                    int? layerIndex = null;
                    int? layerCount = null;
                    if (!string.IsNullOrEmpty(progress.Digest))
                    {
                        var idx = layerOrder.IndexOf(progress.Digest);
                        if (idx < 0)
                        {
                            layerOrder.Add(progress.Digest);
                            idx = layerOrder.Count - 1;
                        }
                        layerIndex = idx + 1;
                        layerCount = layerOrder.Count;
                    }
                    EmitProgress(progress.ToDisplayString(modelTag, layerIndex, layerCount));
                },
                // #48: surface the SHA-compute gap explicitly so a 12B
                // model doesn't read as a hang at 100%. Same purpose as
                // the Windows path's OnPullFinalizing.
                onFinalize: () => EmitProgress($"Finalizing {modelTag}… verifying integrity"));

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
        catch (Exception ex)
        {
            // 2026-05-12 field test: Ollama can return 400 from /api/pull
            // after a 100% download (manifest-assembly failure for some
            // HF quants — e.g. Qwen3.5-9B-GGUF:IQ2_M). Without this catch,
            // the exception bubbled to Program.cs which wrote to stderr
            // only — Swift's PrepHostController waited forever for the
            // pull-model result line and the UI hung at 100%. Emit a
            // failure result so the caller can render the message and
            // advance past the pull step.
            var hint = modelTag.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase)
                ? " Try a different quant (Q4_K_M is the broadest-compatible) or a different HF repo."
                : string.Empty;
            var message = $"Pull failed for {modelTag}: {ex.Message}.{hint}";
            EmitLog(message);
            EmitResult("pull-model", new { ok = false, modelTag, reason = "pull-exception", message });
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
    /// 2026-05-12: strip the <c>hf.co/</c> prefix AND any trailing
    /// <c>:quant</c> suffix from an Ollama-formatted HF tag so HF's
    /// <c>/api/models/{repoId}</c> endpoint accepts it. Field test of
    /// the previous behavior (forwarding the full tag) produced a 404
    /// for every quant-child pull and forced the fallback heuristic.
    /// Internal for direct unit testing.
    /// </summary>
    internal static string ExtractHuggingFaceBareRepoId(string modelTag)
    {
        if (string.IsNullOrEmpty(modelTag)) return string.Empty;
        var afterPrefix = modelTag.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase)
            ? modelTag["hf.co/".Length..]
            : modelTag;
        var colonIdx = afterPrefix.IndexOf(':');
        return colonIdx >= 0 ? afterPrefix[..colonIdx] : afterPrefix;
    }

    /// <summary>
    /// C27 Stage 2: fetch <c>siblings[].lfs.size</c> for a Hugging Face
    /// repo and return the picked-file byte count so the staging
    /// precheck can size off real bytes. Returns -1 (and emits a
    /// pull-model failure result) when the repo is gated/private —
    /// caller should bail without invoking the precheck. Returns 0
    /// when siblings exist but no GGUF size is available; the
    /// EnsureStagingFreeSpace 5 GB floor takes over in that case.
    /// API failures (4xx/5xx other than 401/403) log and fall through
    /// to the heuristic estimate (returns the ModelSizingCatalog
    /// value), matching the WPF posture of "pull still proceeds".
    /// </summary>
    private async Task<long> EstimateHuggingFaceSizeAsync(string repoId, string modelTag, CancellationToken ct)
    {
        HuggingFaceModelDetails details;
        try
        {
            details = await _hfCatalogService.FetchSiblingsAsync(repoId, ct);
        }
        catch (LiveCatalogFetchException ex)
        {
            if (ex.Reason == LiveCatalogFetchReason.NonSuccessStatus
                && (ex.StatusCode == "401" || ex.StatusCode == "403"))
            {
                var msg = $"Hugging Face refused the metadata request for {repoId} ({ex.StatusCode}). " +
                          "If the repo is gated or private, token auth lands in Stage 3.";
                EmitLog(msg);
                EmitResult("pull-model", new { ok = false, modelTag, reason = "hf-auth-required", message = msg });
                return -1;
            }
            EmitLog($"Could not fetch Hugging Face siblings for {repoId} ({ex.Reason}: {ex.Message}); " +
                    "falling back to heuristic size estimate.");
            var fallback = ModelSizingCatalog.Suggest(modelTag);
            return fallback.ApproxDiskGb * 1024L * 1024 * 1024;
        }
        catch (Exception ex)
        {
            EmitLog($"Could not fetch Hugging Face siblings for {repoId}: {ex.Message}; " +
                    "falling back to heuristic size estimate.");
            var fallback = ModelSizingCatalog.Suggest(modelTag);
            return fallback.ApproxDiskGb * 1024L * 1024 * 1024;
        }

        if (details.Gated || details.Private)
        {
            var kind = details.Gated ? "gated" : "private";
            var msg = $"hf.co/{repoId} is {kind} and requires a Hugging Face token. " +
                      "Token auth lands in Stage 3 — pull will fail without it.";
            EmitLog(msg);
            EmitResult("pull-model", new { ok = false, modelTag, reason = $"hf-{kind}", message = msg });
            return -1;
        }

        var pick = HuggingFaceCatalogService.PickSizingFile(details.Siblings);
        if (pick is null || pick.TotalBytes <= 0)
        {
            EmitLog($"No GGUF file sizes published by Hugging Face for {repoId}; " +
                    "proceeding without a sized disk-budget check.");
            return 0;
        }

        var sizeGb = pick.TotalBytes / (1024.0 * 1024 * 1024);
        var partSuffix = pick.PartCount > 1 ? $" ({pick.PartCount}-part split)" : string.Empty;
        EmitLog($"Sizing hf.co/{repoId} from {pick.PrimaryFilename} ≈ {sizeGb:F1} GB{partSuffix}.");
        return pick.TotalBytes;
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

    /// <summary>
    /// C6 Stage 3: removes a model from the SSD via the shared
    /// <see cref="IModelService.DeleteModelAsync"/> path (the same
    /// implementation Windows PrepApp uses). Uses a *short-lived*
    /// temp Ollama server pinned to <c><ssdRoot>/models</c> — the
    /// long-lived <c>_ollamaServer</c> is pinned to the staging root
    /// and would no-op against SSD blobs. To avoid port-11434 collisions
    /// the arm refuses with <c>reason=pull-in-flight</c> when a pull
    /// has already started a server in this sidecar lifetime; the user
    /// is asked to wait until the pull batch completes.
    /// </summary>
    private async Task RemoveModelAsync(string payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            EmitResult("remove-model", new { ok = false, reason = "invalid-payload", message = "remove-model requires a model tag argument." });
            return;
        }

        var modelTag = payload.Trim();

        if (_testMode)
        {
            EmitLog($"test-mode: skipping RemoveModelAsync for {modelTag}");
            EmitResult("remove-model", new { ok = true, testMode = true, modelTag });
            return;
        }

        // M16 (GH #277): the guard must check whether a pull is ACTUALLY
        // in flight, not whether _ollamaServer exists. _ollamaServer is
        // started lazily on the first pull (line 391) and reused across
        // the batch — it stays alive for the rest of the sidecar lifetime
        // by design (MAC27 + MAC35). The pre-fix guard checked
        // `_ollamaServer is not null`, which permanently blocked Remove
        // after any successful pull and surfaced a misleading "wait for
        // the pull batch to complete" message that waiting could never
        // fix. The real in-flight signal is `_activePullCts`, which the
        // pull task sets at line 356 and clears in its finally at line
        // 454. Read it under _pullCtsLock to stay race-free against a
        // concurrent pull task — production's Program.cs detaches pulls
        // so the remove arm CAN execute while a pull is still running,
        // and that case must still refuse cleanly.
        lock (_pullCtsLock)
        {
            if (_activePullCts is not null)
            {
                EmitResult("remove-model", new
                {
                    ok = false,
                    modelTag,
                    reason = "pull-in-flight",
                    message = "Remove is unavailable while a model pull is running in this sidecar lifetime. Wait for the pull batch to complete and try again."
                });
                return;
            }
        }

        // No pull is in flight, but a previous pull may have left
        // _ollamaServer bound to port 11434 (pinned to the staging root).
        // Dispose it now so the SSD-pinned temp server below can claim
        // the port. Reusing the staging-pinned server against the SSD
        // models root would no-op (its OLLAMA_MODELS env is the staging
        // tree), causing a silent "removed" result while the blob stays
        // on disk — the original D14 data-integrity concern.
        if (_ollamaServer is not null)
        {
            EmitLog("Disposing idle staging-pinned Ollama server before remove (frees port 11434).");
            try
            {
                _ollamaServer.Dispose();
            }
            catch (Exception ex)
            {
                // Dispose throwing should not block the remove — log and
                // proceed. The subsequent StartTemporaryServerAsync will
                // surface a clean port-bind failure if the port really
                // is still held.
                EmitLog($"Idle Ollama server dispose threw (continuing): {ex.Message}");
            }
            _ollamaServer = null;
        }

        var ssdModelsRoot = Path.Combine(_ssdRoot, SsdLayout.Models);
        var ollamaDir = Path.Combine(_ssdRoot, SsdLayout.MacOllama);
        var ollamaExe = _ollamaPackage.ResolveOllamaExe(ollamaDir);
        if (ollamaExe is null)
        {
            EmitResult("remove-model", new
            {
                ok = false,
                modelTag,
                reason = "ollama-missing",
                message = $"Mac Ollama binary missing at {ollamaDir}; cannot remove without a server."
            });
            return;
        }

        try
        {
            // Server scope is the lifetime of this method only — disposed
            // before we return so a subsequent pull can claim port 11434.
            using var serverHandle = await _ollamaPackage.StartTemporaryServerAsync(
                ollamaExe, ssdModelsRoot, EmitLog, ct);

            EmitLog($"Deleting {modelTag} from {ssdModelsRoot} via ollama rm…");
            await _modelService.DeleteModelAsync(
                ollamaExe, ssdModelsRoot, modelTag, EmitLog, ct, serverHandle.Host);
            EmitResult("remove-model", new { ok = true, modelTag });
        }
        catch (OperationCanceledException)
        {
            EmitResult("remove-model", new { ok = false, modelTag, cancelled = true });
        }
        catch (Exception ex)
        {
            EmitResult("remove-model", new
            {
                ok = false,
                modelTag,
                reason = "remove-exception",
                message = ex.Message
            });
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
            entries = BuildCatalogEntries(loadResult.Catalog.Models),
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
                entries = BuildCatalogEntries(result.Catalog.Models),
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

    /// <summary>
    /// C27 Stage 1: fetch the default "popular GGUF" page from the
    /// Hugging Face Search API. Soft-failure mirrors
    /// <see cref="RefreshCatalogAsync"/> so the Swift caller can fall
    /// back without timing out on <c>awaitCommandResult</c>.
    /// </summary>
    private async Task DiscoverHuggingFaceCatalogAsync(CancellationToken ct)
    {
        if (_testMode)
        {
            EmitLog("test-mode: skipping HF discover-catalog fetch");
            EmitResult("discover-hf-catalog", new
            {
                ok = true,
                testMode = true,
                fetchedAt = DateTimeOffset.UtcNow,
                sourceUrl = "test-mode",
                query = (string?)null,
                entries = Array.Empty<object>(),
            });
            return;
        }

        try
        {
            EmitLog("Fetching Hugging Face popular GGUF…");
            var result = await _hfCatalogService.SearchAsync(new HuggingFaceSearchQuery(), ct);
            EmitLog($"Fetched {result.Catalog.Models.Count} GGUF repos from {result.SourceUrl}");
            EmitResult("discover-hf-catalog", new
            {
                ok = true,
                fetchedAt = result.FetchedAt,
                sourceUrl = result.SourceUrl,
                query = result.Query,
                entries = BuildCatalogEntries(result.Catalog.Models),
            });
        }
        catch (LiveCatalogFetchException ex)
        {
            EmitLog($"HF discover failed: {ex.Message}");
            EmitResult("discover-hf-catalog", new
            {
                ok = false,
                reason = ex.Reason.ToString(),
                error = ex.Message,
                statusCode = ex.StatusCode,
            });
        }
    }

    /// <summary>
    /// C27 Stage 1: search the Hugging Face Search API for GGUF repos
    /// matching a user-typed query. Payload is a single-line JSON
    /// object: <c>{"search":"qwen","limit":50,"sort":"downloads"}</c>.
    /// Limit and sort are optional; only <c>search</c> is required.
    /// </summary>
    private async Task SearchHuggingFaceAsync(string payload, CancellationToken ct)
    {
        if (_testMode)
        {
            EmitLog("test-mode: skipping HF search-hf fetch");
            EmitResult("search-hf", new
            {
                ok = true,
                testMode = true,
                fetchedAt = DateTimeOffset.UtcNow,
                sourceUrl = "test-mode",
                query = (string?)null,
                entries = Array.Empty<object>(),
            });
            return;
        }

        HuggingFaceSearchQuery query;
        try
        {
            query = ParseHuggingFaceSearchPayload(payload);
        }
        catch (JsonException ex)
        {
            EmitLog($"search-hf payload parse failed: {ex.Message}");
            EmitResult("search-hf", new
            {
                ok = false,
                reason = "InvalidPayload",
                error = ex.Message,
            });
            return;
        }

        try
        {
            EmitLog($"Searching Hugging Face for '{query.Search}'…");
            var result = await _hfCatalogService.SearchAsync(query, ct);
            EmitLog($"Fetched {result.Catalog.Models.Count} GGUF repos for query '{query.Search}'");
            EmitResult("search-hf", new
            {
                ok = true,
                fetchedAt = result.FetchedAt,
                sourceUrl = result.SourceUrl,
                query = result.Query,
                entries = BuildCatalogEntries(result.Catalog.Models),
            });
        }
        catch (LiveCatalogFetchException ex)
        {
            EmitLog($"HF search failed: {ex.Message}");
            EmitResult("search-hf", new
            {
                ok = false,
                reason = ex.Reason.ToString(),
                error = ex.Message,
                statusCode = ex.StatusCode,
            });
        }
    }

    /// <summary>
    /// C27 Stage 4: fetch <c>siblings[]</c> for a Hugging Face repo and
    /// emit a JSON payload the SwiftUI host can fold into its picker
    /// as per-quant child rows. Mirrors the WPF
    /// <c>FetchHuggingFaceQuantsAsync</c> path: gated/private repos
    /// emit an empty children list with a typed reason so the host
    /// surfaces the Stage 3 token nudge.
    /// </summary>
    private async Task FetchHuggingFaceSiblingsAsync(string payload, CancellationToken ct)
    {
        var repoId = (payload ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(repoId))
        {
            EmitResult("hf-siblings", new
            {
                ok = false,
                reason = "InvalidPayload",
                error = "hf-siblings requires a repoId argument (owner/repo).",
            });
            return;
        }

        HuggingFaceModelDetails details;
        try
        {
            details = await _hfCatalogService.FetchSiblingsAsync(repoId, ct);
        }
        catch (LiveCatalogFetchException ex)
        {
            EmitLog($"hf-siblings fetch failed for {repoId}: {ex.Message}");
            EmitResult("hf-siblings", new
            {
                ok = false,
                repoId,
                reason = ex.Reason.ToString(),
                error = ex.Message,
                statusCode = ex.StatusCode,
            });
            return;
        }

        if (details.Gated || details.Private)
        {
            var kind = details.Gated ? "gated" : "private";
            EmitResult("hf-siblings", new
            {
                ok = true,
                repoId,
                gated = details.Gated,
                @private = details.Private,
                reason = $"hf-{kind}",
                quants = Array.Empty<object>(),
            });
            return;
        }

        EmitResult("hf-siblings", new
        {
            ok = true,
            repoId,
            gated = false,
            @private = false,
            quants = HuggingFaceQuantProjector.ProjectAsWirePayload(repoId, details.Siblings),
        });
    }

    /// <summary>
    /// C27 Stage 3: build the env-var dictionary passed into the temp
    /// Ollama server for HF GGUF pulls. Returns null (= no extra env)
    /// when the token is missing/empty so we don't grow the process
    /// env unnecessarily. <c>HF_TOKEN</c> is the modern name; we also
    /// set <c>HUGGING_FACE_HUB_TOKEN</c> for older Ollama builds that
    /// haven't picked up the rename. Internal for direct unit testing.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? BuildHuggingFaceEnv(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var trimmed = token.Trim();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HF_TOKEN"] = trimmed,
            ["HUGGING_FACE_HUB_TOKEN"] = trimmed,
        };
    }

    /// <summary>
    /// C27 Stage 3: install (or clear) the Bearer token on the sidecar's
    /// catalog service. Payload is a single-line JSON object:
    /// <c>{"token":"hf_..."}</c>; empty / missing token clears the
    /// installed value and reverts to anonymous mode. Defense-in-depth:
    /// we never log the token value, only whether one was installed or
    /// cleared — the JSON payload echoes through the EmitLog NDJSON
    /// stream and ultimately to a SwiftUI log strip that the user can
    /// screenshot.
    /// </summary>
    private void SetHuggingFaceToken(string payload)
    {
        string? token = null;
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.TryGetProperty("token", out var t)
                    && t.ValueKind == JsonValueKind.String)
                {
                    var raw = t.GetString();
                    token = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
                }
            }
            catch (JsonException ex)
            {
                EmitLog($"set-hf-token payload parse failed: {ex.Message}");
                EmitResult("set-hf-token", new
                {
                    ok = false,
                    reason = "InvalidPayload",
                    error = ex.Message,
                });
                return;
            }
        }

        _hfCatalogService.UpdateAuthToken(token);
        EmitLog(token is null
            ? "Hugging Face token cleared; subsequent HF requests anonymous."
            : "Hugging Face token installed; subsequent HF requests authenticated.");
        EmitResult("set-hf-token", new { ok = true, tokenInstalled = token is not null });
    }

    /// <summary>
    /// C27 Stage 1: parse a <c>search-hf</c> payload. Accepts the JSON
    /// object shape <c>{"search":"...","limit":50,"sort":"downloads"}</c>;
    /// missing fields fall back to the <see cref="HuggingFaceSearchQuery"/>
    /// defaults (popular GGUF page). Bare empty payload returns the
    /// default query, mirroring <c>discover-hf-catalog</c>.
    /// Internal for direct unit testing.
    /// </summary>
    internal static HuggingFaceSearchQuery ParseHuggingFaceSearchPayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new HuggingFaceSearchQuery();
        }
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var search = root.TryGetProperty("search", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
        var limit = root.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number
            ? l.GetInt32()
            : HuggingFaceCatalogService.DefaultLimit;
        var sort = root.TryGetProperty("sort", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString() ?? "downloads"
            : "downloads";
        return new HuggingFaceSearchQuery(search, limit, sort);
    }

    /// <summary>
    /// C27 Stage 1 / C24 lesson cashed in: single projection helper
    /// used by every catalog-emitting arm (<c>discover-catalog</c>,
    /// <c>refresh-catalog</c>, <c>discover-hf-catalog</c>, <c>search-hf</c>).
    /// Adding a wire field becomes a one-site change here instead of
    /// drifting across four arm bodies — exactly the regression class
    /// C24 named when refresh-catalog dropped <c>parametersBillion</c>
    /// + <c>lastUpdated</c> after PR #259 added them only on the
    /// discover-catalog arm.
    /// </summary>
    private static object[] BuildCatalogEntries(IEnumerable<StarterModelEntry> models)
        => models.Select(m => new
        {
            tag = m.Tag,
            @params = m.Params,
            sizeTier = m.SizeTier,
            description = m.Description,
            useCases = m.UseCases.ToArray(),
            pullCount = m.PullCount,
            parametersBillion = m.ParametersBillion,
            lastUpdated = m.LastUpdated,
            source = m.Source.ToString(),
            // C27 Stage 4: HF rows are repo-level — the SwiftUI host
            // surfaces a DisclosureGroup chevron whose click triggers
            // the `hf-siblings` arm to populate quant children.
            isExpandable = m.Source == ModelSource.HuggingFace,
        }).ToArray<object>();

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

    /// <summary>
    /// M17 (GH #278): Program.cs's pre-pull exception fallback used to
    /// write its <c>result: pull-model …</c> line directly to stdout via
    /// <c>WriteLineAsync</c>, bypassing <see cref="_stdoutLock"/>. Because
    /// pull-model runs on a detached <see cref="Task.Run"/> while the
    /// command loop keeps emitting <c>progress:</c> and result frames for
    /// other commands, two threads could write stdout concurrently and
    /// produce a torn line that Swift's <c>PrepHostController</c> parser
    /// rejects — reintroducing the 100%-hang class PR #272 was meant to
    /// prevent. This entrypoint routes the fallback through the same
    /// lock every other sidecar write uses.
    /// </summary>
    internal void EmitFailureResult(string command, object payload)
        => EmitResult(command, payload);

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
