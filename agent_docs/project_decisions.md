# Project Decisions

Append-only. Once written, entries are not revised. Superseding
decisions are new dated entries that reference the old one.

---

## 2026-04-17 â€” Initialized project_docs framework
- Re-bootstrapped (nuke path): backed up prior `agent_docs/` as
  `agent_docs.pre-init-backup/` and prior `CLAUDE.md` as
  `CLAUDE.md.pre-init-backup` before overwriting. Framework is now
  `CLAUDE.md` + `agent_docs/` split across state / backlog /
  decisions / arch.

---

## 2026-04-17 â€” Historical stable decisions (migrated from prior project_state.md)

These decisions were accumulated in the prior single-file
`project_state.md` under "Stable decisions (don't revisit)" and
are transcribed here verbatim as a single dated block. Future
decisions should be added as their own dated entries below.

### Profiles
- Only two profiles: **Flight Sim** and **General Assistant** â€” no custom/third profiles.
- Profile is switchable after first launch (not a one-time setup choice).
- Profile stored as `ActiveProfile` (`UserProfile` enum) on `PortableConfig` â€” no separate file.
- First-run profile dialog is **required** â€” user must choose before the app proceeds; no default assumed.
  - **Note:** F4 in the backlog proposes moving the FTUE entirely to PrepApp so Runner silently reads `ActiveProfile` from config. When F4 ships, add a new dated entry that supersedes this bullet.
- `BindingsImportCard` and `PttCard` are the two XAML elements gated by profile â€” do not add a third without updating `RefreshProfileVisibility()`.
- Mid-session profile changes save to config but don't re-init services â€” restart required for voice features; this is by design.
- Pill toggle does a **direct apply** (no dialog re-open) â€” `ProfilePill_Checked` handler applies profile, saves config, calls `NotifyRestartRequired()` directly.

### UI / theme
- UI/UX must follow the existing neumorphic dark theme and shared controls (Colors.xaml, Controls.xaml, etc.).
- DataGrid, TabControl/TabItem, GroupBox, CheckBox all styled via implicit styles in `Controls.xaml` â€” do not add per-control inline styling for these in WPF hosts.
- Drive warning (`SelectedDriveWarning`) lives in its own collapsible strip (Row 2 of root grid), not in the log header â€” keep it there for safety visibility.
- Model tag input overlays the tab strip via `Panel.ZIndex=2` + `BgBaseBrush` background â€” intentional, not a z-order bug.
- `ThemedMessageDialog` is PrepApp's general-purpose dialog primitive. All new PrepApp dialogs use it (or a custom Window with the same theme resources). `App.xaml.cs` crash handlers are the explicit exception â€” stay as raw `MessageBox` with zero dependency on the app resource graph.

### Build / tooling
- .NET SDK/TFM bumped to 10.0 â€” x64 .NET 8 runtime not present on dev machine; shared lib stays `net8.0`, tests target `net10.0`, WPF apps stay `net8.0-windows` (runtime is installed x86 only for 8.0).
- Files compiled by the tests project via `<Compile Include>` must carry their own explicit `using` directives â€” don't rely on the owning project's `GlobalUsings.cs`. The test project's `GlobalUsings.cs` is the correct fix location (not suppressions in source files). Established PR #126.

