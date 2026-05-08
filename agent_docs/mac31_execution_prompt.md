# MAC31 — Pull UX: cancel button, single progress line, preserve partial-download progress (cross-OS)

Execution prompt for the top-of-queue Mac usability item filed
2026-05-08 from the v1.3.10 mac field test (8B model pull on a
slow connection). Three discrete sub-bugs in the prep-time pull
flow, bundled into one cross-OS PR per the 2026-05-07 dual-OS
parity rule.

Backlog entry: `agent_docs/mac_project_backlog.md` → "MAC31 — Pull
UX: cancel button, single progress line, preserve
partial-download progress". Sibling-bug context:
`agent_docs/project_state.md` → MAC28 (the `ConsumeAsync` line
forwarder this PR amends) and MAC33 (most recent disk-truth
ship — same prep-core surface).

## Goal

A 5 GB pull on a slow connection presents as a **single
in-place progress line** that climbs monotonically, the user
can **cancel it** at any time without orphaning partial blobs,
and clicking **Retry** picks up from where the previous attempt
left off rather than redownloading from 0%. Both PrepApps
(Mac SwiftUI + Windows WPF) get the Cancel UI; the prep-core
log filter and resume-seeding helper are shared so both pull
paths inherit the fixes automatically.

## Why now

**Top of queue after MAC33 shipped.** v1.3.11 closed the Mac
end-to-end loop on the code side (prep + readiness all-green +
Runner picker populated), so the field-test flow now exposes the
next layer of friction: the pull step itself is unusable on slow
connections. User reported the symptom as "looks like a restart
loop" (it isn't — that's ANSI cursor-rewrite spam) and "I have
no way to bail out short of force-quitting" (true — both
PrepApps go modal during `pull-model` with a 30-min timeout).
Without MAC31 the encrypted-config + disk-truth + readiness wins
shipped in MAC29/MAC33 are gated behind a pull experience that
feels broken even when it's working.

## Root cause — confirmed at kickoff via grep

Three independent issues, all stack-aligned around the prep-time
pull pipeline:

**(a) No cancel UI.** Mac PrepApp blocks on
`hostController.send("pull-model \(tag)", timeout: 1800)`
(`mac-prep-app/Sources/PrepViewModel.swift` ≈line 388) with no
abort handle exposed to the view. Windows PrepApp's
`shared/ViewModels/PrepViewModel.cs` already passes a
`CancellationToken` into `_modelService.PullModelAsync` but the
WPF view never binds a cancel command to a
`CancellationTokenSource`. `mac-prep-host/HostLifetime.cs` has no
`cancel-pull` arm; `prep-core/Services/ModelOperations.PullModelAsync`
already calls `ct.Register(() => kill process tree)` (around line
340 from MAC28's neighborhood — confirm exact line at kickoff),
so the kill-side plumbing exists; only the user-visible escape
hatch is missing.

**(b) ANSI cursor-rewrite escape spam.** Ollama's
`pull` TUI uses `\x1b[?25h`, `\x1b[?25l`, `\x1b[2K`, `\x1b[1G`,
`\x1b[A` to overwrite a single progress line in place in a real
terminal. MAC28's `OllamaServerHandle.ConsumeAsync` captures
stdout line-by-line and forwards each tick to `onLog`, but
doesn't strip or coalesce the rewrite sequences. Result: the log
pane grows by `pulling <hash>... NN%` per tick (~1Hz × multiple
chunks) with literal escape codes appearing as garbage.

**(c) Progress UI resets to 0% on retry.** Ollama's pull is
genuinely resumable: partials persist as
`<ssdRoot>/models/blobs/sha256-<hex>-partial-N` and are
re-validated on the next `pull` invocation. Our progress display
reads only the live `ollama pull` stdout, so retry shows 0% +
re-validation phase before continuing — confusing because users
expect "Retry" to mean "pick up from where I was."

## Dual-OS review pass

**Mac surfaces:**
- `mac-prep-app/Sources/main.swift` — Models step view (search
  for the pulling progress UI). Add a Cancel button bound to a
  `cancelPull()` method on `PrepViewModel`.
- `mac-prep-app/Sources/PrepViewModel.swift` — hold the active
  pull `Task` and a `pullCancelTokenSource`-equivalent state.
  Cancel calls `task.cancel()` and dispatches `cancel-pull` over
  the host channel. New computed/`@Published` `pullProgressLine`
  fed by host progress events; bind it to the view.

**Windows surfaces:**
- `shared/ViewModels/PrepViewModel.cs` — already takes a `ct`
  into pull. Add a `CancellationTokenSource` field, expose
  `CancelPullCommand`, ensure existing pull invocation uses
  `_pullCts.Token`. Add `PullProgressLine` observable.
- `prep-app/MainWindow.xaml` (or whichever XAML hosts the pull
  step) — bind a Cancel button + a single `TextBlock` for the
  in-place progress line.

**Shared-core surfaces:**
- `prep-core/OllamaServerHandle.cs` — `ConsumeAsync` (or a sibling
  helper invoked from it) gains an ANSI strip + a progress
  coalesce. New `onProgress` callback alongside `onLog`,
  defaulting to a no-op so existing callers (Network Mode
  lifecycle, MAC28 unit tests, any other ConsumeAsync consumers)
  compile unchanged. Lines matching `^pulling [a-f0-9]+\.{3}\s*\d+%`
  (after ANSI strip) route to `onProgress`; everything else still
  flows through `onLog` so MAC28's stderr diagnostics survive.
- `prep-core/Services/ModelOperations.cs` — new helper
  (`SeedPartialDownloadProgress` or similar) that, given a
  `modelsRoot` and a model tag, scans `models/blobs/` for
  `*-partial-*` files matching the manifest's expected blob
  digests, sums their sizes against the manifest's total layer
  size, and returns a `double` fraction in `[0.0, 1.0]`. Returns
  `0.0` if no manifest yet exists for that model. Both Windows
  and Mac pull paths call it pre-spawn; the result seeds the
  PrepApp's progress line until the first live `ollama pull`
  tick takes over.
- `mac-prep-host/HostLifetime.cs` — new `cancel-pull` arm. Holds
  the active pull's `CancellationTokenSource` (or a registry keyed
  by tag if multiple concurrent pulls become a thing — they
  aren't today). Cancel signals the token; existing
  `ModelOperations.PullModelAsync` `ct.Register` then kills the
  process tree.

**Non-surfaces (one-line justification):**
- `runner-core/` — Runner does not pull; pulls are prep-time only.
- `runner/` (WPF), `mac-runner/`, `mac-runner-host/` — same.
- `companion/` — no pull UI.
- `runner-cli/` — no pull UI.

**Decision:** **Bundle both OSes in one PR.** All three sub-bugs
share prep-core surfaces; splitting would mean two PRs touching
the same `OllamaServerHandle.ConsumeAsync` signature, which is
exactly the churn the 2026-05-07 dual-OS rule was written to
prevent.

## Architecture

### Sub-bug (a) — Cancel button

**Mac side:**
```swift
// PrepViewModel.swift
private var activePullTask: Task<Void, Never>?
@Published var canCancelPull: Bool = false

func startPull(tag: String) {
    activePullTask = Task { [weak self] in
        guard let self else { return }
        await MainActor.run { self.canCancelPull = true }
        defer { Task { @MainActor in self.canCancelPull = false } }
        // existing hostController.send("pull-model ...")
    }
}

func cancelPull() {
    activePullTask?.cancel()
    Task { try? await hostController.sendOneShot("cancel-pull") }
}
```

`main.swift` — Cancel button visible only when `canCancelPull`
is true. Existing drive-erase confirm button is the visual
reference for danger-styling.

**Mac sidecar:**
```csharp
// HostLifetime.cs (sketch)
private static CancellationTokenSource? _activePullCts;
private static readonly object _pullCtsLock = new();

case "cancel-pull":
    CancellationTokenSource? toCancel;
    lock (_pullCtsLock) { toCancel = _activePullCts; }
    toCancel?.Cancel();
    return WriteResult(new { ok = true });

// inside the existing pull-model arm:
var ourCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
lock (_pullCtsLock) { _activePullCts = ourCts; }
try { await _modelService.PullModelAsync(..., ourCts.Token); }
finally {
    lock (_pullCtsLock) { if (_activePullCts == ourCts) _activePullCts = null; }
    ourCts.Dispose();
}
```

**Windows side:**
```csharp
// shared/ViewModels/PrepViewModel.cs
private CancellationTokenSource? _pullCts;
public ICommand CancelPullCommand => new RelayCommand(_ => _pullCts?.Cancel());

private async Task RunPullAsync(...)
{
    _pullCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    try { await _modelService.PullModelAsync(..., _pullCts.Token); }
    finally { _pullCts.Dispose(); _pullCts = null; }
}
```

XAML: bind a Cancel button to `CancelPullCommand`; gate
visibility on `IsPulling`.

**Partial-blob handling on cancel:** **leave them on disk.**
Ollama's resume logic re-validates `*-partial-*` on the next
pull, so cancelled partials are cached progress, not corruption.
**Do not** add cleanup logic — that would defeat sub-bug (c)'s
fix.

### Sub-bug (b) — Single progress line

`prep-core/OllamaServerHandle.cs` `ConsumeAsync` signature
extension:

```csharp
internal static async Task ConsumeAsync(
    StreamReader reader,
    Action<string> onLog,
    string streamLabel,
    Action<string>? onProgress = null,
    CancellationToken ct = default)
{
    while (!reader.EndOfStream && !ct.IsCancellationRequested)
    {
        var raw = await reader.ReadLineAsync().ConfigureAwait(false);
        if (raw is null) break;
        var cleaned = StripAnsiCursorRewrites(raw);
        if (string.IsNullOrWhiteSpace(cleaned)) continue;

        if (onProgress is not null && IsOllamaProgressLine(cleaned))
        {
            try { onProgress(cleaned); } catch { /* never abort drain */ }
            continue;
        }
        try { onLog($"[ollama serve {streamLabel}] {cleaned}"); }
        catch { /* never abort drain */ }
    }
}
```

`StripAnsiCursorRewrites`: regex `\x1b\[[?]?[0-9;]*[A-Za-z]`
(captures CSI sequences including the `?`-prefixed private modes
Ollama uses). Apply repeatedly until idempotent.

`IsOllamaProgressLine`: regex
`^pulling [a-f0-9]{12,}\.\.\.\s+\d+%(\s|$)`. Conservative —
falls through to `onLog` on shape drift so Ollama format changes
fail open to verbose logging rather than silent loss.

Existing callers (Network Mode lifecycle, etc.) keep working with
`onProgress = null`; the progress branch is dead code for them.

PrepApp pull path constructs the `OllamaServerHandle` with an
`onProgress` lambda that writes to `_progressLine` /
`PullProgressLine`. UI binds to that single observable.

### Sub-bug (c) — Resume seeding

`prep-core/Services/ModelOperations.cs` (or a new
`PartialDownloadProbe.cs` if `ModelOperations` is already heavy
— surface at kickoff):

```csharp
public static double EstimatePartialProgress(string modelsRoot, string modelTag)
{
    var manifest = TryLoadManifest(modelsRoot, modelTag);
    if (manifest is null) return 0.0;

    var totalBytes = manifest.Layers.Sum(l => l.SizeBytes);
    if (totalBytes <= 0) return 0.0;

    var blobsDir = Path.Combine(modelsRoot, "blobs");
    if (!Directory.Exists(blobsDir)) return 0.0;

    long downloaded = 0;
    foreach (var layer in manifest.Layers)
    {
        var fullBlob = Path.Combine(blobsDir, "sha256-" + layer.Digest);
        if (File.Exists(fullBlob)) { downloaded += layer.SizeBytes; continue; }

        // partial-suffix patterns Ollama uses
        var partials = Directory.EnumerateFiles(blobsDir, $"sha256-{layer.Digest}-partial-*")
            .ToList();
        if (partials.Count > 0)
            downloaded += partials.Sum(p => new FileInfo(p).Length);
    }

    return Math.Clamp((double)downloaded / totalBytes, 0.0, 1.0);
}
```

Both pull paths call it pre-spawn:
- Windows: `shared/ViewModels/PrepViewModel.cs` pull invocation.
- Mac sidecar: `HostLifetime.cs` `pull-model` arm.

The seed value is sent to the PrepApp UI as the initial
`PullProgressLine` ("Resuming from 43%...") and overwritten by
the first live `onProgress` line from the new
`ConsumeAsync` channel.

**Manifest format:** Ollama writes per-tag manifests to
`<modelsRoot>/manifests/registry.ollama.ai/library/<model>/<tag>`.
JSON with a `layers[]` array each carrying `digest` (sha256) and
`size`. Confirm at kickoff against a real prepped SSD's
`models/manifests/...` layout (the v1.3.11 field-test SSD has
`llama3.2/1b` to read).

## Tests

### Extend `tests/OllamaServerHandleConsumeTests.cs`

- `ConsumeAsync_StripsAnsiCursorRewrites_FromProgressLines` —
  feed a captured `\x1b[?25l\x1b[2K\rpulling abc123def456... 43%\x1b[?25h`
  string; assert `onProgress` receives the clean form, `onLog`
  is not called for that line.
- `ConsumeAsync_CoalescesProgressLines_ToOnProgressChannel` —
  feed five consecutive `pulling <hash>... NN%` ticks; assert
  `onProgress` invoked five times, `onLog` not called for any.
- `ConsumeAsync_NonProgressLines_StillFlowToOnLog` — feed an
  `ollama serve` startup line + a `download.go:370 part 5
  stalled` stderr-shape line; assert both reach `onLog`,
  `onProgress` not invoked.
- `ConsumeAsync_OnProgressNull_FallsBackToOnLog` — pass
  `onProgress: null`, feed a progress line; assert `onLog`
  received it (ANSI-stripped). Pins back-compat for existing
  callers.
- `ConsumeAsync_ThrowingOnProgress_DoesNotAbortDrain` — pass an
  `onProgress` that throws; feed a progress line followed by a
  log line; assert the log line still arrives.

### New `tests/ModelOperationsPartialProgressTests.cs` (or extend an existing ModelOperations test file)

- `EstimatePartialProgress_NoManifest_ReturnsZero` — empty
  models tree.
- `EstimatePartialProgress_OnlyFullBlob_ReturnsOne` — synthesize
  a manifest with one layer, drop the full `sha256-<hex>` blob
  matching the manifest digest, no partials. Assert `1.0`.
- `EstimatePartialProgress_OnlyPartial_ReturnsFraction` —
  manifest with one 1000-byte layer, drop a 430-byte
  `sha256-<hex>-partial-0` file. Assert `0.43` (within float
  tolerance).
- `EstimatePartialProgress_MixedFullAndPartial_SumsCorrectly` —
  two-layer manifest, layer 1 fully downloaded, layer 2 at 50%
  partial. Assert weighted fraction.
- `EstimatePartialProgress_ManifestMalformed_ReturnsZero` —
  corrupt manifest JSON; assert no throw, returns `0.0`.
- `EstimatePartialProgress_BlobsDirMissing_ReturnsZero` —
  manifest exists but `models/blobs/` not yet created. Assert
  `0.0`.

### Sidecar smoke (extend `tests/MacPrepHostSmokeTests.cs` or sibling)

- `HostRunner_CancelPull_ReturnsOkAndSignalsToken` — drive the
  `cancel-pull` arm via `HostRunner.RunAsync`; mock pull arm
  registers a CTS, `cancel-pull` fires, assert the CTS was
  cancelled. Pins the cross-arm token-handoff plumbing.
- `HostRunner_CancelPull_NoActivePull_ReturnsOk` — calling
  `cancel-pull` with no active pull is a no-op success (idempotent).

### What NOT to test

- Don't drive a real `ollama pull` end-to-end in unit tests —
  network-dependent and slow. The sub-bug (a) cancel path is
  covered by token-signal tests; sub-bug (b) by ConsumeAsync
  unit tests against captured TUI strings; sub-bug (c) by the
  partial-progress synthesis tests.
- Don't snapshot SwiftUI views. Manual smoke covers the Mac UI
  binding.
- Don't add new mac-runner tests — Runner doesn't pull.

## Security

MAC31 is a UX-layer fix; no new attack surface. Confirm during
execution:
- **No new process launches.** All process work flows through
  the existing `ProcessRunner.ArgumentList` paths in
  `OllamaServerHandle` / `ModelOperations`. The `cancel-pull`
  sidecar arm signals an existing token; it does not spawn.
- **No `PathGuards` regressions.** `EstimatePartialProgress`
  uses `Path.Combine` against a vetted `modelsRoot` argument
  but does not accept user-controlled segments — guard against
  `modelTag` containing `..` by validating against an allowlist
  pattern (`^[a-z0-9._-]+(:[a-z0-9._-]+)?$`) before using it
  in path construction.
- **MAC5 plaintext invariant.** No config writes added on any
  path. Confirm at PR end.
- **Encrypted-config format.** No schema changes.
  `ModelEntry` untouched. Cross-language fixture under
  `tests/Fixtures/MacEncryptedConfig/` stays untouched.
- **URL allowlist unchanged.** No new download URLs introduced.

## Out of scope

- **Architectural fix for the Mac sidecar's lack of writeback
  during pull.** Re-deriving the encryption key per pull (so
  `config.Models` could be updated mid-flow) is the long-term
  fix that would let MAC29's readiness check trust config
  truth, but MAC33 already routed all model-state reads to
  disk-truth, so this rebuild is not load-bearing for MAC31.
- **MAC30 encryption-optional toggle.** Independent.
- **MAC32 Finish button no-op.** Separate ticket; needs product
  call.
- **Concurrent pulls.** PrepApp is one-pull-at-a-time today; a
  registry of pull CTSs keyed by tag is unnecessary complexity.
  If MAC31's CancellationToken plumbing collides with a future
  parallel-pull feature, that future PR can promote
  `_activePullCts` to a dictionary.
- **Progress UI for things other than pull.** Stage-prereqs and
  ollama-stage have their own log surfaces; MAC31 only touches
  the model-pull progress flow.

## Do not change

- **`OllamaServerHandle.ConsumeAsync` back-compat for existing
  callers.** New `onProgress` parameter must default to `null`
  so MAC28 tests + Network Mode lifecycle build unchanged.
- **Existing pull invariants.** Pull still respects MAC28's
  health-poll budget, MAC27's temp-server lifecycle, MAC25's
  resolver, MAC26's inner-Ollama path. No regressions in those
  layers.
- **Encrypted-config + MAC5 plaintext invariants.** No config
  writes added.
- **`ProcessRunner.ArgumentList`, `PathGuards`, URL allowlist
  guardrails.** None of these are touched.

## CI workflow

Branch: `kninetimmy/mac31-pull-ux`.

Local validation (Windows-only — user's Mac doesn't have
`dotnet`):
- `dotnet build FreeAiSsd.sln -c Release`
- `dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal`
- Run new test classes specifically:
  `dotnet test tests/FreeAiSsd.Tests.csproj --filter "FullyQualifiedName~OllamaServerHandleConsumeTests"`
  `dotnet test tests/FreeAiSsd.Tests.csproj --filter "FullyQualifiedName~ModelOperationsPartialProgressTests"`
  `dotnet test tests/FreeAiSsd.Tests.csproj --filter "FullyQualifiedName~MacPrepHostSmokeTests"`

CI required jobs (all green before merge):
- `windows-build` — restore / build / test (exercises new
  ConsumeAsync + EstimatePartialProgress + sidecar tests) /
  WPF guardrails / publish.
- `mac-runner-build` — Swift unit tests, Mac host publish, Mac
  host smoke. Unaffected by MAC31 (Runner doesn't pull) but
  required.
- `mac-prep-build` — exercises the new Swift PrepViewModel
  state + main.swift Cancel button via Swift compile.
- `package-release` — skipped on PR.

Expect green on first or second run. Most likely failure mode:
the ANSI regex matches more than intended on a Windows
terminal-output edge case — fix forward by tightening the
pattern.

## Post-merge

- Update `agent_docs/project_state.md`:
  - Move PR entry into `Recently shipped` (top, push the
    oldest entry below the cap).
  - Bump `Last updated`.
  - Reorder `Next up` so MAC32 (Finish button product call) is
    #1 and MAC30 (encryption-optional) is #2; F2a / MAC20 / X18
    / MAC11 / cleanup follow on.
- Append `agent_docs/mac_project_backlog.md` MAC31 status to
  **done** with PR reference + commit SHA.
- Capture any cross-OS execution decision deviations in
  `project_decisions.md` (e.g., if the Cancel button shape
  diverged between Mac and Windows for a defensible reason).
- Dispatch `gh workflow run build.yml --ref main -f
  version=1.3.12 -f include_macos=true` once the user confirms.
- **Manual smoke deferred to a real Mac + an external SSD + a
  slow connection:**
  - Prep a fresh SSD, start a `llama3.1:8b` pull (or any model
    big enough that a slow connection takes >2 min).
  - Confirm progress shows as a single in-place line that
    monotonically climbs.
  - Mid-pull, click Cancel. Confirm:
    - Process exits cleanly (no zombies in Activity Monitor).
    - `<ssd>/models/blobs/sha256-*-partial-*` files remain on
      disk.
    - UI returns to a Retry-ready state.
  - Click Retry. Confirm:
    - Initial progress line shows the resumed fraction (e.g.
      "Resuming from 43%...") rather than 0%.
    - Pull continues from approximately where it stopped.
  - Cross-OS roundtrip: take that mid-pull SSD to a Windows
    machine, run Windows PrepApp, complete the pull. Confirm
    the partial blobs Ollama wrote on Mac were re-validated
    and the pull did not redownload from 0%.

## Open questions to resolve at execution start

1. **`ModelOperations.PullModelAsync` `ct.Register` neighborhood.**
   MAC28 work referenced "around line 340" but the file may have
   shifted. Confirm exact line via grep + read the existing
   process-tree-kill registration; the new `cancel-pull` flow
   relies on it firing correctly on token cancellation.
2. **Existing `ConsumeAsync` callers' DI shape.** The new
   `onProgress` parameter must default to null without breaking
   `internal static` reflection tests. Read
   `OllamaServerHandleConsumeTests.cs` and confirm; if the test
   harness uses positional args that would shift, prefer a
   sibling helper method over an in-place signature change.
3. **PrepApp progress UI binding location.** The Mac side's
   pulling step lives in `EncryptionSetupStepView` /
   `ModelsStepView` (confirm at kickoff which step actually
   shows the pull progress). Windows equivalent in
   `MainWindow.xaml` — confirm which step element shows the
   pull progress today and add the Cancel button + single
   `TextBlock` next to it.
4. **Manifest path layout on real prepped SSDs.** The v1.3.11
   field-test SSD should have
   `models/manifests/registry.ollama.ai/library/llama3.2/1b`
   already. Confirm by reading
   `prep-core/Services/ModelOperations.DiscoverModelsOnDisk` (the
   MAC29 helper) for the manifest-walk shape and reuse the same
   path-construction logic in `EstimatePartialProgress` for
   consistency. **Do not duplicate** the walk logic — extract a
   shared `ResolveManifestPath(modelsRoot, modelTag)` helper if
   `DiscoverModelsOnDisk` does the walk inline.
5. **Cancel-during-stage-prereqs vs cancel-during-pull.** MAC31
   scope is pull only, but if the user clicks Cancel during
   stage-prereqs, the existing flow already swallows it.
   Confirm at kickoff that the new Cancel button is **only
   visible during pull** so the user can't trigger it during
   non-cancellable stages.
