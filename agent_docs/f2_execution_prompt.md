# F2 — Live model list fetch (cross-platform PrepApp)

**Branch:** `kninetimmy/f2-live-model-catalog`
**Backlog item:** `agent_docs/project_backlog.md` → F2 (line ~238)
**Status entering this prompt:** triaged, one-shot for v1 per backlog. Cross-platform scope added at prompt time (MAC17/17a/17b shipped; PrepApp is dual-host).

## Goal

Add a **Refresh Catalog** affordance to PrepApp that fetches the live
Ollama model library and falls back to the embedded
`prep-core/Resources/starter-models.json` on any failure. The user
manually triggers a refresh; the result replaces the in-memory catalog
for the current session and surfaces a "last updated" timestamp.

Cross-platform from the start: the fetch service lives in `prep-core/`
(already cross-platform, plain net8.0), Windows PrepApp wires it via
`PrepViewModel`'s WPF binding, and Mac PrepApp wires it via a new
`refresh-catalog` stdin command on `mac-prep-host` consumed by the
SwiftUI step views.

## Why now

`prep-core/Resources/starter-models.json` is static and was last
hand-curated some time ago. New / better models (e.g. recent Llama,
Phi, Qwen versions) don't appear without a release. The user wants
the option to refresh on demand without shipping a new build.

## Cross-OS parity decision

**Bundle Windows + Mac in one PR by default.** The shared service
HAS to land in `prep-core/` first, and the per-host UI wiring is
small on both sides:
- Windows: one new command on `PrepViewModel`, one button + caption
  in `MainWindow.xaml`.
- Mac: one new arm in `mac-prep-host/HostLifetime.cs`, one Swift
  method + button in `mac-prep-app/Sources/`.

