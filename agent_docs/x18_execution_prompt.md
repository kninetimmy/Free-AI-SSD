# X18 — Ingest observability (cross-platform)

Execution prompt for the first item in the v1.3.x RAG audit
sequence: surface ingest outcomes (failed chunks, parse failures,
file rejections, textless-page count) to the user instead of
swallowing them into log lines, plus a configurable failure
threshold.

Backlog entry: `agent_docs/project_backlog.md` → "X18 — Ingest
observability". Audit rationale lives in
`agent_docs/project_decisions.md` (2026-04-19 RAG audit entries).

## Goal

After every ingest (Add Files / Sweep / Rebuild on Windows; the
equivalent NDJSON-driven flow on Mac), the user sees a structured
summary of what happened: files imported, files skipped with
reasons, parse failures with reasons, embedding chunks failed,
textless-page count (X17 hook — field stays in the schema even if
detection isn't wired yet), and whether the run aborted at the
embedding-failure threshold. Threshold itself becomes configurable
via `PortableConfig` instead of a hard-coded `0.50d` constant.

Today: `DocumentIngestor` populates `IndexingProgress.FailedChunks`
(`shared/Documents/DocumentIngestor.cs:202`) but
`runner/MainWindow.xaml.cs:1018-1095` reads only `CompletedFiles` /
`TotalFiles` / `CurrentFile` for a single-line "Indexing N/M:
file" status. Parse failures caught at
`shared/Documents/DocumentIngestor.cs:104` are logged + swallowed.
Threshold abort message at `:206-210` becomes an exception the UI
turns into "Indexing failed. Missing embedding model?" — wrong
attribution, wrong remediation. Mac side: NDJSON `progress` frames
are emitted by `RunnerLocalApiService` but Mac
`handleNdjsonProgress` (`mac-runner/Sources/main.swift:889`)
ignores them and only reads `file-rejected` / `complete` / `error`
— so even rejected counts surface only as `(N rejected — see
logs)`, with no chunk-level detail.

## Why now

First item in the v1.3.x RAG audit sequence (X18 → X15 → X19 → X20
→ X22 → X23) per `agent_docs/project_decisions.md` (2026-04-19 RAG
audit triage). Comes first because it's the smallest, gives us the
observability we'll need to validate every downstream RAG change
against real ingests (you can't tell whether a streaming-pipeline
or hybrid-retrieval change improved or regressed quality if the
baseline failures aren't visible), and it's a clean test of the
strengthened cross-OS rule on a runner-core change with two host
UI surfaces.

## Dual-OS review pass

**Windows surfaces:**
- `runner/MainWindow.xaml(.cs)` — three call sites
  (`AddFiles_Click`, `SweepFolders_Click`, `RebuildIndex_Click`)
  consume `Action<IndexingProgress>` callbacks today; need to
  consume the new `IngestResult` returned by
  `IDocumentOperationsService.IngestFilesAsync` /
  `SweepFoldersAsync` / `RebuildIndexAsync`.
- New compact summary surface in `MainWindow.xaml`. Two acceptable
  shapes — pick at execution start (see "Open questions"):
  inline summary panel below the existing `IndexingStatusText`,
  or a modal popup that the user dismisses. Either way, the
  summary uses the existing log-style colors (no new theme work).

**Mac surfaces:**
- `mac-runner/Sources/main.swift` — `handleNdjsonProgress`
  switch (`mac-runner/Sources/main.swift:889`) gains a new
  `case "summary"` arm. Existing `libraryStatus` string gains a
  matching one-line summary; new `@Published lastIngestSummary:
  IngestSummary?` powers a small disclosure section in the
  Documents view (or expands the existing `libraryStatus` line —
  pick at kickoff, but lean toward minimal SwiftUI surface to keep
  this PR focused).
- New Codable `IngestSummary` mirror type in
  `mac-runner/Sources/IngestSummaryTypes.swift` (new file, follows
  the `StarterCatalogTypes.swift` pattern from F2).

**Shared-core surfaces:**
- `shared/Documents/DocumentIngestor.cs` — return new
  `IngestResult` from `IngestFilesAsync` / `SweepFoldersAsync` /
  `RebuildIndexAsync`. Aggregate parse failures + the throw-at-
  threshold path into the result rather than throwing /
  swallowing.
- `shared/Documents/DocumentModels.cs` — new `IngestResult`,
  `IngestFileOutcome`, `IngestFailureReason` types.
- `shared/PortableConfig.cs` — new `MaxEmbeddingFailureRatio`
  field, default 0.50d (matches today's hard-coded constant; back-
  compat-safe).
- `runner-core/Services/IDocumentOperationsService.cs` +
  `runner-core/Services/DocumentOperationsService.cs` — return
  type changes from `Task` to `Task<IngestResult>` on the three
  ingest methods.
- `runner-core/Services/RunnerLocalApiService.cs` — emit a new
  NDJSON frame `{type: "summary", summary: {...}}` between the
  last `progress` and the `complete` frame. Mac consumes this.

**Non-surfaces (one-line justification):**
- `companion/` — has no ingest UI (chat + voice only; checked
  2026-05-07). No mirror work needed.
- `runner-cli/` — chat REPL only. R1 follow-up adds `/docs` /
  `/reindex` slash-commands but is not in scope for X18; if those
  ship later they can pick up the new return type without
  refactoring.
- `prep-app/` (Windows PrepApp) — does not ingest documents; only
  stages drives. No surface.
- `mac-prep-app/` — same. No surface.

**Decision:** **Bundle both OSes in one PR.** Shared-core change
is the load-bearing piece; both host UI wirings are small (~25-30
lines each). Matches the bundle-criteria from
`project_decisions.md` (2026-05-07 cross-OS rule). If the Mac
SwiftUI surface balloons past ~40 lines or hits a SwiftUI 6 strict-
concurrency design question that needs its own review, split into
**X18a** (shared-core + Windows + NDJSON `summary` frame) and
**X18b** (Mac UI surface) — file X18b before merging X18a. Default
remains bundle.

## Architecture

### New shared types in `shared/Documents/DocumentModels.cs`

```csharp
public sealed class IngestResult
{
    public int FilesAttempted { get; set; }
    public int FilesImported { get; set; }
    public List<IngestFileOutcome> Skipped { get; set; } = new();
    public List<IngestFileOutcome> ParseFailed { get; set; } = new();
    public int TotalChunksAttempted { get; set; }
    public int ChunksFailed { get; set; }
    public int TextlessPagesDetected { get; set; } // X17 hook; 0 until X17 lands
    public bool AbortedAtThreshold { get; set; }
    public string? AbortMessage { get; set; }
}

public sealed class IngestFileOutcome
{
    public string FileName { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
```

`IngestFileOutcome` is reused for both `Skipped` (rejected at the
file-level boundary, e.g., unsupported extension, oversize) and
`ParseFailed` (got past the gate, parser threw — currently
swallowed at `DocumentIngestor.cs:104`). The two lists stay
separate because the user-facing remediation differs: Skipped
reasons are usually configuration (file too large) while
ParseFailed is usually content corruption.

`TextlessPagesDetected` is the X17 hook. Field stays at zero in
this PR; X17 (textless-page diagnostic) will populate it without
schema changes.

### `DocumentIngestor` changes

`IngestFilesAsync` / `SweepFoldersAsync` / `RebuildIndexAsync`
return `Task<IngestResult>`. Internals:
- Parse failures (`InvalidOperationException` catch at
  `DocumentIngestor.cs:104`) push into
  `result.ParseFailed.Add(new IngestFileOutcome { FileName =
  Path.GetFileName(sourcePath), Reason = ex.Message })` instead of
  the current log-and-continue.
- Embedding-failure threshold abort (`DocumentIngestor.cs:206-210`)
  flips `result.AbortedAtThreshold = true` and stores the message
  in `result.AbortMessage`, then **still throws** so the existing
  fail-fast contract is preserved at the API boundary; the API
  layer catches and emits the summary frame with the populated
  fields. Don't change the throw-vs-return shape silently — the
  three call sites (Windows direct, runner-core API, future
  runner-cli) all expect exception-on-abort today. Result is
  populated for diagnostic use.
- Threshold itself reads from
  `config.MaxEmbeddingFailureRatio` (new field) instead of the
  `MaxEmbeddingFailureRatioBeforeAbort` private const. Keep the
  const as the default value source so existing tests keep
  working.

### `PortableConfig` change

```csharp
public double MaxEmbeddingFailureRatio { get; set; } = 0.50d;
```

Schema migration: existing v1.2.x configs without the field load
with the default 0.50, identical to today's behavior. No version
bump needed; this is additive on a JSON object.

### NDJSON protocol — `summary` frame

`RunnerLocalApiService.HandleProgressedOpAsync` and the upload
handler emit one new frame between the final `progress` and the
`complete`:

```json
{"type":"summary","summary":{
  "filesAttempted":5,
  "filesImported":4,
  "skipped":[{"fileName":"big.pdf","reason":"File exceeds 50 MB limit"}],
  "parseFailed":[],
  "totalChunksAttempted":42,
  "chunksFailed":3,
  "textlessPagesDetected":0,
  "abortedAtThreshold":false,
  "abortMessage":null
}}
```

Frame ordering: `start` → 0..N `progress` → 0..N `file-rejected`
(only on uploads — keep existing emission for back-compat; sweep
/rebuild emit `summary.skipped` instead) → `summary` → `complete`
or `error`. Mac decoder must tolerate the existing `file-rejected`
frame (uploads still emit it; X18 doesn't consolidate that).

### Windows UI surface

`MainWindow.xaml` gains a small `<TextBlock>` (or
`<ItemsControl>` for multi-line) below `IndexingStatusText` named
`IngestSummaryText`. Hidden when `Visibility=Collapsed` between
ingests. Populated from the returned `IngestResult` after each of
the three ingest call sites. Format:

```
4 of 5 files imported.
1 skipped: big.pdf — File exceeds 50 MB limit
3 chunks failed to embed.
```

Or, if the run aborted: prepend `Aborted: ` and the abort message
verbatim. No new colors / icons in this PR — keep the surface
boring. Replace today's `"Indexing failed. Missing embedding
model?"` catch-handler text in `MainWindow.xaml.cs:1027` with the
abort message verbatim when available; fall back to `ex.Message`
otherwise.

### Mac UI surface

`mac-runner/Sources/IngestSummaryTypes.swift` (new file): Codable
`IngestSummary` and `IngestFileOutcome` mirroring the C# shape.

`PrepViewModel`-equivalent in `main.swift` (the existing
`@MainActor class AppViewModel`): new `@Published var
lastIngestSummary: IngestSummary? = nil`. `handleNdjsonProgress`
gains:

```swift
case "summary":
    if let summaryObj = frame["summary"] as? [String: Any],
       let data = try? JSONSerialization.data(withJSONObject: summaryObj),
       let parsed = try? JSONDecoder().decode(IngestSummary.self, from: data) {
        DispatchQueue.main.async { self.lastIngestSummary = parsed }
    }
```

Documents view gains a small disclosure section that renders the
summary when non-nil — same field order as Windows. Matches
brand-tinted-native posture from MAC17 (no custom chrome).

## Tests

### Unit tests in `tests/DocumentIngestorFailureHandlingTests.cs`

Existing tests continue to pass (assert exception on threshold
abort). Add:
- `IngestFilesAsync_ReturnsResult_WithFilesImportedAndSkipped` —
  feeds a mix of valid + oversize + unsupported-extension files,
  asserts `result.FilesImported`, `result.Skipped` populated with
  filenames and reasons.
- `IngestFilesAsync_ParseFailure_PopulatesParseFailedList` —
  injects a parser that throws `InvalidOperationException`,
  asserts `result.ParseFailed[0].FileName` + `.Reason` and that
  the surrounding files still ingest.
- `IngestFilesAsync_ThresholdAbort_StillThrows_ButResultPopulated`
  — keeps the throw contract; catches at the test boundary and
  asserts that any side-effect-collected result captured during
  the run reflects `AbortedAtThreshold`. (If this proves awkward
  because the result is internal-to-throw, expose
  `result.AbortMessage` via a new throw type instead — surface
  the choice at execution start.)
- `IngestFilesAsync_RespectsConfigThreshold` — sets
  `config.MaxEmbeddingFailureRatio = 0.10d`, verifies abort
  triggers earlier than the 0.50 default.

### API-level test in `tests/RunnerLocalApiLibraryTests.cs`

- `Sweep_EmitsSummaryFrame_BetweenProgressAndComplete` — feeds an
  ingest with mixed outcomes through the live local API, asserts
  the NDJSON sequence contains exactly one `summary` frame
  positioned after the last `progress` and before `complete`,
  with the expected fields.
- `Upload_KeepsFileRejectedFrames_AlongsideSummary` — pin the
  back-compat: upload path still emits per-file `file-rejected`
  frames, AND the `summary.skipped` list contains the same names.

### Mac sidecar test in `tests/MacRunnerHostLibraryTests.cs`

- `MacRunnerHost_Sweep_EmitsSummaryFrame` — runs the same scenario
  through `mac-runner-host` DI on Windows CI; the test asserts the
  C# side emits the frame. Swift-side rendering is not unit-tested
  (no test runner for the SwiftUI view; covered by manual smoke).

### What NOT to test

- Don't add new `tests/Fixtures/IngestSamples/` — synthetic in-
  test fixtures are sufficient for the surface-area pinning here.
  Real public-domain PDFs are X23's job.
- Don't write SwiftUI snapshot tests; not part of the existing
  Mac CI surface.

## Security

X18 is observability — no new attack surface, no new IO, no new
process launches. Confirm during execution:
- `IngestFileOutcome.Reason` strings are user-supplied via
  parser errors. Render them in WPF as `<TextBlock Text="{...}"/>`
  (auto-escaped) and in SwiftUI as `Text(...)` (auto-escaped).
  Not a new vector but worth confirming.
- No new env vars, no new files on disk, no new endpoints.

## Out of scope

- **OCR / textless-page detection itself.** X17 owns that. The
  `TextlessPagesDetected` field stays at zero this PR — X17 will
  populate it without schema changes.
- **Streaming ingest pipeline.** X15.
- **Hybrid retrieval.** X19.
- **Section-aware chunking metadata.** X20.
- **Token-aware prompt packing.** X22.
- **Real PDF fixtures in tests.** X23.
- **Re-styling the post-ingest surface.** Compact text only this
  PR; richer panel UX can be a follow-up if field use shows the
  text version is too dense.
- **Companion / runner-cli ingest surfaces.** Companion has none;
  runner-cli's `/docs` / `/reindex` is the R1 follow-up and can
  pick up the new return shape when it lands.

## Do not change

- **Encrypted-config format.** No new encrypted-payload fields;
  `MaxEmbeddingFailureRatio` is on the plaintext-shape side of
  the split (lives in `PortableConfig`, not in
  `EncryptedSecrets`). Cross-language fixture under
  `tests/Fixtures/MacEncryptedConfig/` stays untouched.
- **MAC5 plaintext invariant.** No Mac-side config writes added
  in this PR; the new field flows in the existing plaintext
  config path.
- **NDJSON frame names already in use.** `start`, `progress`,
  `file-rejected`, `complete`, `error` keep their current
  shapes. `summary` is additive.
- **`IndexingProgress` shape.** Stays as-is so existing progress
  consumers (and any out-of-tree integrators) don't break.
- **Throw-on-threshold contract.** Don't switch to return-value-
  signaling for threshold abort in this PR — the three callers
  all expect exceptions today, and changing two contracts in one
  PR muddies review.
- **`URL allowlist`, `ProcessRunner.ArgumentList`, `PathGuards`
  guardrails.** None of these are touched. Confirm at PR end.

## CI workflow

Branch: `kninetimmy/x18-ingest-observability` (or `…-x18a-…` /
`…-x18b-…` if the bundle splits during execution).

Local validation: `dotnet build FreeAiSsd.sln -c Release` +
`dotnet test tests/FreeAiSsd.Tests.csproj` first. Mac swiftc
build is gated by CI's `mac-prep-build` / `mac-runner-build`
jobs — the user's Mac doesn't have `dotnet` installed so Mac
test pass is CI-only.

CI required jobs (must all be green):
- `windows-build` — full restore / build / test / WPF
  guardrails / publish.
- `mac-runner-build` — Swift unit tests, Mac host publish, Mac
  host smoke, Runner.app bundle.
- `mac-prep-build` — unaffected by X18 but runs as required.
- `package-release` — skipped (no release tag on PR).

Expect CI to be green on first or second run. If a CI run fails,
fix-forward on the same branch; do not skip hooks; do not amend.

## Post-merge

- Update `agent_docs/project_state.md`:
  - Move PR entry into `Recently shipped`.
  - Bump `Last updated`.
  - Reorder `Next up` so X22 (next-cheapest pure-runner-core RAG
    item) is #1, with X15 (Opus, larger) queued behind it. Keep
    MAC11 noted as parked behind Apple Dev renewal.
- Append `agent_docs/project_backlog.md` X18 status to **done**
  with PR reference.
- Capture the cross-OS execution decision (bundle vs split) in
  `project_decisions.md` only if it deviated from the planning-
  phase call here. If bundle held, no decision entry is needed —
  the prompt's planning record is the trail.
- **Manual smoke deferred** to a real Windows machine + a real
  Mac:
  - Windows: drop a mix of valid + oversize + corrupted PDFs into
    Add Files; confirm the post-ingest summary appears with
    accurate counts. Run Sweep on a folder with similar mix.
    Trigger a threshold abort by tightening
    `MaxEmbeddingFailureRatio` to 0.10 in the config and feeding
    a known-failing embedding host; confirm the abort message
    surfaces verbatim.
  - Mac: same matrix via the SwiftUI Documents view.
  - Cross-platform parity: Windows and Mac summary text matches
    field-for-field for the same input set.

## Open questions to resolve at execution start

1. **Threshold-abort signaling shape.** Current plan keeps the
   throw contract and stores the abort message in
   `IngestResult.AbortMessage` for diagnostic surfacing in the
   API summary frame. Confirm at kickoff whether it's cleaner
   to introduce a typed `IngestAbortedException` carrying the
   `IngestResult`, vs side-channeling the result via the throw.
   Bias toward the typed exception if the API layer needs both
   the message and the populated lists.
2. **Windows summary surface — inline vs popup.** Inline below
   `IndexingStatusText` is simpler and matches the existing log-
   like flow. Popup would be more attention-grabbing for the
   abort case. Default to inline; surface the choice if the WPF
   layout already implies a different convention.
3. **Mac summary surface — inline string vs disclosure section.**
   Inline keeps the SwiftUI surface tiny (just enrich
   `libraryStatus`); disclosure section gives the user a way to
   re-read the last summary after the status line moves on.
   Default to disclosure with an inline single-line preview;
   confirm at kickoff if the existing Documents view layout
   forces a different choice.
4. **Frame ordering on uploads.** Today the upload path emits
   `file-rejected` per file before any `progress`. New `summary`
   frame goes between last `progress` and `complete`, but the
   `summary.skipped` list will duplicate names also seen in
   `file-rejected`. Pin "duplication is intentional for back-
   compat; clients reading the summary should treat it as the
   canonical view; clients reading individual frames keep their
   existing behavior." Surface at kickoff if a different choice
   feels cleaner.