### Drive detection (WMI)
- **USB SSD drive detection primary path:** `ROOT\Microsoft\Windows\Storage` â€” `MSFT_PhysicalDisk WHERE BusType = 7` (USB) â†’ `MSFT_Disk` join via `UniqueId` â†’ `MSFT_Partition.DriveLetter`. Fallback: legacy `Win32_DiskDrive WHERE InterfaceType='USB'` ASSOCIATORS chain (kept for compatibility but misses UAS adapters that report SCSI). Both paths log failures via `Trace.WriteLine` instead of silently swallowing. Established F1 fix (PR #129, commit `3b20db8`). Internal drives still require the ShowFixedDrives toggle. Fail-open is acceptable here (drive enumeration, not a security gate).
- **`MSFT_PhysicalDisk` â†’ `MSFT_Disk` join via `UniqueId` is required** before querying `MSFT_Partition.DiskNumber` â€” `DeviceID` on `MSFT_PhysicalDisk` is not the same value as the OS disk number. Established by Codex catch + `3b20db8`.
- **WMI disposal pattern:** always `using var collection = searcher.Get()` then `using (obj) { ... }` for each loop variable â€” `ManagementObjectCollection` and `ManagementObject` hold COM handles and must be explicitly disposed. Established PR #122.

### Workflow
- **TODO backlog workflow:** "tackle section X" â†’ Claude outputs a well-formed implementation prompt + states the recommended model from the section's `**Model:**` line in `project_backlog.md`. Multi-stage sections target Stage 1 by default unless overridden. README update follows each completed section, not each stage.

---

## 2026-04-17 â€” Headless CLI is a thin HTTP client, not an in-process host

`runner-cli/` is a standalone `net8.0` project that speaks to a running
Runner over its existing LAN HTTP API (`RunnerLocalApiService`). It is
not an in-process console host for Runner, not a WPF/console-mode toggle
on the Runner project, and does not share Runner's DI/boot path. Keeps
Runner's stack unchanged, keeps the CLI dependency-light, and makes the
SSH/Tailscale use case work without touching the WPF host. Established
PR #130 (`bb59a6c`).

---

## 2026-04-17 â€” CLI config precedence: flag > env var > default

For `runner-cli/`, configuration follows the industry-standard
precedence `--flag` > env var > hardcoded default (matches kubectl,
docker, psql, ollama patterns). Default URL is `http://127.0.0.1:41555`
â€” mirrors `PortableConfig.NetworkPort`. API key has no default; a null
key is acceptable only when the host does not require one. API keys are
read from `--api-key` or `$FREEAI_API_KEY` and never logged, echoed, or
persisted. Established PR #130 (`bb59a6c`).

---

## 2026-04-18 â€” v1.2.x: ship each fix as its own PR + release, not bundled

Triage originally grouped X1+X2+X3 as "the v1.2.2 bundle". Stephen
revised 2026-04-18: each bug-fix section gets its own PR and its own
patch release (v1.2.2 = X2 only; X3 will be v1.2.3; X1 will be v1.2.4).
Rationale: narrower PRs are easier to revisit as context for future
work â€” "fewer things that each one has". Applies to the v1.2.x patch
stream; bundled PRs remain fine for multi-stage features (F3/F4/B2
etc.).

---

## 2026-04-19 â€” PrepApp ModelService / ReadinessService bypass IConfigStore intentionally

`ModelService` and `ReadinessService` in PrepViewModel write directly to
`portable-config.json` via `PortableConfig.SaveAsync` / `config.SaveAsync`
rather than routing through `IConfigStore`. This is intentional: both services
run exclusively in the pre-finalize phase of the one-way PrepApp setup flow.
Finalize (`EnableConfigEncryptionAsync`) is the terminal step; it deletes the
plaintext file. Post-finalize, `portable-config.json` does not exist and
PrepApp model operations would fail to load config anyway â€” the PrepApp is not
designed for post-finalize re-entry. Routing these writes through `IConfigStore`
was considered for X9 Stage 4 and explicitly excluded. If the PrepApp ever
gains a "re-open encrypted drive" workflow, these call sites must be revisited.

---

## 2026-04-19 â€” Migration must use LoadWithValidationAsync, not LoadAsync

`TryMigratePlaintextAsync` uses `PortableConfig.LoadWithValidationAsync` (not the
convenience `LoadAsync`) before absorbing a newer plaintext into the encrypted blob.
A corrupt or malformed plaintext returns `isValid = false`; migration bails immediately
and preserves the plaintext rather than overwriting the valid encrypted blob with a
default (empty) config. Security invariant: when the plaintext cannot be validated,
the encrypted blob remains authoritative and untouched. Gemini critical finding on
PR #147 (`b75e42a`).

---

## 2026-04-19 â€” OnClosing drain uses GetAwaiter().GetResult(), not cancel-and-retry

`MainWindow.OnClosing` blocks the UI thread with
`ConfigStore.FlushAsync(5s).GetAwaiter().GetResult()` before `LockSession()`.
Async cancel-and-retry was rejected: WPF shutdown sequencing makes that
pattern easy to get subtly wrong (callbacks fire after the window is gone).
Safe here because `SsdEncryption.SaveEncryptedConfigAsync` uses
`ConfigureAwait(false)` throughout â€” no UI `SynchronizationContext` captured,
no deadlock risk on the block. Established PR #146 (`542559b`).

---

## 2026-04-19 â€” RAG audit: X17 multimodal scoped to Stage 1 diagnostic only

Third-party RAG audit flagged "multimodal PDF ingest" (OCR for scanned pages, table
extraction, image handling) as its #1 Critical finding. Stated product workload is
text-layer PDFs with embedded diagrams (DCS airframe manuals â€” Chuck's Guides and
similar). Scanned PDFs are not part of the near-term use case.

X17 keeps **Stage 1 only**: a textless-page diagnostic that flags per-page when the
extracted text layer is effectively empty, surfaced via the ingest summary (X18). No
OCR engine integration, no table extraction, no image handling at this time. Full OCR
path revisited only if Stage 1 diagnostics show scanned PDFs in active use, or if
embedded-image information is confirmed to carry content that the text layer omits.
Keeps us out of an OCR-engine decision (Tesseract.NET bundled vs external-binary vs
Windows-only) that would churn the portable/macOS deployment story for speculative gain.
Established 2026-04-19 RAG audit triage plan session.

---

## 2026-04-19 â€” X21 embedding provenance slots before F3, reordering the queue

Pre-audit, `project_state.md` queued F3 (PrepApp 3-tab restructure) as the first item
after the H2 hardening batch. Post-audit, X21 (embedding provenance + compat gating,
Sonnet-scale, ~2-3 days) slots in **before F3** between H2 and F3.

Rationale: without provenance gating, a change to the embedding model silently scores
mismatched chunks as zero (`VectorIndex.DotProductSimd` returns 0 on length mismatch â€”
no error thrown, no log). Every downstream RAG item (X15 streaming ingest, X18
observability, X19 hybrid retrieval, X20 section-aware chunking) touches the index; if
any of those triggers an embedding-model swap during development, the corruption is
invisible. X21 adds `embedding_model` / `embedding_dimension` / `parser_version` /
`chunker_version` to the chunk schema and manifest, validates at query + ingest time,
and surfaces mismatches as a clear reindex prompt. Small cost; preventative; unblocks
everything RAG-shaped that follows. Established 2026-04-19 RAG audit triage plan
session.

---

## 2026-04-19 â€” RAG audit fallout: 7 separate X-items, not a single umbrella

RAG audit produced 9 findings. Three absorbed as scope expansions on existing backlog
items (X10 + X13 + X15). Remaining six map to seven new X-items (X17 textless
diagnostic, X18 ingest observability, X19 hybrid retrieval, X20 section-aware chunking
+ metadata, X21 provenance, X22 prompt packing + grounding, X23 realistic test
fixtures).

An umbrella "RAG quality overhaul" item in the X9 multi-stage shape was considered and
rejected. Echoes the 2026-04-18 "ship each fix as its own PR + release" decision:
narrower items are easier to reorder, pause, or drop mid-flight as field priorities
shift. A ~10-stage umbrella locked into a single sequence would fight that flexibility.
Established 2026-04-19 RAG audit triage plan session.

---

## 2026-04-19 â€” X10 ships path-capture first; stable document GUID spins out as X10-Redux

RAG audit argued the root cause of orphaned vectors on re-ingest is path-based chunk
keying, and proposed a stable `document_id GUID` on chunks + manifest entries as the
principled fix. Current X10 scope (capture the old `StoredRelativePath` before
overwrite, delete old vectors + old stored file via that captured path) is kept for the
first PR. Stable-document-GUID upgrade spins out as **X10-Redux**, revisited only if
the path-capture approach shows field issues.

Rationale: path-capture is a smaller blast-radius change that fits the existing X10 PR
shape (already covers rebuild-from-stored, per-file transactionality, SQLite WAL /
busy_timeout). Introducing a new identity layer with schema migration in the same PR
inflates review surface and delays the field-log `vectors.db` lock fix. If path-capture
+ WAL cleanly resolves the symptoms, the identity-layer work may never be needed.
Established 2026-04-19 RAG audit triage plan session.

---

## 2026-04-20 â€” shared/Io/ as home for shared IO utilities

`shared/Io/FileOps.cs` (`FreeAiSsd.Shared.Io`) established as the location for
shared filesystem helpers. All `File.Replace` calls in the shared library must route
through `FileOps.ReplaceWithRetry` (5 attempts, 25 ms base backoff doubling,
`IOException`/`UnauthorizedAccessException` only). New callers should not add bare
`File.Replace` calls â€” extend `FileOps` instead.

---

## 2026-04-19 â€” X21b: reindex prompt triggers on drive selection, not config change

PrepApp's embedding-mismatch reindex prompt fires on drive selection
(`OnSelectedDriveChanged`), not on config edit. A per-session
`HashSet<string> _provenanceCheckedRoots` (OrdinalIgnoreCase) prevents
repeated dialog on repeated selection of the same root.

`ResolveOllamaExe` (finds existing exe, no download) is used for the
reindex path â€” not `EnsureOllamaReadyAsync`, which would silently
download Ollama. If Ollama isn't installed on the drive, reindex aborts
with a user-visible log message. Established PR #158 (`92625a9`).

---

## 2026-04-19 â€” X21 embedding provenance: Option B migration (backfill from blob, no forced reindex)

When migrating existing v1.2.9 libraries to schema M2, existing rows receive
`embedding_model = 'unknown'` and `embedding_dimension` backfilled from
`LENGTH(embedding)/4`. The gate hard-refuses only on dimension mismatch;
model-name drift from `'unknown'` logs a warning only.

Forcing a full reindex on upgrade was rejected â€” users with large libraries
(800-page PDFs) should not have to re-embed just to upgrade. Option B is
reversible: if field data shows model-drift false-negatives causing real
problems, a stricter gate can be added in X21b or a follow-on item without
changing the schema. Established PR #157 (`449ec2e`).


---

## 2026-04-20 â€” wrap-up runs on feature branch before merging

Run /wrap-up on the feature branch before merging the PR so doc updates
land in the same commit and no separate solo doc push is needed after merge.
Merge commit SHA will be absent from the state doc entry â€” the PR number is
sufficient for git traceability. First applied on PR #161 (X12).

---

## 2026-04-20 â€” ChatResult / TranscriptionResult discriminated unions; RagRetrievalFailed as first-class variant

`IChatService` and `ISpeechToTextService` return sealed abstract record unions
(`ChatResult` and `TranscriptionResult`) instead of raw payloads. All callers
must switch exhaustively â€” the compiler rejects unhandled cases. This eliminates
silent empty-string returns masking transport and model failures.

`ChatResult` has three variants: `Success(ChatResponse)`,
`RagRetrievalFailed(ChatResponse, string RagError)`, and `Failure(string ErrorMessage)`.
`RagRetrievalFailed` is distinct from "no hits above threshold" (which is `Success`
with `usedContext=false`). The LAN API surfaces the distinction via
`X-RAG-Status: retrieval-failed` vs `success` response header. The streaming
endpoint emits in-stream `{type:"error"}` / `{type:"rag-warning"}` NDJSON events
(headers are already committed after `{type:"start"}`).

`OperationCanceledException` is not caught and returned as `Failure` â€” it rethrows,
letting callers observe cancellation naturally. Established X13 (PR forthcoming).

---

## 2026-04-21 â€” F3 merged-grid actions use explicit bulk selection only

PrepApp's merged Models grid does **not** auto-select configured/downloaded rows.
All model actions now operate only on rows the user explicitly checked in the grid.

The standalone Verify action is removed from the PrepViewModel/UI. Download skips
checked rows already present on the drive, and Remove applies one chosen action to
all checked rows instead of silently acting on the first checked row only. For
config-only removal, entries are removed from config rather than merely reset to
`NotInstalled`.

Rationale: after Starter + Configured Models merged into one grid, the old defaults
became unsafe and misleading â€” default-selected downloaded rows could trigger
accidental re-downloads, and first-row-only Remove no longer matched the visual
selection model. Explicit selection keeps the 2-tab PrepApp flow predictable for
non-technical users.

---

## 2026-04-21 â€” Plan / prompt / execute handoffs include an explicit GPT-5.4 vs GPT-5.3 Codex recommendation

When a backlog item is handled via a plan -> prompt -> execute loop, the prompt
draft must explicitly call out which execution model is recommended (`gpt-5.4`
vs `gpt-5.3 codex`) and give a short rationale.

Rationale: model choice had been implicit during handoff. Making it explicit at
prompt time reduces ambiguity when resuming in a fresh session and makes the
saved execution prompt self-contained.

---

## 2026-04-21 - F4 Stage 1: PrepApp owns first-run profile selection; Runner no longer blocks on null ActiveProfile

This entry supersedes the 2026-04-17 Profiles decision that required a first-run
Runner profile dialog before the app could proceed.

PrepApp's FTUE now owns the first profile choice. The four-step FTUE starts with
the two-machine architecture explainer, includes an inline profile selector, and
persists the local selection in `PrepTargetPreferenceStore` until finalization.

Finalize writes `PortableConfig.ActiveProfile` and immediately applies
`ProfileDefaults.Apply(config, profile)` before the final save / encryption path.
Runner still allows mid-session profile switching via the in-window pills.

Runner must remain backward-compatible with older SSDs where `ActiveProfile` is
null. In that case it starts normally, keeps flight-sim-only UI hidden by default,
and does not resurrect the old required modal.

---

## 2026-05-05 - MAC2 platform boundary: shared stays mixed only as known debt until adapters exist

MAC2 audits the current macOS portability blockers without moving runtime code.
`shared/FreeAiSsd.Shared.csproj` remains a plain `net8.0` project, but its
`System.Management`, `NAudio`, and `SharpDX.DirectInput` references are now
explicit known debt, not a precedent for adding more Windows-only packages to
the future core.

The extraction direction is: platform-neutral Runner core first, then host or
adapter projects for Windows-only audio capture/playback, DirectInput HOTAS,
Windows SAPI, WMI system probes, UAC, and PowerShell `Format-Volume`. The Mac
Swift app remains thin and should consume the shared/local host boundary rather
than duplicate encryption, RAG, or API behavior.

`tests/MacPlatformBoundaryTests.cs` is the guardrail: it keeps `shared/` as a
plain non-WPF project, treats the current Windows-only package references as a
bounded inventory to pay down, and keeps `runner-cli/` as a portable HTTP
client rather than an in-process Runner host.

---

## 2026-05-05 - MAC1 supported Mac baseline: Apple Silicon, macOS 11+, arm64-only

The first supported Mac release targets Apple Silicon Macs only. Intel Macs are
explicitly unsupported, not best-effort beta, unless a later decision
supersedes this baseline.

Minimum supported OS is macOS 11 Big Sur because it is the earliest macOS
generation that runs production Apple Silicon Macs. The current local test
machine may run a much newer Tahoe 26.x build, but that does not raise the
support floor by itself.

Free-AI-SSD Mac app artifacts are arm64-only. Universal or x86_64 app artifacts
are out of scope. Upstream payloads such as Ollama may be consumed if they ship
as universal binaries, but only the Apple Silicon path is promised and
validated.

The supported shared Windows + macOS SSD filesystem is exFAT. NTFS remains the
Windows-only full-runner format. APFS is Mac-only and is not a supported
Windows PrepApp staging target unless a future Mac-native prep/staging workflow
exists.

PrepApp should gain an OS compatibility choice before broad Mac distribution:
Windows only preselects NTFS; Windows + macOS preselects exFAT; macOS-only media
still uses exFAT when staged from Windows, with APFS deferred until a Mac-native
prep workflow exists.

The first supported Mac release requires encrypted config unlock/save, verified
macOS Ollama start/stop, streaming and non-streaming chat, RAG citations,
document library use, useful diagnostics, and honest packaging, signing, and
notarization state.

Voice/STT/TTS, HOTAS/PTT, DCS import UI, Companion split-PC workflows, and a
Windows-equivalent Prep UI are deferred beyond the first supported Mac release.

The UI direction remains Swift/SwiftUI as a native thin Mac UI over shared/core
services. This is the right default for a Mac app as long as Swift does not
duplicate encryption, RAG, config, or network API logic. Avalonia or another
cross-platform UI should be reconsidered only if MAC3-MAC7 prove the thin
Swift host blocks parity or creates meaningful duplicated business logic.

---

## 2026-05-05 - MAC3 Runner core boundary: shared business logic moves out of WPF host

MAC3 introduces `runner-core/FreeAiSsd.RunnerCore.csproj` as a plain `net8.0`
home for platform-neutral Runner logic. Chat, RAG orchestration, document
operations, model management, local API endpoint logic, and service contracts
now live there instead of being compiled directly from the WPF `runner/`
project.

The WPF Runner remains the Windows host and adapter layer. Windows Ollama
process launch/trust startup, Whisper concrete STT, Windows/Piper TTS playback,
DirectInput HOTAS/PTT, DCS import UI support, and WMI-backed system resource
probing stay in `runner/`. Model sizing now depends on the core
`ISystemResourceProbe` contract, with `WindowsSystemResourceProbe` as the
Windows adapter.

`tests/MacPlatformBoundaryTests.cs` now guards RunnerCore as a non-WPF,
non-Windows-targeted project without blocked Windows-only package references or
a project reference back to the WPF Runner.