Pattern follows MAC17b (PR #198) — single PR with sidecar command +
Swift call-site swap, exercised by an in-process `HostRunner` test
on Windows CI.

**Split fallback (F2/F2a) if any of these surface during execution:**
- The Mac UI surface needs a non-trivial step-view restructure (more
  than ~30 lines of Swift). Then F2 ships Windows + the prep-core
  service, and F2a ships the Mac UI in a follow-up.
- The Mac sidecar command needs a new payload shape that doesn't fit
  the existing `result: <command> <json>` protocol. Then split.
- Strict-concurrency / `@unchecked Sendable` needs come up that
  weren't already paid down in MAC17a. Then split.

If you split, both PRs land in this session window — F2a is not a
"some day" item.

## Architecture

### New: `prep-core/Services/LiveModelCatalogService.cs`

Plain net8.0. Public surface:

```csharp
public interface ILiveModelCatalogService
{
    Task<LiveCatalogResult> FetchAsync(CancellationToken ct);
}

public sealed record LiveCatalogResult(
    IReadOnlyList<StarterModelCatalogEntry> Entries,
    DateTimeOffset FetchedAt,
    string SourceUrl);
```

Implementation:
- `HttpClient` (injected — testable via `HttpMessageHandler`)
- URL allowlist enforced before request: only HTTPS to allowlisted
  hosts. **Allowlist constant lives next to the service**, mirrors
  the `OllamaPackageTrustPolicy` pattern.
- Timeout: 10 seconds (constant, not config-driven for v1).
- JSON parse via `System.Text.Json` (already in use everywhere).
- Output `StarterModelCatalogEntry` shape **must match** the existing
  static loader's shape — no schema drift; UI consumes one type.
- Failure modes (network, timeout, parse, HTTP non-2xx) → throw a
  typed `LiveCatalogFetchException` with the underlying reason. The
  caller decides whether to fall back.

### Source choice — design moment, surface before coding

**Backlog assumes Ollama-first.** Verify before building:
- Does `ollama.com/library` expose a JSON list endpoint? Last I checked
  (cutoff Jan 2026) the public surface was HTML; the registry V2 API
  at `registry.ollama.ai/v2/library/<model>/tags/list` requires
  knowing model names up front.
- If no clean list API exists, options are:
  - **(a)** Curate a list of "popular" model slugs in code, hit the V2
    tags endpoint per-slug. Live data on tags + sizes, but the list of
    *which* models is still semi-static.
  - **(b)** Scrape `ollama.com/library` HTML. Fragile; rejected unless
    no other path exists.
  - **(c)** Skip Ollama entirely for v1, use HuggingFace's
    `https://huggingface.co/api/models?filter=gguf&sort=downloads`
    real API. Heavier filtering needed but real list semantics.

**First task in execution: spend ~15 min validating Ollama's actual
public API surface.** Pick the simplest viable path. Document the
choice in the PR body and add a one-line decision to
`project_decisions.md` with the exit ramp ("if Ollama ships a list API
later, swap to it").

### Windows wiring

- `shared/ViewModels/PrepViewModel.cs`:
  - Inject `ILiveModelCatalogService` via existing DI surface in
    `prep-app/App.xaml.cs`.
  - New `RefreshCatalogCommand` (async). On success, swap the in-memory
    catalog used by the model grid; update `LastCatalogUpdate`
    timestamp property; toast/log "Refreshed N models from <source>".
  - On failure, log the typed exception, leave the existing catalog in
    place, surface "Refresh failed — using bundled list" to the log
    panel.
- `prep-app/MainWindow.xaml`:
  - "Refresh Model List" button near the existing model grid header.
  - Small caption "Last updated: <timestamp> (bundled)" or
    "Last updated: <timestamp> (live)" next to it.
  - Disabled while refresh is in flight (existing busy-state pattern).

### Mac wiring

- `mac-prep-host/HostLifetime.cs`:
  - New `case "refresh-catalog"` arm. Calls
    `ILiveModelCatalogService.FetchAsync(ct)` against the same DI as
    Windows. Returns `result: refresh-catalog <json-payload>` where
    `<json-payload>` is `{ "fetchedAt": "...", "sourceUrl": "...",
    "entries": [...] }`. On exception, returns `result:
    refresh-catalog {"error": "<reason>"}` (don't crash the sidecar).
  - DI registration in `Program.cs` mirrors Windows.
- `mac-prep-app/Sources/PrepViewModel.swift`:
  - New `refreshCatalog()` async method calling
    `hostController.send("refresh-catalog")`. Decode the payload into
    a Swift mirror of `StarterModelCatalogEntry`. Update `@Published`
    catalog state.
  - Mirror Windows behavior: log on failure, leave bundled list in
    place.
- `mac-prep-app/Sources/<step-view>.swift`:
  - "Refresh" button in the model selection step. Disabled while refresh
    is in flight via existing pattern.
  - Last-updated caption matching Windows wording.

If the existing Mac step view doesn't have a clean spot for this
button, that's the **F2a split signal** — keep F2 to Windows + prep-core
and ship Mac in a focused follow-up.

## Tests

### `prep-core` unit tests

In `tests/`:
- `LiveModelCatalogServiceTests.cs`:
  - Happy path: mocked `HttpMessageHandler` returns curated JSON,
    service returns N entries with the right shape, `SourceUrl`
    matches.
  - URL allowlist: service refuses to hit a non-allowlisted URL
    (configured allowlist exposed via constructor for testability).
  - Timeout: handler that delays past the 10s threshold → typed
    exception.
  - HTTP non-2xx: handler returns 500 → typed exception with status
    code surfaced.
  - Malformed JSON: typed exception, original parse error in
    `InnerException`.
  - Schema drift safety: a known field missing from the JSON throws
    a clear "schema mismatch" exception rather than silently
    deserializing nulls.

### Cross-language test

- `MacPrepHostSmokeTests.cs`:
  - New `HostRunner_RefreshCatalog_ReturnsBundledFallback`:
    register a fake `ILiveModelCatalogService` that always throws,
    drive `refresh-catalog` over `HostRunner.RunAsync` (in-process,
    Windows CI), assert the returned payload includes the
    `"error"` key. Pins the sidecar protocol.
  - Optional: `HostRunner_RefreshCatalog_HappyPath` with a fake
    service returning two entries, assert the Mac sidecar surfaces
    them in the result payload.

### What NOT to test

- Real-network calls in CI (would be flaky; the prep-core unit
  tests cover the service contract).
- Mac-side Swift parsing of the result payload — covered by
  manual smoke; an automated test would need a Swift JSON fixture
  that drifts from the C# shape.

## Security

- URL allowlist for outbound HTTP. **Constant in code**, not
  config-driven for v1 — adding new sources is a code change and
  a PR review.
- HTTPS only. Reject `http://` URLs at allowlist time, not at
  request time.
- Reasonable timeout (10s).
- No process exec — pure fetch + parse.
- No new package dependencies. `HttpClient` is built-in;
  `System.Text.Json` is already used everywhere.
- The fetched JSON is **data, not code** — never deserialize into
  types with constructor side effects, never use `JsonSerializer`
  with `TypeInfoResolver` that allows arbitrary types.
- Document the new outbound-HTTP surface in
  `project_decisions.md` so future security-review passes know
  why PrepApp is talking to an external host.

## Out of scope

- **HuggingFace fetch** (deferred to F2-followup if Ollama path
  works; or promoted to F2 source if Ollama doesn't have a clean
  list API — see "Source choice" above).
- **Auto-refresh on PrepApp launch.** Manual button only. Keeps
  the network-surface boundary explicit and respects the offline
  posture.
- **Catalog persistence to disk.** In-memory only for the current
  session. Next session uses the bundled catalog until refreshed
  again.
- **Per-model curation / quality filtering.** Whatever the source
  returns is what we surface, sorted by whatever the source ranks
  by. v1 trusts the upstream ordering.
- **Embedding the catalog into the encrypted config.** Not relevant
  — catalog is non-secret reference data.
- **Companion / Runner integration.** F2 is PrepApp-only.

## Do not change

- `StarterModelCatalogEntry` schema. The point of this work is to
  populate it with live data; if the schema is wrong, that's a
  separate item.
- The fallback path through `StarterModelCatalogLoader` →
  embedded resource → file (MAC16's chain). Live fetch becomes a
  fourth fall-through layer at the top, not a replacement.
- Encrypted-config format / scheme name / cross-language fixture.
- MAC5 plaintext invariant — the Mac sidecar still receives
  PortableConfig over stdin only; the catalog is not config.
- `DiskutilFormatCommand` parity-pin.
- `ProcessRunner.ArgumentList` invariant on any process launch
  (irrelevant here — F2 launches no processes).

## CI workflow

Standard:
- `windows-build` runs the new `LiveModelCatalogServiceTests` and
  the new `MacPrepHostSmokeTests` arm.
- `mac-runner-build` builds the Mac runner (no F2 changes there;
  should be noop).
- `mac-prep-build` builds the Mac PrepApp with the new sidecar
  command and Swift refresh path. Run swiftc strict-concurrency
  warnings clean — MAC17a's `[weak self]` → `let weakSelf = self`
  pattern applies if any new closures land in `PrepHostController`
  call sites.
- `package-release` skipped (no release tag).

If `mac-prep-build` fails strict-concurrency on first run, the fix-
forward pattern from MAC17a commit `8c234b1` is the playbook.

## Post-merge

- Wrap-up PR (docs-only) following the MAC17b → PR #199 / MAC18 →
  pending-wrap-up pattern: move F2 entry to "Recently shipped",
  bump `Last updated`, and append the source-choice decision entry
  to `project_decisions.md` if not already done in the feature PR.
- Update `agent_docs/project_backlog.md` F2 status to done.
- If Mac side was split as F2a, **do not** mark F2 done until F2a
  also merges — the parity rule says cross-OS work tracks together.

## Open questions to resolve at execution start

1. Ollama list-API verification (see "Source choice"). Resolve
   first, document choice in PR body.
2. Where exactly the Refresh button lives in the Mac PrepApp step
   sequence — depends on the current `mac-prep-app/Sources/` step
   view layout. Read the existing step views first.
3. Whether to surface the source name ("Ollama" / "HuggingFace")
   in the timestamp caption or just generically "live". Lean
   toward naming the source — useful for debugging.
