using System.Text.Json;
using FreeAiSsd.MacPrepHost.Services;
using FreeAiSsd.PrepApp.Services;
using FreeAiSsd.Shared;

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
    private readonly OllamaPackageService _ollamaPackage;
    private readonly ModelService _modelService = new();
    private readonly PrereqService _prereqService;
    private readonly ReadinessService _readinessService;

    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public HostLifetime(string ssdRoot, string ollamaHost, TextWriter stdout, TextWriter stderr, bool testMode = false)
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
        _ollamaPackage = new OllamaPackageService();
        _prereqService = new PrereqService(dialogService);
        _readinessService = new ReadinessService(_modelService);
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
            case "verify-model":
                await VerifyModelAsync(payload, ct);
                break;
            case "readiness":
                await RunReadinessAsync(ct);
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
    }

    // --- Command implementations ----------------------------------------

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

        var modelsRoot = Path.Combine(_ssdRoot, SsdLayout.Models);
        var ollamaDir = Path.Combine(_ssdRoot, SsdLayout.MacOllama);
        var ollamaExe = _ollamaPackage.ResolveOllamaExe(ollamaDir)
            ?? throw new FileNotFoundException(
                $"Mac Ollama binary not found under {ollamaDir}. Run stage-ollama first.");

        var result = await _modelService.PullModelAsync(
            ollamaExe, modelsRoot, modelTag, EmitLog, ct, _ollamaHost);

        EmitResult("pull-model", new
        {
            ok = true,
            modelTag,
            sha256 = result.Sha256,
            sizeBytes = result.SizeBytes,
        });
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
