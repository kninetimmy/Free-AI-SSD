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

---

## 2026-05-05 - Cross-platform PrepApp parity (amends MAC1)

This entry amends the 2026-05-05 MAC1 baseline, which deferred a Windows-equivalent
Prep UI beyond the first supported Mac release. Mac-native PrepApp is now in scope
as MAC16/MAC17/MAC18, sequenced after Runner parity (MAC4-MAC8) and packaging
hardening (MAC10/MAC10a/MAC11), and before the supported-release docs (MAC15).

**Rationale.** A Mac-only user should be able to download Free-AI-SSD on a Mac,
prep an external SSD, and run the app without ever owning or borrowing a Windows
machine. Symmetrically, a Windows-only user should keep the Windows PrepApp path.
The current "Windows PrepApp prepares everything; Mac just runs" model leaves
Mac-only users dependent on a borrowed Windows machine, which contradicts the
stated goal of treating macOS as a first-class supported platform.

**Source/target compatibility, accepted as OS limits (not project gaps):**

- APFS targets require a Mac source. Windows cannot natively format APFS, so
  APFS prep from Windows is permanently out of scope. Bundling third-party
  drivers to enable it would add licensing and security review surface that
  isn't justified for this workload.
- NTFS targets require a Windows source. macOS cannot natively format NTFS, so
  NTFS prep from Mac is permanently out of scope. Mac users wanting an
  NTFS-only drive are directed to Windows PrepApp in docs.
- exFAT works from either source and is the universal common ground. It is
  the only filesystem produced by the macOS PrepApp.

**APFS dropped from supported targets entirely.** Earlier MAC1 wording deferred
APFS until a Mac-native prep workflow existed; that workflow is now planned, but
APFS is not. exFAT is adequate for the supported workload (RAG via SQLite,
model files, DCS bindings, encrypted config). The SQLite-WAL-on-exFAT-with-
external-drive risk applies to cross-platform drives anyway, so it must be
hardened regardless of whether APFS exists. APFS may be revisited only if exFAT
proves inadequate during MAC17 validation; in that case a new dated decision
will record the trigger and target.

**Composition direction in scope for the first supported Mac release.** Mac
Runner is the cross-platform composition target: it hosts RAG, DCS bindings,
encrypted config, the LAN API, and X4's web chat UI (because
`RunnerLocalApiService` lives in `runner-core/` post-MAC3, X4 lands on Mac with
no Mac-specific UI track). The Windows Companion can connect to a Mac-hosted
Runner over LAN. **Companion-on-Mac (Mac as a Companion host) remains deferred**
- the niche of a niche of a niche.

**Architectural pattern.** MAC16 mirrors MAC3: extract platform-neutral PrepApp
business logic (manifest, staging, prereq fetch, encrypted config write, starter-
model catalog) into a new `prep-core/FreeAiSsd.PrepCore.csproj`. WPF prep host
on Windows and SwiftUI prep host on Mac both consume `prep-core/`.
`IDriveService` adapter stays platform-specific (`Format-Volume` on Windows,
`diskutil` on Mac) under the MAC2 boundary tests.

**Security invariants are unchanged on either side.** SHA-256 + URL allowlist
on prereq downloads, explicit argument lists on `diskutil` and `Format-Volume`
calls, encrypted-config format unchanged so drives roundtrip Mac <-> Windows.

---

## 2026-05-05 — Mac Ollama trust gate (MAC4)

PR #177, merge commit `648fcd9`. The macOS Ollama runtime now passes the same
supply-chain gates as Windows, plus an Apple Silicon (arm64) Mach-O slice check
required by the MAC1 baseline.

**Pinned version, single source of truth.** `OllamaPackageTrustPolicy` now
exposes `DefaultMacPackage` alongside `DefaultWindowsPackage`, both pinned to
upstream Ollama `v0.5.7`. `MacToolCatalog.Ollama.SourceUrl` reads from
`DefaultMacPackage.Url`, and `tools/FreeAiSsd.PrereqFetch` downloads the
pinned URL directly (no more `releases/latest/...` resolve), so the bundled
zip, the staging hash check, and the runtime trust gate cannot drift.

**Validator core is shared between platforms.** `ValidateExecutionAttestation`
(Windows) and the new `ValidateMacExecutionAttestation` both call a single
`ValidateExecutionAttestationCore(attestationPath, urlText)` helper. Adding a
third platform later means adding one path resolver and one entry to
`PinnedMetadataByUrl`, not duplicating the validator. The Mac attestation
lives at `<ssd>/mac/tools/ollama/ollama-package-trust.json` so a single SSD
can carry both Windows and Mac attestations without collision.

**arm64 slice check runs in pure managed code.** New `MachOArchInspector`
parses Mach-O magic + fat headers without shelling out to `lipo`. This lets
Windows-side PrepApp validate the arm64 slice during staging, before the SSD
ever touches a Mac. Pure-x86_64 payloads now fail closed with a new
`OllamaPackageTrustFailureReason.Arm64SliceMissing` reason; universal
(arm64+x86_64) and pure-arm64 payloads pass.

**Rationale.** MAC1 fixed Apple Silicon as the only supported Mac hardware,
but nothing yet *enforced* that at staging or runtime. A future Ollama release
could ship x86_64-only without warning; without this gate the staged drive
would still validate by SHA-256 and fail confusingly at first launch. The
arm64 check turns that into a clear, early refusal at prep time.

**Process-launch invariant preserved.** `MacOllamaLifecycleService`
(runner-core, plain `net8.0`) and the Swift `mac-runner` both launch
`ollama serve` via argument-array forms (`ProcessStartInfo.ArgumentList` in
C#, `Process.arguments: ["serve"]` in Swift), never string concatenation.
Loopback bind is non-negotiable: `OLLAMA_HOST=127.0.0.1:<port>` always, never
`0.0.0.0`. Both gates check the on-SSD attestation and refuse to start
`ollama serve` on missing / malformed / URL-mismatched / SHA-mismatched
attestations.

**Out of scope of MAC4 (deferred to later items):** encrypted-config unlock
on Mac (MAC5), Mac LAN API host (MAC6), the staging-time selection between
`Resources/ollama` and `MacOS/Ollama` inside the upstream `Ollama.app` bundle
(latent pre-existing behavior, not introduced or worsened by MAC4).

---

## 2026-05-05 — MAC5 native Swift encryption: deliberate format duplication

The macOS Runner now unlocks and re-saves encrypted SSDs natively via
`mac-runner/Sources/SsdEncryption.swift`, a Swift port of
`shared/SsdEncryption.cs`. PBKDF2-HMAC-SHA256 (CommonCrypto) and AES-256-GCM
(CryptoKit) are reimplemented in Swift; the on-disk format is unchanged
(`aes-256-gcm+pbkdf2-sha256-v1`, 210k iterations, 16-byte salt, 12-byte nonce,
16-byte tag, lowercase camelCase JSON fields, two-file atomic commit with
state-rename rollback).

**Why duplicate.** The alternative was hosting a .NET 8 console process on
Apple Silicon purely so the Mac runtime could call into `SsdEncryption` and
`ConfigStore`. That would drag a cross-architecture runtime onto the Mac
launch path to do nothing but read and re-emit a JSON config blob — a small,
stable, security-critical surface that Apple ships native primitives for
(`CryptoKit.AES.GCM`, `CommonCrypto.CCKeyDerivationPBKDF`,
`Foundation.FileManager.replaceItemAt`). For MAC5's bounded surface, native
beats cross-arch hosting on every axis except code reuse, and the cost of
that reuse (a .NET sidecar) is high enough to defer to a later item if it's
ever justified at all.

**Why this doesn't cascade.** The MAC2 dependency-audit doc said "keep the
Swift app thin — do not duplicate encryption, RAG, or network API logic in
Swift." MAC5 explicitly waives that guideline for the encrypted-config format
*only*. RAG (MAC7), document management (MAC8), and network API hosting
(MAC6) keep the original guideline; those surfaces are large, evolving, and
not well-served by a native rewrite.

**How drift is prevented.** The cross-language pin lives in
`tests/Fixtures/MacEncryptedConfig/csharp-encrypted/`. The Swift test binary
generates the fixture (via the `write-fixture` subcommand) and round-trips it
on every Mac CI run; the C# `MacEncryptedConfigCrossLanguageTests` round-trip
the same fixture on every Windows CI run and additionally assert the JSON key
shape (`enabled`, `scheme`, `iterations`, `encryptedConfigFile`, `updatedAtUtc`
on the state file; `version`, `scheme`, `iterations`, `salt`, `nonce`, `tag`,
`ciphertext`, `createdAtUtc` on the encrypted blob) so a silent
`JsonNamingPolicy` change on the C# side fails Windows CI. The error strings
returned to users are pinned identically on both platforms ("Incorrect
password.", "Encrypted drive metadata is missing.", etc.) so user-facing docs
apply to both.

**Process-launch and key-handling invariants kept.** AES-GCM nonces come from
`SecRandomCopyBytes` (never reused, never derived); the derived 32-byte key
is held in a Swift `Data` buffer wrapped by an `UnlockMaterial` class whose
`zeroize()` overwrites the buffer on app background, app termination, manual
lock, and `deinit`. No plaintext `portable-config.json` is ever written by the
Mac runner; the plaintext-migration helper mirrors
`SsdEncryption.TryMigratePlaintextAsync` (branch A merges newer plaintext into
the encrypted blob then deletes plaintext; branch B silently removes stale
plaintext).

**Exit ramp.** If MAC6 (Mac LAN API host) ends up bringing a long-running
.NET process onto Mac for chat/streaming/RAG, the encrypted-config logic can
optionally consolidate back into `IConfigStore` at that point. That would be
a MAC6 decision, not a regret about MAC5 — the format pin makes either
direction safe.

**Out of scope of MAC5 (deferred to later items):** Mac LAN API host (MAC6),
RAG / streaming / models endpoints on Mac (MAC6/MAC7), document management
(MAC8), Mac-native PrepApp that *creates* encrypted drives from Mac (MAC17;
MAC5 only handles unlock and re-save of existing blobs).

---

## 2026-05-06 — MAC6 Mac net8.0 sidecar host: approved exit ramp from MAC5

The macOS Runner now hosts the LAN API surface (`/api/health`, `/api/models`,
`/api/chat`, `/api/chat/stream`, gated `/tts/*`, `/stt/transcribe`,
`/voice/query`, plus the X4 static-file plumbing) by spawning a net8.0
sidecar process — `mac-runner-host/FreeAiSsd.MacRunnerHost.csproj` — that
links runner-core directly and reuses `RunnerLocalApiService` byte-for-byte.
The Swift `mac-runner` owns Ollama lifecycle and encrypted-config IO; the
sidecar consumes both via stdin handshake.

**Architectural choice.** Sidecar over Swift port. The alternative —
reimplementing `RunnerLocalApiService` + `ChatService` + multipart audio
+ NDJSON streaming + the RAG glue in Swift — would have produced large,
ongoing duplication for an active surface. MAC7 (RAG parity) and MAC8
(document management) both touch the same underlying services; a Swift
port would either fork the in-flight work twice or block MAC7/MAC8 on a
Swift catch-up project.

**Rationale.** MAC5 left this exit ramp open explicitly: encrypted-config
unlock/save was a small, stable, security-critical surface that justified
a native Swift port; the chat / RAG / API surface is large, evolving, and
not well-served by duplication. Apple Silicon (.NET 8 osx-arm64) is fast
enough that a long-running .NET host pays a tolerable cost — the only
real downside is the ~70 MB self-contained publish footprint, which is a
rounding error on an SSD already carrying multi-GB models.

**Boundary preserved from MAC5.** Encrypted-config IO stays Swift-
authoritative. The Swift app unlocks the SSD, holds the in-memory
plaintext as `[String: Any]`, and hands the dictionary to the sidecar
over stdin only — never via a temp file, never via shared memory. The
plaintext-config invariant from MAC5 is unchanged: no plaintext
`portable-config.json` is ever written to disk on Mac. When the user
locks the drive (manual lock, app background, app terminate), the
sidecar receives `shutdown\n` on stdin and exits before the Swift app
zeroes the unlock material.

**Reuse boundary, encoded.** The Mac host references runner-core
directly. There is no fork of `RunnerLocalApiService`. Any platform
behavioral difference is expressed by injecting a different DI
implementation (e.g., `NoOpSpeechToTextService` and `NoOpTtsProvider`
in place of `WhisperSpeechToTextService` and `PiperTextToSpeechService`).
This is enforced by `MacPlatformBoundaryTests.MacRunnerHost_RemainsPlainNet8WithoutWindowsPackages`.

**X4 plumbing, not X4.** `RunnerLocalApiService` now wires `UseDefaultFiles`
+ `UseStaticFiles` in front of the `/api/*` group when a `wwwroot/`
directory exists. The runner-core project committed `wwwroot/.gitkeep`
so the directory ships in publish output on both Windows and Mac. No
SPA assets are bundled in MAC6 — when X4 ships those assets,
`/chat/index.html` is served from the same Kestrel on both platforms
with no Mac-specific code path.

**Out of scope of MAC6 (deferred):** RAG endpoint parity (MAC7),
document management endpoints (MAC8), Mac-native PrepApp (MAC17), and
the actual X4 SPA implementation. The host serves chat without a
populated library (RAG-off path) until MAC7 lands.

---

## 2026-05-06 — Review follow-up fixes use a separate PR

When a merged PR review turns up follow-up bugs or cleanup, implement those
fixes on a separate branch/PR rather than continuing on the merged feature
branch. This keeps the original shipped PR history clean, gives CI and review
a distinct target for the follow-up, and makes it obvious which changes were
part of the original feature versus post-merge hardening. The local PR #181
follow-up fixes from 2026-05-06 are a one-time exception; apply this rule to
future reviews after that.

---

## 2026-05-06 — MAC8: broader `/api/library/*` API supersedes R1 Stage 2

PR #185 ships the full library-management surface as eight endpoints —
list / create / set-active / upload (multipart + NDJSON progress) / delete /
add-watched-folder / sweep / rebuild — instead of the originally narrower
`R1 Stage 2` plan (`GET /api/documents` + `POST /api/documents/reindex`).
Rationale: the same endpoints serve the Mac Swift UI now and Windows
Companion / RunnerCli later, so the API is shaped once. User-explicit
direction: "more work now is just less work later." `R1 Stage 2`'s entry
in `project_backlog.md` is updated to call out only the RunnerCli `/docs`
+ `/reindex` slash-commands as remaining work — they wrap the existing
endpoints, no new server surface needed.

---

## 2026-05-06 — MAC8: Mac sidecar uses `NoOpConfigStore` to preserve plaintext-config invariant

The MAC8 endpoint group introduces call paths through
`DocumentOperationsService.SaveConfigAsync`, which on Windows persists via
the real `ConfigStore`. Mac cannot do the same without violating the
MAC5/MAC6 invariant that encrypted-config IO is Swift-authoritative
(`ConfigStore` would either fail without a derived key or fall back to
plaintext). The fix:

- `mac-runner-host/NoOpConfigStore.cs` implements `IConfigStore` with no-op
  `SaveAsync` / `LoadAsync` and wired into the sidecar's DI in place of
  `ConfigStore`.
- Mutating MAC8 endpoints (`POST /api/library`, `PUT /api/library/active`)
  return the updated `activeLibraryId` in their HTTP response so the Swift
  parent persists via `SsdEncryption.swift`.
- `PortableConfig.ActiveDocumentLibraryId` is mutated in the sidecar's
  in-memory copy by `DocumentOperationsService`, so subsequent `/api/chat`
  requests on the same sidecar process honor the new active library
  without a save round-trip.
- Library manifests, watched-folder lists, and the chunk index are
  on-SSD JSON / SQLite owned by `DocumentLibraryManager` — never
  plaintext-config-adjacent and persist regardless of who owns
  `IConfigStore`.

Windows host keeps using the real `ConfigStore` directly via the WPF
Runner UI's in-process call path — the HTTP layer is additive and not
in the Windows hot path.

---

## 2026-05-06 — MAC8: NDJSON progress pump uses sync queue + drain, not Channel + Task.Run

`RunnerLocalApiService.PumpProgressAsync` (used by `/api/library/{id}/files`,
`/sweep`, `/rebuild`) buffers `IndexingProgress` events into a thread-safe
`ConcurrentQueue<T>` populated synchronously by the
`Action<IndexingProgress>` callback `DocumentIngestor` invokes, then drains
the queue and writes NDJSON frames on the request thread *after* the
operation completes.

The earlier design used `Channel<IndexingProgress>` + `Task.Run` for a live
streaming pump, which crashed Windows CI in ways that took two commits to
diagnose (cross-thread writes to Kestrel's response pipe interacted badly
with how Windows ASP.NET serializes pipe writes). The buffered design
loses live progress streaming, but that's irrelevant: the Mac UI parses
NDJSON buffered (macOS 11 baseline lacks `URLSession.bytes`), and the
test fixtures buffer the body too. The wire shape — start frame, progress
frames, complete or error — is unchanged.

Future SSE/NDJSON-emitting endpoints in `RunnerLocalApiService` should
follow this pattern unless live streaming is a strict UX requirement (in
which case the cross-thread risk has to be re-investigated).

---

## 2026-05-06 — MAC8: `WriteNdjsonAsync` uses explicit camelCase JsonNamingPolicy

`RunnerLocalApiService.WriteNdjsonAsync` serializes payloads with a static
`JsonSerializerOptions { PropertyNamingPolicy = CamelCase }`, matching what
`ConfigureHttpJsonOptions` does for regular `IResult` responses.

Before this fix: `JsonSerializer.Serialize(payload)` with default options
rendered anonymous-type properties lower-camel ("type", "library") but
nested record properties PascalCase ("FileCount", "Files", "Id"). Mixed
casing on the wire broke clients reading `library.fileCount` — including
the new MAC8 tests, which spent two CI runs failing with
`KeyNotFoundException` until a defensive test diagnostic dumped the body.

Rule: any future NDJSON-emitting code in `runner-core/` must serialize
through the same camelCase options (or a shared helper) so anonymous-type
fields and record fields agree.

---

## 2026-05-06 — MAC8: ASP.NET catch-all routing preserves `%2F` — decode explicitly

When a route uses catch-all (`{*relPath}` syntax), ASP.NET decodes most
percent-encoded characters but deliberately leaves `%2F` encoded; decoding
it would change the route structure (a `/` is a path segment delimiter).
Server handlers that accept paths via catch-all must call
`Uri.UnescapeDataString` on the bound parameter so clients can use either
`Uri.EscapeDataString`-style encoding (which encodes `/` to `%2F`) or
raw forward slashes (which the catch-all preserves). Both must round-trip
to the same logical path.

Discovered when `RunnerLocalApiLibraryTests.DeleteFile_RemovesEntryAndReturnsRefreshedManifest`
failed with HTTP 400 because PathGuards saw `files%2F<sha>_<name>.txt`
(escaped `/`) and refused on traversal. Fix in
`RunnerLocalApiService.cs` `MapDelete("/{libraryId}/files/{*relPath}", ...)`
re-decodes via `Uri.UnescapeDataString` before passing to `PathGuards`.

---

## 2026-05-06 — MAC9: Swift thin-UI over .NET sidecar locked in as long-term Mac UI

After MAC4-MAC8 shipped the full Mac Runner-parity track (encrypted
config, Ollama lifecycle, RAG, library management, network API host,
chat with citations), MAC9 is the architecture checkpoint to re-evaluate
whether the Swift/SwiftUI thin-UI bet from MAC1 was correct, or whether
to course-correct (cheapest moment, before MAC10/10a/10b/11 packaging
hardening locks in artifact shape).

**Decision:** Keep Swift as a thin native UI over the local
`mac-runner-host` .NET sidecar. Reject Avalonia replacement and
CLI-first-longer alternatives.

**Evidence from MAC4-MAC8:**
- Swift surface: ~1,730 lines (`mac-runner/Sources/main.swift` 1,226 +
  `mac-runner/Sources/SsdEncryption.swift` 502).
- Business logic in Swift: zero. RAG, chat, ingestion, library CRUD,
  `/api/*` endpoints all live in `runner-core/` net8.0 and run on Mac
  via the `mac-runner-host` sidecar.
- Approved duplication: exactly one — `SsdEncryption.swift`, with the
  documented waiver and exit ramp from the 2026-05-05 MAC5 decision
  entry.
- Parity blockers caused by the UI-architecture choice: zero. MAC4
  through MAC8 all shipped without UI-architecture friction.
- Mac-native niceties Swift gave us free: NSOpenPanel pickers,
  lock-on-background, app-terminate key zeroization, NSApplication
  lifecycle integration.

**Why not Avalonia:** would throw away ~1,200 lines of working Swift UI
through MAC8, reintroduces the cross-arch .NET hosting concern that
MAC5 deliberately dodged for `SsdEncryption`, makes Apple lifecycle /
signing / sandbox harder rather than easier, delivers zero user-visible
value, and risks the temporary Apple Developer access window earmarked
for MAC10/MAC11 validation.

**Why not CLI-first-longer:** would regress shipped capability. The
Swift app already exists, works, and matches Windows Runner feature
parity for the in-scope subset. Going CLI-first now is a step backward
on Stephen's stated goal ("get Mac up to Windows level").

**Exit-ramp criteria — re-open MAC9 if any of these become true:**
- A Mac UI feature requires duplicating non-trivial *business* logic in
  Swift (UI-only duplication does not count).
- WPF and Swift UIs drift apart in feature set faster than parity work
  can keep up across two language ecosystems.
- Apple lifecycle, sandbox, or signing complexity in the Swift host
  comes to exceed what Avalonia would inherit anyway.
- A second non-Apple platform target (e.g. Linux Runner) is added,
  shifting the calculus toward a single cross-platform UI codebase.

None of these are true today, so MAC9 closes here without runtime
changes. The Swift/SwiftUI thin-UI + `mac-runner-host` sidecar is the
supported Mac UI architecture going forward.

## 2026-05-06 — MAC10a: filesystem derived from existing PrepTargets, not a new selector

The MAC10a backlog entry reads as "add a Windows / Windows+macOS /
macOS-only compatibility selector before format." When the work was
opened, that selector already existed: PrepApp has had **Prepare for
Windows** and **Prepare for Mac** checkboxes (bound to `PrepareWindows`
/ `PrepareMac` on `PrepViewModel`, persisted via
`PrepTargetPreferenceStore`) since the Mac track started. Adding a
parallel "filesystem" dropdown would have been a second source of truth
for the same user intent.

**Decision:** MAC10a derives the filesystem from the already-present
`PrepTargets` selection rather than introducing a new control:
- Windows-only → `NTFS`.
- Anything that includes Mac (Mac-only or Win+Mac) → `exFAT`.
- The chosen filesystem is surfaced in `EraseConfirmDialog` so the user
  sees "Format as: NTFS (Windows only)" or "Format as: exFAT (Windows +
  macOS compatible)" before the destructive Format-Volume call.

**Why APFS is still deferred:** Per MAC1, APFS is a Mac-native filesystem
and Windows can't reliably create or write to it. Until a Mac-native
prep workflow exists (MAC17), even the "Mac-only" branch has to stage
exFAT from the Windows PrepApp. The mapping table will get an
APFS branch when MAC17 lands; until then, Mac-only and Win+Mac collapse
to the same exFAT answer.

**Security invariants unchanged:** label still passes via the
`FREEAI_FORMAT_LABEL` env var (never inlined), `powershell.exe` is still
resolved through the absolute System32 path, the `EraseConfirmDialog`
gate still precedes the destructive call, and `ProcessRunner.ArgumentList`
is still used for the format launch.

**Exit ramp:** if a future requirement separates "what OS will use this
SSD" from "what filesystem to format" — for example, a power-user case
where someone wants exFAT on a Windows-only drive for cross-tooling
reasons — re-introduce a dedicated filesystem control then. As long as
the two intents stay 1:1, deriving one from the other keeps the UI
honest.

## 2026-05-06 — MAC10b: single shared app icon across Mac Runner and all WPF hosts

MAC10b set out to replace the default macOS placeholder icon on
`Runner.app`. The user asked that the same icon also apply to the
Windows WPF apps for cross-platform brand parity, and that the choice
between "one icon for all hosts" vs "different icons for Runner / PrepApp /
Companion" be left to judgement.

**Decision:** one shared icon — `assets/icon/AppIcon` — applies to all four
hosts: Mac `Runner.app`, Windows Runner, Windows PrepApp, Windows Companion.

**Why one icon, not per-host:**
- Free-AI-SSD is one product. PrepApp and Runner are phases of the same
  user journey on the same drive, not standalone apps competing for
  recognition. A shared mark reads as "Free-AI-SSD" everywhere.
- Per-host icons would mean three more art files to keep in sync if the
  brand changes, with no user payoff (PrepApp users will recognize the
  Runner icon when they see it later, and vice versa).
- Companion is intentionally a satellite of Runner; matching icons make
  the relationship visually obvious.

**Asset pipeline:** the canonical art is generated in code by
`assets/icon/IconRenderer.swift` (Core Graphics drawing of a hexagonal
chip with a glowing core on a Big Sur squircle). `assets/icon/build-icons.sh`
renders every required size and produces `AppIcon.icns` (macOS) +
`AppIcon.ico` (Windows) + `AppIcon.png` (1024 master). Both binaries are
committed so CI doesn't need a Swift-renderer step on the Windows job and
so MSBuild can reference the `.ico` directly via `<ApplicationIcon>`. Re-run
`build-icons.sh` if the design changes; the binaries are derivable, but
committed for build-time stability.

**Info.plist polish bundled with this work:** `CFBundleName` and a new
`CFBundleDisplayName` set to "Free AI SSD" (≤15 chars); `CFBundleVersion`
added (was missing); `CFBundleIconFile=AppIcon` added; `LSApplicationCategoryType=public.app-category.utilities`,
`LSRequiresNativeExecution=true` (arm64-only per MAC1), `NSHighResolutionCapable=true`,
`NSHumanReadableCopyright`. `CFBundleShortVersionString` deliberately
left at "1.0" — version-tracking for the Mac app firms up at MAC11 when
signing/notarization make releases real.

**Exit ramp:** if a downstream phase warrants distinct identity (e.g.
PrepApp ships standalone for IT admins as a separate distribution
channel, or Companion gets a marketing push as a remote-control product),
introduce per-host icon files at that point. The `<ApplicationIcon>`
property is per-csproj, so divergence is a one-line change per host.

## 2026-05-06 — MAC16: prep-core RootNamespace pinned to FreeAiSsd.PrepApp for namespace stability

MAC16 extracted platform-neutral PrepApp business logic out of the
WPF `prep-app/` host into a new `prep-core/FreeAiSsd.PrepCore.csproj`
(plain `net8.0`), mirroring the MAC3 (`runner-core/`) pattern. Eleven
files moved: six service implementations (`ArtifactStaging`, `Prereq`,
`OllamaPackage`, `Model`, `Readiness`, `Encryption`),
`StarterModelCatalog`, `MacArtifactAvailability`, `ModelOperations`,
`OllamaServerHandle`, plus `Resources/starter-models.json`.

**Decision (a):** the new `prep-core` csproj pins
`<RootNamespace>FreeAiSsd.PrepApp</RootNamespace>` so every moved file
keeps its original namespace (`FreeAiSsd.PrepApp` /
`FreeAiSsd.PrepApp.Services`). Same trick MAC3 used for runner-core
(kept `FreeAiSsd.Runner.*` namespaces). Two reasons:

1. **Zero call-site churn.** `prep-app/MainWindow.xaml.cs`,
   `tests/ModelOperationsTests.cs`, and `shared/ViewModels/PrepViewModel.cs`
   all imported the existing namespaces; renaming would have meant
   touching every consumer for cosmetic gain.
2. **Embedded resource name stability.** `StarterModelCatalogLoader.Load`
   falls back to an embedded resource named
   `FreeAiSsd.PrepApp.Resources.starter-models.json`. MSBuild generates
   embedded resource names from `<RootNamespace>` + folder path; pinning
   the root namespace keeps the resource lookup string in the loader
   correct without a change.

**Decision (b):** `<InternalsVisibleTo Include="FreeAiSsd.Tests" />` was
added to the prep-core csproj. `tests/ModelOperationsTests.cs` calls
`ModelOperations.BuildOllamaArgs` and
`ModelOperations.TrySelectModelLayerDigest`, both `internal static`
helpers. The previous `<Compile Include="..\prep-app\ModelOperations.cs"
Link="...">` link-source pattern in `tests/FreeAiSsd.Tests.csproj`
compiled the source directly into the test assembly, so `internal`
members were visible without ceremony. Replacing that with a
`ProjectReference` to prep-core revoked that visibility — the first CI
Windows build failed with four CS0117 errors. Surfaced via fix-forward
commit `079bea3`. Choosing `InternalsVisibleTo` over widening
`BuildOllamaArgs`/`TrySelectModelLayerDigest` to `public` because they
are implementation details of the Ollama CLI argument format and OCI
manifest layer selection — narrowing public API surface is preferable
to widening it for test plumbing.

**Why one big extraction PR (vs incremental file-by-file):** the eleven
moved files form a tightly-coupled cluster — `ArtifactStagingService`
references `MacArtifactAvailability`, `ModelService` references
`ModelOperations`, `OllamaPackageService` references `OllamaServerHandle`.
Splitting them across PRs would have forced temporary ProjectReferences
in both directions or duplicated source files mid-refactor. One mass
`git mv` with `git`'s rename detection preserves history cleanly and
makes the boundary review tractable in one pass.

**Exit ramp / re-evaluation triggers:**
- If MAC17 (macOS PrepApp MVP) finds it needs to expose any of the
  currently-internal `ModelOperations` helpers to a non-test consumer,
  promote them to `public` at that point and drop the
  `InternalsVisibleTo` line.
- If a future `prep-core` consumer outside `prep-app/` and
  `mac-prep-app/` (MAC17) wants to use the moved types under a
  different namespace, accept the call-site churn and drop the
  `<RootNamespace>` pin then. Until then, the pin is the lowest-cost
  way to maintain stability.
- If `Resources/starter-models.json` ever moves to `shared/` (e.g.
  Runner ever wants to surface starter-model recommendations on
  the chat side), the embedded resource name will change naturally
  with the new owning project; the loader's `EmbeddedCatalogResourceName`
  constant gets updated then, not before.

## 2026-05-06 — Mac UI design language: brand-tinted native (Option C), MAC17 leans pure-native (Option A)

The MAC9 decision locked in Swift/SwiftUI as the long-term Mac UI
architecture but did not settle the *visual* direction. Today's Mac
Runner (`mac-runner/Sources/main.swift`) is stock SwiftUI on dark mode
— zero styling — while the Windows hosts share a locked-in neumorphic
dark theme (`shared/UI/Theme/{Colors,Controls,Theme}.xaml`,
non-negotiable per the existing UI/theme decision). Before MAC17 ships
a second SwiftUI host (`mac-prep-app/`) and bakes a default in for
both apps, settle the cross-platform visual stance.

**Decision:** Mac apps adopt **brand-tinted native** styling
(Option C) — native macOS HIG controls, native dialogs, native sheet
behavior, but with a brand-consistent dark color palette and accent
colors pulled from the shared WPF tokens. **MAC17 specifically leans
closer to pure native** (Option A): destructive disk operations use
unmodified `NSAlert` confirmation sheets, system-default button
chrome on the erase / format affordance, and the OS's standard
disk-permission prompts. The Mac Runner refresh that comes after
MAC17 is the place to cash in the brand tinting in earnest.

**Why brand-tinted native, not full neumorphic port (Option B):**
- A faithful Swift port of `Controls.xaml` / `Theme.xaml` means
  building custom `ButtonStyle`, `TextFieldStyle`, panel surface,
  border, and shadow primitives in SwiftUI to mimic WPF chrome.
  That's a real surface area to maintain — every WPF style change
  becomes drift the Mac side has to chase, and SwiftUI's defaults
  fight back at every step (focus rings, hover states, accessibility
  affordances).
- Native controls are the *only* thing that gives Mac users
  predictable behavior: cmd-comma for prefs, tab focus order,
  VoiceOver, full-keyboard-access, dark-mode auto-tracking,
  Dynamic Type. A custom theme either reimplements all of that or
  silently regresses on it.
- MAC9 explicitly cited "Mac-native niceties Swift gave us free" as
  a reason to keep Swift; throwing those away to chase pixel parity
  with WPF undermines the rationale that justified Swift in the
  first place.

**Why pure native specifically for MAC17:**
- MAC17's PrepApp formats drives. A custom-themed destructive-erase
  confirmation dialog is a trust regression — users (correctly)
  weight unfamiliar UI on a destructive action as a red flag.
  Native `NSAlert` with the standard "destructive" button styling is
  what macOS users have been trained to recognize for fifteen years.
- `diskutil` permission prompts come from the OS regardless of UI
  chrome; surrounding them with a custom-themed shell makes the
  trusted OS prompt feel like an interruption rather than a
  continuation of the flow.
- PrepApp is short-residence software (run once or twice per drive,
  not daily). Brand expression has lower payoff there than in the
  Mac Runner, which is the daily-use surface.

**What "brand-tinted" means concretely:**
- **Accent color:** SwiftUI `tint(.accentColor)` driven by an asset
  catalog `AccentColor` set to the WPF `AccentCyanColor` (#00E5FF)
  for primary actions, with `AccentMagentaColor` (#FF2D92) reserved
  for destructive emphasis where native semantics allow override
  (status pills, progress accents — *not* the actual destructive
  confirmation button, which stays system-default red).
- **Backgrounds / surfaces:** stay native (`.background(.regularMaterial)`,
  `Color(NSColor.windowBackgroundColor)`) — do not hardcode
  `BgBaseColor` (#1A1D24) on every view. Light mode comes free; if
  we lock to dark via Info.plist `NSRequiresAquaSystemAppearance` later
  that's a separate decision.
- **Status colors:** `StatusSuccessColor` / `StatusWarningColor` /
  `StatusDangerColor` from the WPF palette mirror cleanly into
  SwiftUI for inline status indicators (badges, log severity
  glyphs), without touching control chrome.
- **Typography:** native SF Pro at native sizes — no font overrides.
  The brand identity comes from color and the icon, not type.
- **Iconography:** the shared `AppIcon` from MAC10b is already
  brand-consistent across all four hosts; SF Symbols for in-app
  affordances stay native.

**What stays explicitly off-limits on the Mac side:**
- Custom `ButtonStyle` that changes shape, shadow, or padding from
  system defaults.
- Hardcoded background color hex on view containers.
- Font family overrides (SF Pro only).
- Custom focus rings, custom hover states, custom selection
  highlights.
- Custom dialog windows for destructive confirmations — always
  `NSAlert` / SwiftUI `.alert(...)` / SwiftUI `.confirmationDialog`.

**Exit ramp — re-open this decision if any of these become true:**
- A Windows<->Mac switching user reports the visual disconnect is
  bad enough to confuse them about which app they're in (real user
  feedback, not aesthetic preference).
- The Windows neumorphic theme gets reworked toward something
  closer to native — at which point the cross-platform target
  shifts and the Mac side can re-anchor.
- A second non-Apple platform target lands and a single
  cross-platform UI codebase becomes attractive (same exit-ramp
  trigger as MAC9).

**Application order:**
1. MAC17 (mac-prep-app, this PR series) — pure native + the asset
   catalog `AccentColor` and `Status*` color set wired up but used
   sparingly. Establishes the asset catalog convention.
2. Mac Runner refresh (separate future item, not yet on backlog) —
   apply brand tinting in earnest now that the conventions exist.
   That's the right place to revisit `mac-runner/Sources/main.swift`
   wholesale, since today's screen is the rawest stock-SwiftUI
   surface in the project.

## 2026-05-07 — MAC18: cross-platform prep compatibility matrix published

With MAC17 / MAC17a / MAC17b shipped, both Windows and Mac PrepApp
hosts produce drives that are byte-identical at the encrypted-config
and SSD-layout level. MAC18 publishes the source/target/filesystem
matrix in user-facing docs so users know which prep host can produce
which target drive, and so the unsupported cells are recorded as OS
limits rather than project gaps.

**Matrix (locked):**

| Source OS | Target | Filesystem | Supported |
|-----------|--------|------------|-----------|
| Windows | Windows-only | NTFS | yes |
| Windows | Cross-platform | exFAT | yes |
| Windows | Mac-only | exFAT | yes (APFS not available from Windows) |
| Mac | Mac-only | exFAT | yes (APFS deferred from supported targets) |
| Mac | Cross-platform | exFAT | yes |
| Mac | Windows-only | NTFS | not supported — use Windows PrepApp |

**Why these specific cells stay unsupported:**

- *APFS from any source:* Per MAC1, APFS is Mac-native; Windows
  cannot reliably create or write to it. MAC10a's PrepTargets →
  filesystem mapping deliberately collapses Mac-only and
  cross-platform onto exFAT for this reason. MAC17 inherits the
  same mapping in `DiskutilFormatCommand`. APFS support would
  require both a Mac-native prep workflow (which exists post-MAC17)
  and a deliberate decision that exFAT has proven inadequate — no
  evidence of that today, so APFS stays out of supported targets
  rather than being on the roadmap.
- *Mac → Windows-only NTFS:* macOS does not natively format NTFS.
  Third-party drivers exist but introduce a runtime dependency the
  project deliberately avoids (the whole point is "plug in a drive,
  it just works"). Users wanting NTFS-only drives are routed to
  Windows PrepApp explicitly in README + QUICKSTART.

**Encrypted-config bidirectional roundtrip is a published guarantee.**
Drives prepped on Windows unlock cleanly on Mac and vice versa. The
on-disk format (AES-256-GCM + PBKDF2-SHA256) is identical on both
platforms; this is pinned by the cross-language fixture under
`tests/Fixtures/MacEncryptedConfig/` (both `csharp-encrypted/` from
MAC5 and `swift-prep-encrypted/` from MAC17). Either direction
breaking would fail Windows CI.

**Files changed:**
- `README.md` — tagline updated, `Cross-platform PrepApp` feature row
  added, new `Source/Target compatibility` subsection with the matrix
  + bidirectional-roundtrip callout, "What You Need" reframed for
  either host, parallel Mac walkthrough in Phase 1, components
  reference updated to include `mac-prep-app/`, `mac-prep-host/`,
  `mac-runner-host/`, `runner-core/`, `prep-core/`.
- `docs/QUICKSTART.txt` — 5-step quick-start reframed for either host,
  matrix added as its own block, filesystem note rewritten around
  "filesystem comes from your target choice".
- `agent_docs/project_decisions.md` — this entry.

**Out of scope (held the line):**
- Removing the "macOS beta" framing from Runner-side docs — that's
  MAC15's job and depends on MAC11 (signing + notarization) landing
  first. MAC18 only adds the prep matrix; the Runner-side beta
  caveats stay as-is.
- Release notes — v1.2.9 (2026-04-19) was the last release, pre-MAC17.
  The next release will pick up MAC17/17a/17b/18 together; release
  notes drafted at MAC11 / signed-beta cut, not now.
- Mac PrepApp screenshots — deferred to MAC15 + a real-Mac smoke
  pass.

**Exit ramp:** if exFAT proves inadequate for a real Mac use case,
re-open the APFS-target decision; the matrix gains a new row rather
than a footnote. If Mac-side NTFS becomes feasible without a runtime
dependency (e.g., Apple ships native NTFS write support), the
"Mac → Windows-only NTFS" cell flips to supported and the docs
update accordingly.

## 2026-05-07 — Cross-OS parity audit is mandatory after every single-OS task

Free-AI-SSD ships on Windows + Mac with shared cores (`runner-core/`,
`prep-core/`, `shared/`) consumed by per-OS host adapters (WPF on
Windows, SwiftUI on Mac, with `mac-runner-host` / `mac-prep-host`
sidecars bridging Swift to the .NET cores). Single-OS surfaces (UI
hosts, platform adapters like `IDriveService`, packaging metadata,
docs) can drift silently when work lands on only one OS.

User established the rule explicitly 2026-05-07 after MAC18 wrapped
up the cross-platform PrepApp parity track: *"from now on when one
os gets work done it needs to also get looked at on the other os
and see if the work needs to be done there as well i dont want to
miss things on os to os."* User strengthened it later the same day
after F2 surfaced a Mac UI parity gap that a planning-phase review
would have caught earlier: *"going forward we need to ensure all
tasks have a dual os review pass to ensure we are touching all
things we need or setting up a follow on task if needed."*

**Decision (workflow rule, applies to all future work):**

Every task plan (execution prompt, ad-hoc plan, pre-coding sketch)
must include an explicit **Dual-OS review pass** during planning,
not just an audit after merge. The pass:
- Enumerates surfaces touched on Windows (WPF runner / PrepApp /
  Companion) and Mac (Swift runner / Swift PrepApp / sidecars).
- Picks one outcome: **bundle** both OSes in the same PR, **split**
  (ship one platform first; file the second-platform follow-up
  before merging the first), or **single-OS** with a one-line
  justification for why the other platform is unaffected.
- Lives in the execution prompt so future agents reading it cold
  see the check happened. Convention: a `## Dual-OS review pass`
  section near the top, right after `## Goal` / `## Why now`.

After completing the task, still flag the audit outcome in the
user-facing summary ("checked Windows side — no mirror needed
because X") so the user sees the planned decision held up against
implementation reality.

**Two execution patterns are both acceptable.** Pick whichever fits
the change shape:

1. **Audit-after-merge with focused single-OS PRs (default).** Land
   the task on one OS, merge it, then do the cross-OS audit. If
   work is needed on the other OS, file a follow-up PR. Matches
   the existing MAC17 → MAC17a → MAC17b cascade pattern. Best when:
   - The change is large enough that mixing platforms in one PR
     hurts review.
   - The platforms have meaningfully different host concerns
     (e.g., WPF threading vs Swift strict-concurrency).
   - One platform's CI failure shouldn't block the other.

2. **Bundle both OSes per task.** Single PR adds the shared-core
   change plus both platform adapters / UI wirings. Best when:
   - The change is naturally dual-platform (e.g., a shared-core
     service consumed by both hosts, where each host's wiring is
     small).
   - Splitting would create an awkward intermediate state on `main`
     where the shared-core service exists but only one platform
     uses it.

**How to choose for a given task:**
- If a shared-core change is needed AND both per-host UI surfaces
  are small (~≤30 lines each), default to bundle.
- If shared-core is unchanged and only one platform is touched,
  default to audit-after-merge.
- If shared-core is changed but one platform's wiring is large or
  has design questions, split: ship the shared-core + simpler
  platform first; do the larger platform as a follow-up.

The split is a real escape hatch, not a default for "any time the
Mac surface is bigger." First case where this gets exercised
seriously: F2 (Live model list fetch) — execution prompt at
`agent_docs/f2_execution_prompt.md` documents the bundle-default,
F2a-followup-fallback decision shape.

**How to surface the audit result:**
- Always flag the audit in the user-facing summary, even when no
  work is needed ("checked Windows side — no mirror needed because
  X"), so the user sees the check happened.
- Track parity gaps in whichever backlog they belong to:
  `agent_docs/mac_project_backlog.md` for Mac items,
  `agent_docs/project_backlog.md` for general / Windows.

**Why this is in `project_decisions.md` and not just user memory:**
The rule is load-bearing for all future work. User memory at
`~/.claude/projects/-Users-stephenelswick-Free-AI-SSD/memory/feedback_cross_os_parity_audit.md`
captures it for sessions on the user's machine, but
`project_decisions.md` is the project-facing capture so any agent
(human or otherwise) reading the repo cold sees the rule.

**Exit ramp:** if Free-AI-SSD ever drops back to a single supported
OS (e.g., a hypothetical Mac-only fork), this rule becomes
trivially satisfied and can be retired. As long as both OSes are
actively supported, the rule stands.


---

## 2026-05-07 — F2: live catalog source = Ollama HTML scrape with bundled fallback

PrepApp's Refresh button (F2) fetches the live starter-model catalog
by HTML-scraping `https://ollama.com/library`. HuggingFace's
`/api/models?filter=gguf` was the obvious clean-API alternative
but was rejected: HF returns model IDs like
`bartowski/Meta-Llama-3.1-8B-Instruct-GGUF` which are not
Ollama-pullable tags, and PrepApp's whole purpose is to surface
tags the user can hand to `ollama pull`.

**Source verification (2026-05-07):**
- `ollama.com/library` returns HTML only — no JSON endpoint, no
  `__NEXT_DATA__` blob.
- `registry.ollama.ai/v2/library/<model>/tags/list` returns 404
  for unauthenticated requests.
- HF `/api/models` is a clean JSON API but wrong-shape data.

**Why HTML-scraping is acceptable here:**
- Ollama's library page exposes `x-test-*` attributes
  (`x-test-model`, `x-test-model-title`, `x-test-size`,
  `x-test-capability`, `x-test-pull-count`) clearly designed as
  test selectors. These should be more stable than CSS class names
  under refactors.
- The failure mode is graceful: typed `LiveCatalogFetchException`
  with a `SchemaDrift` reason fires when the parser finds zero
  cards. Both Windows and Mac UI catch it and keep the existing
  bundled list in place; the user sees a status caption explaining
  the failure rather than a modal error.
- A captured HTML fixture at
  `tests/Fixtures/OllamaLibrary/2026-05-07-snapshot.html` pins
  the parser against a known page version. When Ollama redesigns,
  the test fails loudly (zero matches → SchemaDrift) and we
  refresh the fixture + selectors as a focused follow-up.

**URL allowlist:** `LiveModelCatalogService.AllowedSources` is a
code constant. Adding a new source requires a PR review. HTTPS-only,
checked before the request leaves the process so CI grep can audit
the network surface. Mirrors `OllamaPackageTrustPolicy` posture.

**Exit ramps:**
1. If Ollama publishes a real list-API JSON endpoint, swap the
   parser for a clean JSON path — the `StarterModelCatalog`
   shape stays the same, only the parsing layer changes.
2. If a HuggingFace → Ollama-tag translation layer becomes
   tractable (e.g., HF starts publishing Ollama-pullable mappings),
   add HF as a second source behind the same interface.
3. If scraping breaks faster than we can patch (>1x per quarter),
   degrade to bundled-only and remove the live path.

---

## 2026-05-08 — MAC26: Mac Ollama runtime spawns inner `Ollama.app/Contents/Resources/ollama` directly; bypass LaunchServices

**Driver.** v1.3.6 mac field test (post-MAC25 ship) failed first
model pull. Direct SSD inspection: zero blobs anywhere on the
system, sidecar hung 18 minutes in `await`, host log stopped
cleanly at the prereqs step, exit 137 (= SIGKILL) on qwen2.5:7b.
Root cause turned out to be that the staged Mac Ollama binary is
the 119 KB CLI shim from the macOS desktop distribution, not a
self-contained server.

**Decision.** On Mac, the project runs the **inner self-contained
Go server** at `Ollama.app/Contents/Resources/ollama` (53 MB,
Developer ID signed by Infra Technologies, identifier=`ollama`,
supports `serve`/`pull`/`run` like Linux/Windows) directly as a
child process of `mac-prep-host`. The 119 KB top-level CLI shim
and the LaunchServices fallback path are abandoned. Staging keeps
`Ollama.app/` intact (no copy out, no rewrite) — preserves Apple's
code signing and the adjacent `lib/ollama/runners/` directory the
server needs at inference time.

**Why.** Three structural problems with the macOS Ollama
distribution forced this:

1. **macOS Ollama ships as a GUI desktop app, not a CLI server.**
   `ollama-darwin.zip` is the Electron-based desktop bundle.
   Linux/Windows ship a single self-contained CLI server binary;
   macOS does not. The closest equivalent is buried inside
   `Ollama.app/Contents/Resources/`.

2. **LaunchServices launches GUI apps in a clean environment.**
   The 119 KB shim's documented fallback is to launch `Ollama.app`
   via LaunchServices when no daemon is up. LaunchServices does
   not propagate env vars from the calling process. So
   `OLLAMA_MODELS=/Volumes/FREEAI/models` set by
   `ModelOperations.PullModelAsync` never reaches the spawned
   daemon — even when a pull would succeed, models would land in
   `Ollama.app`'s default location, never on the SSD.

3. **Headlessly-launched `Ollama.app` from `/Volumes/FREEAI/...`
   is SIGKILL-prone.** Field test exit code 137 (= signal 9) on
   qwen2.5:7b is consistent with macOS killing the
   headlessly-spawned GUI daemon (TCC, jetsam under memory
   pressure, signing/quarantine quirks on the unusual SSD path).

Direct child-process spawning (not via LaunchServices) inherits
env naturally and avoids all three failure modes. The inner Go
server is itself Developer ID signed and self-runnable.

**Constraint locked.** Per user product call 2026-05-08: must not
require user-managed Ollama install. The project bundles Ollama
and the runtime fix has to run a self-contained server. Rules out
the alternate "stop bundling, document manual install" path.

**Exit ramps.** Re-open if any of:

1. Ollama publishes a standalone darwin CLI server package without
   the GUI wrapper — switch to that and drop the inner-bundle path.
2. The inner server binary changes its runtime expectations (e.g.
   requires the Electron parent for an operation hit at runtime).
3. Apple changes signing / TCC / quarantine policy in a way that
   breaks running GUI-bundle inner binaries as headless children.

**Implementation owners.** MAC26 backlog entry in
`mac_project_backlog.md` carries the file-by-file plan, cross-OS
review, and acceptance criteria. Kickoff verification (manual
one-line CLI test of the inner server pulling against
`OLLAMA_MODELS`) gates the design before any code lands.

**Supersedes.** No prior decision — first explicit lock on Mac
Ollama runtime architecture.

## 2026-05-08 — Encryption is opt-in (default OFF) on both PrepApps

**Decision.** SSD config encryption becomes opt-in across Windows
and Mac PrepApp. The toggle is visible on the encryption setup
step, defaulted to OFF, framed as an optional security upgrade
rather than a gate. A user who taps "Continue without encryption"
proceeds to model pull without any passphrase friction. The
plaintext path writes `<root>/config/portable-config.json`
directly; the encrypted path writes
`<root>/config/portable-config.encrypted.json` as before. **MAC30
is the implementation issue.**

**Why.** Field pushback from v1.3.5 (carried through v1.3.9):
"this version forces you to encrypt. you cant pull the models
unless you set an encryption password. that shouldnt be forced."
The MAC17a-#6 stance ("plaintext-mode prep is out of scope") was
made for engineering tractability — the `!enableEncryption`
codepath threw `failed` because the plaintext writer was missing
on Mac. That's a fixable engineering gap, not a product
constraint.

**The narrowed invariant.** Pre-MAC30, MAC5 said "no plaintext
config containing secrets ever written." Post-MAC30, the
invariant tightens to "**the API key is never written in
plaintext**" — narrower and more defensible. The existing
`shared/PortableConfig.cs:275`
`NetworkModeEncryptionRequiredMessage` guard already enforces
this: enabling Network Mode + Require API Key on a plaintext
config throws at save time. So Companion-on-LAN remains an
encrypted-config feature; everything else (local chat, RAG, DCS
bindings, voice) is fine on plaintext.

**Threat model the user accepts on plaintext.** A lost or
stolen unencrypted SSD reveals: model list with hashes, document
library metadata, PTT keybinds, UI preferences. It does NOT
reveal: API keys (guard above), document content (lives in
`docs/` and `models/blobs/`, addressed separately). For most
single-user offline workflows this is an acceptable posture.
Users who plug into a multi-user PC or carry the SSD across
trust boundaries are exactly who the encryption upgrade is for
— and the explainer text on the toggle should say so.

**Cross-OS scope.** Both PrepApps. Windows kept its toggle
through MAC17a; Mac lost it. MAC30 restores Mac and flips both
defaults to OFF in the same PR per the 2026-05-07 dual-OS rule.

**Exit ramps.** Re-open if any of:

1. Field testing reveals a leak vector through plaintext config
   the threat model above doesn't cover (e.g. cached prompt
   metadata containing user secrets).
2. Network Mode usage rises high enough that "Companion needs
   encryption" becomes a major friction — at which point we'd
   either (a) auto-prompt for encryption on the first Network
   Mode toggle, or (b) move the API key out of the encrypted
   config to a separate keychain-backed store.
3. A regulatory or compliance posture lands that requires
   encryption-at-rest for offline AI deployments — flip the
   default back to ON.

**Implementation owners.** MAC30 backlog entry in
`mac_project_backlog.md` carries the file-by-file plan, the
threat-model framing for the user-facing explainer, and
acceptance criteria.

**Supersedes.** Replaces the MAC17a-#6 stance ("plaintext-mode
prep is out of scope"). MAC5's plaintext invariant narrows from
"no plaintext config" to "no plaintext API key".

## 2026-05-08 — Disk-truth is the canonical source for installed-model state on the SSD

**Decision.** `<ssdRoot>/models/manifests/registry.ollama.ai/library/<model>/<tag>`
is the source of truth for "what models are installed". Both runner-core C#
(`ModelManagementService.GetInstalledModelNames`, `GetModelSizingWarnings`,
`RunnerLocalApiService` `/models` endpoint) and the Mac SwiftUI runner
(`mac-runner/Sources/main.swift` `applyConfigToUi`) enumerate the manifest
tree directly via `ModelOperations.DiscoverModelsOnDisk`. `config.Models` is
no longer the canonical filter for installed status. Any new consumer asking
"what's installed?" must read disk truth, not config, and must accept
`ssdRoot` (or capture it on a service ctor) to do so.

**Why.** The Mac PrepApp sidecar pulls models against an unlocked encrypted
config, but the unlock material is zeroized before the long-running pull runs
(security posture: minimize key residency). Re-deriving the key per pull
would be expensive and would touch the encryption hot path. The result is
`config.Models` stays empty post-pull on Mac even when the blob is on disk.
MAC29 fixed `ReadinessService` by reading disk; MAC33 then surfaced three
more C# consumers and one Swift consumer that had to make the same swap.
Fixing at every read site means the sidecar never needs to write back, which
keeps the MAC5 plaintext-config invariant simple and the encryption code
path narrow.

**Cross-language symmetry required.** The Mac SwiftUI runner reads
`config["models"]` directly from the in-memory dict — *not* via the LAN
endpoint. Any future consumer added on the Swift side has the same
obligation: enumerate `<ssdRoot>/models/manifests/...` directly rather than
filter the dict. Verify with a kickoff grep before locking the architecture
on any future source-of-truth swap.

**Exit ramps.** Re-open if any of:

1. Profiling shows disk enumeration is a meaningful read-path cost (almost
   certainly isn't — it's small directory walks) and a cache or writeback
   becomes warranted.
2. A future feature legitimately needs config-pinned status that disk can't
   represent (e.g. "user uninstalled this in settings, hide it from the
   picker"). Add a layered model: disk gives the universe, config-pinned
   status filters out user-hidden entries.
3. MAC30 ships and the encryption-write hot path becomes trivial enough that
   opportunistic config writeback at unlock time becomes worth restoring.

**Implementation reference.** PR #215 (MAC29, `ReadinessService`) +
PR #218 (MAC33, runner-core + Swift). Helper:
`prep-core/ModelOperations.cs:103` `DiscoverModelsOnDisk`.

---

## 2026-05-08 — Mac Runner sidecar auto-spawns at unlock; "Network Mode" toggle is LAN-exposure only [MAC34]

Pre-MAC34, the `mac-runner-host` sidecar started/stopped in lockstep with
the user-facing "Network Mode (LAN API)" toggle. Because Mac chat
architecturally routes through that sidecar (the C# RAG pipeline lives
there, not in Swift), local-only chat falsely required toggling Network
Mode on — and once toggled on, the auth gate 503'd because PrepApp shipped
`networkApiKey: ""`.

**Decision (locked):** the sidecar is now part of the unlocked-session
lifecycle on Mac. It auto-spawns at unlock (`ensureLocalChatStackRunning`
in `mac-runner/Sources/main.swift`, called from both `attemptUnlock`
success and `loadConfig` plaintext path), bound to 127.0.0.1 by default,
and tears down at lock before the unlock material is zeroized. The
toggle's role narrows to runtime bind-address control: OFF forces
loopback regardless of what `networkBindAddress` says in config; ON uses
the configured `networkBindAddress` (still defaults to 127.0.0.1, so
toggling ON with default config is currently a no-op — actual LAN exposure
requires editing the JSON, same as Windows per the existing TODO at
`runner/MainWindow.xaml.cs:2176`). UI label changes from "Network Mode
(LAN API)" to "Expose API on LAN".

The persisted PortableConfig field stays `networkModeEnabled` for
cross-OS schema compatibility; only the Mac runtime semantics shifted.
Windows Runner runs runner-core in-process and is unaffected.

The Start/Stop buttons in the Mac Runner UI were removed because ollama
auto-starts at unlock and stops at lock. Manual control would re-introduce
the bug shape this decision exists to close.

Won't be revisited unless we move RAG to a Swift implementation (would
remove the architectural reason for a sidecar at all).

---

## 2026-05-08 — Runtime API-key backfill in Mac Runner over loopback bypass in runner-core [MAC34]

While fixing the v1.3.12 field-test 503 ("API key is required by
configuration but not set on host"), considered two paths to make local
chat work despite an empty `networkApiKey`:

**Path A (rejected): loopback bypass in `RunnerLocalApiService`.** Skip
the API-key check when `context.Connection.RemoteIpAddress` is loopback.
Auth on 127.0.0.1 is trivially defeatable by any process already on the
machine, so the bypass *is* defensible in isolation — but grep showed
the existing `ApiKeyEnforcement_BlocksChatWithoutKey` test exercises a
real loopback connection and would silently start passing under the
bypass. Drafted, then reverted.

**Path B (accepted): runtime backfill in Mac Runner.** Generate a fresh
32-byte hex key inline in `restartHostSidecar` when the unlocked config's
`networkApiKey` is empty, mirror it into the in-memory `portableConfig`
(so `apiKeyForLocalApiRequest()` agrees with the sidecar), and persist it
via `saveConfig` so subsequent unlocks reuse the same key. Cross-OS PrepApp
generation (Mac `EncryptedConfigWriter`, Windows `PrepViewModel.FinalizeAsync`)
ensures *future* SSDs have the key from first prep; the runtime backfill
heals legacy v1.3.12-prepped SSDs that already shipped with empty keys.

**Why Path B wins:**
- Auth posture stays uniform — every connection still verifies the key,
  whether loopback or LAN. Future changes to the auth gate don't have to
  re-reason about "when does loopback skip apply."
- No test churn — `ApiKeyEnforcement_BlocksChatWithoutKey` (and the other
  auth tests) pin real behavior on real loopback connections, and they
  keep working because nothing in `RunnerLocalApiService` changed.
- Legacy SSDs self-heal without a config-format migration.
- The cross-OS PrepApp generation is a clean parallel fix on the
  Windows side that closes a latent Windows bug too (Windows users who
  enabled Network Mode by hand-editing JSON would have hit the same 503).

Won't be revisited unless we move auth to a transport-level mechanism
(mTLS) where address-based gates become natural.

---

## 2026-05-09 — Cross-OS parity rule applies to user-visible behavior, not implementation shape [MAC32]

The 2026-05-07 cross-OS parity rule (every single-OS task gets a
dual-OS audit pass) does NOT mandate identical implementation
shapes across OSes. MAC32 shipped a Mac `.done` step with a Quit
button on one side and a Windows `ShowInfo` modal on the other —
because Mac PrepApp is a step-machine SwiftUI flow with a natural
terminal step, while Windows PrepApp is a tabbed XAML UI with no
step machine. Mirroring would have meant either re-architecting
Windows around steps or building a fake terminal-page overlay; both
were larger surfaces for no user-visible benefit.

What stays identical across OSes: the user-visible *message*. Both
surfaces say "Your SSD is ready. Open Runner from this SSD to start
chatting" — so cross-OS docs cite one phrase and field-test reports
match across machines.

Rule of thumb: when the parity audit surfaces a gap, the question is
"does the user feel the same gap on both OSes?" — not "does the
codepath look identical." If the answer is yes-on-Mac-no-on-Windows
(or vice versa), only the gap-side ships code; the other side gets
documented as already-correct in the PR description and a recon
note. **MAC31a Windows side** (verified already correct because
`CancelOperation` already returns the user to the Models tab where
MAC31's resume seed populates "Resuming…") is a parallel example
from the same PR.

Established PR #225 (`179dfc0`).

---

## 2026-05-09 — Mac sidecar handshake hardcodes `networkModeEnabled = true`; LAN exposure is governed purely by `networkBindAddress` [MAC34a]

After MAC34 the Swift comment said "the toggle now controls bind
address only," but `restartHostSidecar` still passed the toggle's
runtime value as the `networkModeEnabled` field of the C# handshake.
That made the C# `RunnerLocalApiService.StartAsync` early-return at
`if (!config.NetworkModeEnabled) return;` whenever the toggle was
OFF, so the Mac sidecar refused to start on every unlock — chat
dead until the user manually toggled it ON.

The contract is now: Swift always sends `networkModeEnabled = true`
in the handshake. The toggle's runtime effect is a single dimension
— the bind address. Loopback when the toggle is OFF, the configured
`networkBindAddress` when ON. The C# inner gate stays in the shared
code path as defense-in-depth (Windows pre-gates externally in
`MainWindow.xaml.cs:470` so it's also never reached there with
false). Persisted `networkModeEnabled` in PortableConfig still
reflects user intent across sessions and across OSes — only the
runtime semantics decoupled from it.

Why not the alternative — change C# to ignore `networkModeEnabled`
on the Mac sidecar specifically: `RunnerLocalApiService` is shared
between Mac and Windows; introducing a Mac-only branch would split
the contract for one downstream consumer. Hardcoding true at the
Swift→C# boundary is one line of code, preserves the C# contract,
and keeps the existing `HostRunner_WithNetworkModeDisabled_FailsWithoutReadyLine`
smoke valid as defense-in-depth.

Established PR #226 (`95b62b5`).

---

## 2026-05-09 — Mac runner reclaims port 11434 by killing PIDs holding it, not by name-matching ollama processes [MAC34b]

Field test of v1.3.13 surfaced silent `Ollama exited with code 1`
followed by chat-host crashes when Ollama.app + a stray CLI ollama
already held 127.0.0.1:11434. Windows side-steps the same scenario
via `OllamaLifecycleService.ResolvePort` (scans preferred+20). Mac
can't port-shift because the C# sidecar handshake takes a fixed
host URL passed in from Swift, so reclaiming the port via process
termination fits Mac's lifecycle better.

The kill scope is "what is holding *our* port," not "anything
named ollama." Implementation runs
`lsof -nP -t -iTCP:<port> -sTCP:LISTEN` to enumerate PIDs, then
SIGTERM → 0.6s grace → SIGKILL anything still alive. This means:
- A sibling ollama serving a different model on a different port
  (a parallel-workflow case) is left alone.
- A non-ollama process accidentally holding 11434 still gets freed
  up — the policy is "we own the staged port," not "we're polite
  to ollama specifically."
- Logs the reclaimed PIDs so the user has visibility (the user
  reported "I had two running and had no idea" before the fix).

Won't be revisited unless we move Mac to port-shifting (would
require teaching the C# sidecar handshake to accept a
runtime-resolved host URL — much larger surface for the same
field-test outcome).

Established PR #227 (merge hash pending v1.3.14 dispatch).

---

## 2026-05-09 — Mac model pulls stage to host APFS, then merge sequentially to the SSD; Windows stays direct-to-SSD [MAC35]

Field test of v1.3.14 pulling `qwen2.5:7b` (4.7 GB on a 1 Gb line)
collapsed to ~5 MB/s on exFAT — 290 stall events over 19 minutes,
Ollama UI bouncing 35-60 % → 6 %. Direct Ollama on Windows over
the same connection downloads fine. Root cause confirmed in
`<ssdRoot>/logs/macos-prep-host-20260509.log`: Ollama 0.5.7
hardcodes `numDownloadParts = 16` (verified upstream
`envconfig/config.go`, May 2026 — no env var override) and exFAT
FSKit on macOS 15+ cannot sustain 16 concurrent writers on a
single blob. Chunks make local progress but Ollama's per-chunk
byte-progress detector trips, kills and restarts the chunk, and
the displayed percentage drops back to that chunk's restart point.

Mac model pulls now stage into
`~/Library/Caches/FreeAiSsd/ollama-staging` (host APFS — no exFAT
contention) and then sequentially copy the manifest + referenced
blobs to the SSD via `prep-core/OllamaModelStager.MergeToSsdAsync`.
The merge is content-addressed (skip-if-size-match is the
idempotent-retry path), per-blob atomic (tmp-then-rename so dest
never holds partial bytes), and manifest-written-last (a torn
merge is invisible to `DiscoverModelsOnDisk`, which enumerates
manifests). Cancel during the merge cleans up the in-flight tmp
file before re-throw. The same pull-CTS wraps both phases so a
user cancel tears down cleanly regardless of which phase is in
flight. A 2x-model-size disk-space precheck (5 GB floor for
unknown sizes via `ModelSizingCatalog.Suggest`) refuses to start
when the staging volume can't fit the pull, surfacing a clear
error before the network round-trip.

Windows path is **untouched** — NTFS sustains 16 parallel writers,
so routing Windows pulls through a host-stage step would cost
extra disk space without solving any observed problem. The
asymmetry follows the same shape as MAC34b's `lsof`-vs-port-shift
split: implementation diverges by platform constraint, user-visible
outcome converges.

Source-of-truth for installed models stays disk-truth on the SSD
(MAC33 invariant preserved). The merge writes byte-identical
layout to what a direct pull would produce — same manifest path,
same `sha256-<hex>` blob filenames, same sizes — so
`DiscoverModelsOnDisk` and the runner-side disk-truth read at
`RunnerLocalApiService:160` continue to work without any awareness
of the staging detour.

Why not the alternatives:
- **Fork Ollama to expose `numDownloadParts` as an env var.**
  Larger surface to maintain per upstream version; the bundled
  CLI would diverge from upstream behavior; users already on the
  field would still hit the bug until they updated.
- **Stage runner-side pulls through the same path
  (`PullEmbeddingModelAsync`).** Deferred from this PR. The Mac
  runner's HTTP `/api/pull` hits the long-running daemon;
  restaging would require restarting the daemon mid-chat or
  running a parallel temp daemon. The embedding model is ~270 MB
  so the user-visible cost of deferral is bounded (~1 min worst
  case at 5 MB/s). Filed as a follow-up if the pathology actually
  surfaces.
- **Single-chunk-mode flag at the Ollama HTTP level.** No such
  flag exists in 0.5.7; we'd be back to forking.

Ships unrevisited unless: (a) Ollama upstream adds a parallelism
knob; (b) macOS gains a non-exFAT filesystem usable across
Win+Mac that the user is willing to format the SSD as; (c) the
staging-cache disk usage becomes a user complaint and we need to
add a post-merge cleanup pass.

Established PR #229 (`0eecceb`), shipped v1.3.15.

---

## 2026-05-09 — Code that enumerates Ollama manifest/blob files on macOS-prepped SSDs must filter AppleDouble companion files (`._*`) [MAC35a]

macOS auto-creates AppleDouble companion files (`._<name>`) next to
any file with extended attributes when the destination filesystem
lacks native xattr support. exFAT — the only filesystem usable
cross-platform Win+Mac and therefore the default for our SSDs —
qualifies. The companion file's first byte is the AppleDouble magic
0x00 0x05 0x16 0x07, so handing one to `JsonDocument.Parse` raises
"'0x00' is an invalid start of a value. LineNumber: 0".

The v1.3.15 mac field test reproduced this end-to-end: a clean
MAC35 stage+merge of `qwen2.5:7b` left a valid 858-byte `7b`
manifest on the SSD AND a 4 KB `._7b` AppleDouble sidecar that the
kernel injected during the merge. `DiscoverModelsOnDisk`'s
wildcard enumeration picked up both, `FindModelBlobForModel`
resolved to the sidecar, and the unhandled parse aborted readiness.

The permanent rule: any code that enumerates files under
`models/manifests/` or `models/blobs/` on a macOS-prepped SSD must
filter out leaves starting with `._`. Ollama tag and name segments
cannot legally start with `.`, so the filter can never false-skip
a real manifest or blob. JSON parsers reading manifest contents
should additionally wrap their `JsonDocument.Parse` call in
try/catch so any future malformed manifest produces a clean
failure rather than aborting the calling command — mirrors the
catch already in `EstimatePartialProgress`.

Why not the alternatives:
- **Strip xattrs at write time** in `OllamaModelStager.MergeToSsdAsync`
  via `xattr -c` after rename. Defensive but doesn't cover xattrs
  other code paths might add (Quarantine, Spotlight metadata,
  Finder tags). Filtering at read time is the universal fix.
- **Use `dot_clean`** to scrub the SSD periodically. Indirect, racey,
  and adds a moving part during prep.
- **Format the SSD as APFS or NTFS.** APFS is Mac-only; NTFS isn't
  natively writable on macOS without third-party drivers. exFAT
  remains the only cross-platform option; `._*` files are a fact
  of life on it.

Windows path is unaffected — NTFS supports xattrs natively, so
AppleDouble companion files never appear there. The filter is
harmless on Windows because no legitimate Ollama tag starts with
`.`.

Established PR #231 (`8f3b54e`), shipped v1.3.16.

---

## 2026-05-09 — Plaintext invariant narrows from "no plaintext config containing secrets" to "API key never written in plaintext" [MAC30]

Pre-MAC30 (MAC5/MAC17a era): the rule was no plaintext config of
any kind. MAC30 introduced an opt-out path; with the toggle OFF
the SSD must still be safe to leave in someone else's hands. The
narrowing is: `PortableConfig` may now be plaintext as long as
`networkApiKey` is empty. Enforced at three sites:

1. `mac-prep-app/Sources/PlaintextConfigWriter.swift` — strips
   `networkApiKey` from the dictionary before
   `JSONSerialization.data(...)` even if
   `InitialPortableConfigPayload` generated one in memory. The
   in-memory payload still generates a random key per MAC34 (so
   the encrypted path stays correct), but the plaintext writer
   refuses to land it on disk.
2. `shared/PortableConfig.cs:275` `NetworkModeEncryptionRequiredMessage`
   — fires at save time if Network Mode + Require API Key +
   plaintext config combine. Both `ConfigStore.SaveAsync` and
   `PortableConfig.SaveAsync` honor this guard.
3. Mac Runner's MAC34 runtime API-key backfill only triggers on
   the encrypted unlock path; a plaintext config that turns
   Network Mode on hits the save-time guard before the runner
   ever reaches the backfill site.

Companion-on-LAN remains an encrypted-config feature. Plaintext
is for local-only chat: loopback chat works with no key required
because `RunnerLocalApiService`'s loopback bypass (also MAC34)
allows requests from `127.0.0.1` through unauthenticated.

Why not the alternatives:
- **Refuse to write plaintext at all** — that's the pre-MAC30
  rule, and it's why the user pushed back on encryption being
  forced. The narrower invariant is what makes opt-out viable.
- **Encrypt only the `networkApiKey` field inside an otherwise
  plaintext config** — adds a second key-derivation surface and
  blurs the "encrypted blob is the entire config" contract MAC5
  is built on. Not worth the complexity for one field.

Established PR #233 (`0685cfd`), shipped v1.3.17.

---

## 2026-05-09 — Mac Runner: lock-on-blur removed as default [MAC36]

The MAC5-era `NSApplication.willResignActiveNotification` →
`lockSession(reason: "App backgrounded")` observer is no longer
registered. With MAC30 making encryption opt-in (default OFF), a
plaintext SSD has no key to zeroize on backgrounding and the
auto-teardown forced the user to re-select the SSD on every alt-tab
just to get the chat host back up. The v1.3.17 mac field test
signal was unanimous.

What stays:
1. **Lock-on-quit** (`willTerminateNotification`) — derived AES key
   never outlives the process, encrypted or plaintext.
2. **Manual Lock button** — user-initiated zeroize remains the
   first-class teardown.
3. **Lock-on-deinit** — safety net for view-model release.

Supersedes the lock-on-background bullet of the MAC5 invariant
("derived AES key never outlives the user's active session"). The
spirit of the invariant survives — quit + manual + deinit — but
"active session" is now scoped to the process lifetime, not to the
focus state of the Runner window.

Users who *do* enable encryption and want lock-on-idle will get an
explicit opt-in preference as part of F4 (FTUE Stage 2+). That
preference will register the observer back when the toggle is on.

Why not the alternatives:
- **Keep blur-lock and ask users to disable it** — adds a setting
  to make the default less hostile, which inverts the
  default-should-be-fine principle. The setting belongs to the
  encrypted-and-paranoid case, not the default case.
- **Lock on blur only when encrypted** — encryption is a per-prep
  toggle, not a runtime mode, and reading the on-disk encryption
  state from the Runner each time the focus changes adds a fragile
  coupling. The opt-in F4 preference is the cleaner shape.

Established PR #235 (`567f49a`), shipped v1.3.18.

---

## 2026-05-09 — Mac streaming consumers use URLSessionDataDelegate, not bytes(for:) [MAC36]

The `mac-runner/` and `mac-prep-app/` deployment target is
`arm64-apple-macos11.0` (pinned in `.github/workflows/build.yml` —
`-target` flag at lines 168, 175, 284, 392, 516; SsdEncryptionTests
header documents it as the MAC1 baseline). `URLSession.bytes(for:)`
and `URLSession.bytes(from:)` are macOS 12+ and will not compile on
this target. All Mac streaming consumers must use
`URLSessionDataDelegate.urlSession(_:dataTask:didReceive:)` with a
buffered line-splitter.

Existing references:
- `mac-runner/Sources/NdjsonFrameBuffer.swift` (MAC36) — pure
  helper for `\n`-terminated NDJSON frames; CRLF-tolerant; carries
  trailing tail across chunks. The canonical pattern for any new
  streaming consumer.
- `mac-runner/Sources/main.swift` `handleNdjsonProgress` — older
  buffered-then-replay path for `/api/library/{id}/files` and
  sweep/rebuild progress. Acceptable when live progress isn't
  needed; new code that needs token-level streaming should use the
  delegate path.

Per-call `URLSession` instances paired with the delegate must be
invalidated (`finishTasksAndInvalidate()` on completion or
`invalidateAndCancel()` on teardown) to break the
URLSession-retains-delegate-strongly cycle. The delegate itself
holds a `weak var owner` to the view-model.

This decision unlocks if (a) the macOS deployment target bumps to
12+ — at which point `bytes(for:)` becomes available and is
preferred for new code — or (b) Mac drops streaming entirely. Until
then, the delegate path is the only supported shape and is locked
into the project.

Established PR #235 (`567f49a`), shipped v1.3.18.

## 2026-05-09 — Bundled Ollama version is no longer pinned; resolved per-build from upstream `sha256sum.txt` [MAC38]

MAC4 (2026-05-05) pinned the bundled Ollama to `v0.5.7` because the
previous "resolve `releases/latest`" path drifted from the static
`OllamaPackageTrustPolicy.PinnedMetadataByUrl` dictionary on every
upstream release, causing PrepApp staging to refuse the bundle.
That pin caused a new failure mode in the v1.3.18 mac field test:
`pull model manifest: 412: The model you are attempting to pull
requires a newer version of Ollama` when staging
`deepseek-r1:8b` — the model's manifest schema had moved past
`v0.5.7`'s CLI. There's no environment-variable workaround;
`numDownloadParts = 16` is hardcoded upstream and any newer Ollama
would have the same bottleneck on Mac, which MAC35 already
addresses with host-staging.

MAC38 fixes the failure mode without falling back to the static
pin: drop the static `Default*Package` records and the
`PinnedMetadataByUrl` dictionary entirely, and move the trust
anchor to the on-SSD attestation file.

**New trust chain (Mac):**
1. CI's `FreeAiSsd.PrereqFetch` (`tools/FreeAiSsd.PrereqFetch/Program.cs`)
   calls `PrereqResolver.ResolveLatestOllamaMacAsync` against
   `https://api.github.com/repos/ollama/ollama/releases/latest`,
   picks `Ollama-darwin.zip` (lowercase fallback for older
   releases), and fetches the release's `sha256sum.txt` asset.
2. `DownloadAndVerifyAsync` downloads + verifies the bytes against
   the vendor SHA-256. The resolved version, source URL, and SHA
   are written into the bundled `mac-tools-manifest.json`.
3. PrepApp `ArtifactStagingService.StageMacOllamaAsync` reads the
   bundled manifest and uses *its* hash as the staging-time gate
   (no static lookup). On match, the `MacOllamaStagingPipeline`
   runs the arm64-slice gate and writes the on-SSD attestation
   under `mac/tools/ollama/ollama-package-trust.json`.
4. At runtime, `MacOllamaLifecycleService.ValidateTrust` and the
   Swift `evaluateTrustGate` in `mac-runner/Sources/main.swift`
   validate the attestation directly: file present + deserializes
   + URL is HTTPS to an allowlisted host (`github.com` or
   `objects.githubusercontent.com`) + SHA-256 is well-formed
   64-char hex. The 180MB+ binary is *not* re-hashed on each
   launch; the attestation is PrepApp's signed receipt of the
   staging-time hash verification.

**New trust chain (Windows):** Same shape, but the resolver runs
at PrepApp first-run instead of at CI build time —
`OllamaPackageService.EnsureOllamaReadyAsync` calls
`ResolveLatestOllamaWindowsAsync`, downloads, verifies, writes
attestation. The "Ollama package URL" advanced field in
`MainWindow.xaml` is removed because there's no longer a static
URL the user could override.

**Why this works where MAC4's previous attempt didn't:** the
"drift" failure mode in MAC4 was that the static dictionary at
runtime disagreed with the dynamic CI-resolved hash. The fix
isn't to pin both ends; it's to remove the static dictionary as
the runtime authority. The attestation written at staging time
(after byte-level hash verification against the upstream's
vendor `sha256sum.txt`) is the runtime authority. Same trust
model `.NET 8` already uses with Microsoft's `releases.json`
SHA-512 path. The URL-allowlist half of the chain stays; only
the URL→hash dictionary is gone.

**Why MAC35 host-staging stays:** Pre-refactor verification
re-checked upstream `server/download.go` through `v0.23.2`;
`numDownloadParts = 16` is still hardcoded with no env override.
Bumping to latest doesn't change the exFAT-write bottleneck on
Mac. The comment in `prep-core/OllamaModelStager.cs` was updated
to record the re-verification.

**Why a 5-minute spike before the refactor:** The Mac path bets
on the upstream `Ollama.app/Contents/Resources/ollama` layout
staying intact. A pre-refactor spike confirmed `v0.23.2`'s
archive is byte-identical at every touchpoint we rely on (inner
CLI path, universal arm64+x86_64 slices, vendor `sha256sum.txt`
format with `./` filename prefix). `Contents/Resources` now also
ships GGML inference dylibs alongside the CLI; extraction is
`ZipFile.ExtractToDirectory` so they ride along automatically.

This decision unlocks if upstream removes the GitHub release
shape (no `sha256sum.txt`, no per-asset URL), at which point we'd
need a different vendor-hash source. It also unlocks if Mac drops
the `Ollama.app` GUI bundle and ships only the CLI tarball
(`ollama-darwin.tgz` is now alongside `Ollama-darwin.zip` in
upstream releases) — at that point the Mac side simplifies
further and the inner-binary resolver in `OllamaPackageService`
goes away.

Established PR #237 (`edc99d3`), shipped v1.3.19.

## 2026-05-10 — Long-running listener ports get a two-layer recovery path: lsof+kill on stale processes and port-shift on TIME_WAIT [MAC39]

Mac runner field test (v1.3.19) wedged after Lock + re-select-SSD:
the new `mac-runner-host` sidecar tried to bind the same port the
prior listener had used and Kestrel raised `Failed to bind to
address http://127.0.0.1:NNNNN: address already in use`. Quitting
and relaunching the app was the only recovery. MAC34b had already
solved the *equivalent* failure for ollama on port 11434 by killing
PIDs holding the port via `lsof`; the sidecar port hadn't been
covered.

Two distinct failure modes can hold a port across a planned
restart on macOS:

1. **Stale process.** A prior process didn't exit cleanly (crash,
   SIGKILL race, or the process is still in zombie state). `lsof
   -nP -t -iTCP:port -sTCP:LISTEN` finds it; SIGTERM → SIGKILL
   reclaims the port.
2. **Kernel TIME_WAIT / cleanup delay.** The process is gone but
   the kernel is still holding the port (briefly, but long enough
   to fail an immediate rebind). `lsof` won't show this — the
   socket is in a kernel state without a process attached.

MAC39 covers both. For the chat-host port the Mac runner now does
the lsof+kill before each `hostController.start()` (same pattern
as MAC34b for ollama). For the kernel-cleanup case the Kestrel
side picks a different port instead of waiting: the new
`RunnerLocalApiService.ResolveAvailablePort(bindAddress,
preferredPort)` scans `preferredPort..preferredPort+20` via a
short-lived `TcpListener` probe, returns the first free port, and
the host announces the actual `baseUrl` via the existing `ready:
<url>` stdout line. The Mac Swift client (and any LAN consumer
parsing `CurrentBaseUrl`) picks up the shifted port transparently.

**Why both layers, not just one:**
- lsof+kill alone leaves the TIME_WAIT case unfixed — Lock + fast
  re-unlock would still fail because no straggler process is
  there to kill.
- Port-shift alone leaves the stale-process case half-fixed — the
  shifted port works, but the original port is still held by a
  zombie. Subsequent restarts keep climbing through ports until
  +20 runs out. Killing the straggler keeps the working set
  bounded to the configured port.

**Why port-shift is safe for LAN consumers:** The default Mac
runner posture is loopback-only (LAN exposure is OFF by default
post-MAC34a, and the user must explicitly opt in). When LAN
exposure is on, the configured port is the *preferred* port; if
it's actually free at startup, the shift never fires. If it does
fire, the shift is +1..+20 and the new URL is announced via
`CurrentBaseUrl` to whatever local consumer cares (mac-runner
parses `ready:` from stdout; Windows runner reads `CurrentBaseUrl`
directly). LAN clients have to re-discover the URL after a host
restart anyway because the loopback bind would have looked
different to them — same operational property either way.

**Apply to:** any future C# Kestrel host that the user can
restart at runtime (the MAC39 helper lives on
`RunnerLocalApiService` for now; promote to a shared utility if
another host needs it). Don't apply to Ollama itself — Ollama's
port is bound by the upstream binary, not by us, and the lsof+kill
half (MAC34b) already covers Ollama's failure mode.

Established PR #239 (`8eaa922`), shipped v1.3.20.

## 2026-05-10 — Lock button is encryption-conditional; plaintext SSDs see Stop/Start [MAC39]

After MAC30 (2026-05-09) made encryption opt-in, the default
plaintext SSD had no unlock material to zeroize. The Mac runner's
"Lock" button on a plaintext SSD was a misleading verb — it tore
down the chat stack (sidecar + ollama) but left the user with no
way to bring it back without re-selecting the SSD. The v1.3.19
field test specifically called this out: "if encryption is
present show lock, if not present start/stop?"

The fix is a conditional verb in `ContentView`'s button row,
driven by a new `@Published var isEncryptedSsd: Bool` set in
`loadConfig()` from
`SsdEncryption.isEffectivelyEncryptedForWriteGuard(ssdRoot:)`:

- **Encrypted SSD:** keeps "Lock" — `lockSession()` continues to
  do its full job (cancel chat, shut down sidecar, stop ollama,
  zeroize unlock material, re-show the unlock dialog). The MAC5
  / MAC30 invariant that the unlock material never outlives the
  user's session is preserved on every encrypted drive.
- **Plaintext SSD:** swaps to "Stop" or "Start" depending on
  whether the chat host is currently up (gated on
  `networkApiBaseUrl != nil`). The new `stopChatHost(reason:)` is
  a subset of `lockSession`: cancels the in-flight chat task,
  shuts down the sidecar, stops ollama, but does *not* touch
  `unlockMaterial` (there isn't any), `portableConfig`, or the
  model picker state. The new `startChatHost()` is a thin wrapper
  on the existing `ensureLocalChatStackRunning()`.

**Why not unify Lock with Stop on plaintext:** Lock has stronger
semantics on encrypted drives — it intentionally ejects the user
back to the unlock dialog. Reusing the same button verb for two
different behaviors would obscure the security-relevant operation.
Two verbs for two operations is correct; the conditional rendering
just hides the wrong one.

**Why not always show the button + grey out on encrypted-locked:**
The encrypted-locked state already returns early in `loadConfig`
and presents the unlock dialog; the button is hidden anyway via
`!vm.isEncryptedLocked` for the encrypted side. The new code only
adds the plaintext branch.

This decision unlocks if encryption ever becomes mandatory again
(unlikely — the v1.3.5 field test was the original push to make
it optional, and MAC30 / v1.3.17 codified the default-OFF
posture).

Established PR #239 (`8eaa922`), shipped v1.3.20.

## 2026-05-10 — Mac chat-stream URLSession timeout bumped to 180s for cold model loads [MAC39]

`URLSessionConfiguration.default.timeoutIntervalForRequest` is 60s
on macOS. The chat-stream contract from
`runner-core/Services/RunnerLocalApiService.cs:209-263` emits a
`start` frame immediately on receipt (before forwarding to ollama)
so the per-request timer resets on first byte — but on cold model
loads (5GB+ deepseek-r1:8b on USB SSD takes 30-90s before ollama
emits the first token), 60s isn't always enough headroom. The
v1.3.19 field test surfaced this as `Chat failed: The request
timed out` on the first prompt against a freshly-pulled 8b model.

Bumping the timeout to 180s for the chat session specifically
(other URLSessions in the runner — library list, ingest, etc. —
keep the default 60s) covers cold-load on every reasonable SSD
without unbounding a genuinely wedged request. 180s is the
ceiling on macOS for a chat that produces zero progress *between*
frames; once tokens start streaming, every token resets the
timer, so a long inference doesn't hit it.

**Why client-side timeout, not server-side keepalive:** keepalive
frames would mask actual server-side hangs. The current behavior
("you stop seeing data → eventually fail") is the right
diagnostic surface; 180s is just the right number for it.

**Apply to:** any future Mac client that streams from a slow
inference path. Don't apply to Windows — Windows is unaffected
because (a) it doesn't have an equivalent "USB SSD on macOS"
constraint and (b) HttpClient's defaults differ.

Established PR #239 (`8eaa922`), shipped v1.3.20.

## 2026-05-10 — Model-pull progress source-of-truth is the Ollama HTTP API, not CLI stdout text [MAC40]

`ModelOperations.PullModelAsync` consumes Ollama's `POST /api/pull`
streaming NDJSON response and surfaces it as structured
`OllamaPullProgress` frames (`Status`, `Digest`, `Total`,
`Completed`). The pre-MAC40 path spawned `ollama pull <tag>` as
a subprocess and parsed its TUI stdout; the v1.3.20 field test
showed why that's the wrong abstraction.

The CLI rendering is itself a downstream consumer of `/api/pull`
— the binary is just an HTTP client that prints what it gets.
Parsing the rendered text means our code is tied to the CLI's
human-facing format choices: MAC31's regex `pulling <hash>... NN%`
broke when post-MAC38 Ollama shifted the dots to a colon.
Broadening the regex defers the next break; switching to the
JSON contract eliminates the regression class because Ollama
maintains the API across versions.

**Apply to:** any future "parse the CLI's stdout to discover
state" temptation. If Ollama (or any tool we wrap) exposes an
HTTP/JSON contract for the same data, prefer that — even at the
cost of a bigger refactor than text-scraping. The principle is
"parse the source of truth, not the rendering."

**Doesn't apply to:** delete-model and other one-shot CLI
operations where there's no progress stream to consume. Those
stay on `RunProcessStreamingAsync` because exit-code-and-final-
log is sufficient and the equivalent HTTP call would be more
ceremony for no benefit.

Established PR #241 (`b5ac727`), shipped v1.3.21.

## 2026-05-10 — F2a: "Most popular" cap applies only to Recommended-source rows

On the WPF merged Models grid (`prep-app/MainWindow.xaml`), the
"Most popular" toggle restricts visibility to the top-15 entries
by ollama.com pull count — but **only for `Recommended`-source
rows.** Configured rows (`Source = "Config"`) and on-disk rows
(`Source = "Disk"`) always pass the filter.

**Why:** The merged grid surfaces three semantically different row
classes through one collection. The user already chose / installed
the Configured + on-disk entries — hiding them behind a popularity
filter would obscure their own work. The popular cap exists to
shrink the *discovery* surface (the 399-entry post-Refresh
catalog), not to gate already-tracked state.

**Implementation:** `PrepViewModel.IsModelRowVisible(row)` checks
`IsStarterOnlyRecommendationRow(row)` before consulting the
precomputed top-N tag set. The Mac picker doesn't have this
issue because `EncryptionSetupStepView` only renders catalog
entries (no row-class mixing).

**Search**, by contrast, runs uniformly across all three row
classes — search is "find this thing," not "narrow the discovery
surface," so it should match anywhere.

Established PR #243 (`859ac08`), shipped v1.3.22.

## 2026-05-10 — Unified C/W/M task-label scheme; parity rule strengthened

The backlog moves from a mixed `X*` / `F*` / `B*` / `H*` / `R*` / `MAC*` numbering scheme to three flat per-OS-scope buckets:

- **`C#`** — Cross-OS (touches `shared/`, `runner-core/`, `prep-core/`, or both per-host UI surfaces)
- **`W#`** — Windows-only (WPF Runner / PrepApp internals, Companion VR PC, DCS-anchored work)
- **`M#`** — Mac-only (SwiftUI hosts, `mac-*-host` sidecars, mac packaging, native Mac UX). Bodies live in `mac_project_backlog.md`.

**Why this change:** The legacy scheme grew out-of-order — X-numbers came from chronological field-test triage, F/B-numbers from feature/behavior dictation notes, MAC-numbers from a parallel Mac-track file. Numbers no longer reflected priority or scope. Cross-cutting concerns (e.g., the Mac side of an X-numbered "Windows" item) had no canonical home. The user's 2026-05-10 ask: *"lets take this time to order things in a way that makes sense and fix the numbering. mac only tasks should have their own letter/number; windows should have its own letter and number; and tasks that touch both should have their own letter and number."*

**Bucket assignment rule:** based on where the **work** lands, not where the symptom appears or how the concept frames itself. If shared-core changes substantively, it's `C` even when one OS surface is bigger. Pure-WPF or pure-SwiftUI work that doesn't touch shared code is `W`/`M`.

**Migration policy:**

- **Shipped items keep their original IDs** (`X*`, `F*`, `B*`, `H*`, `R*`, `MAC*`). Renaming would break PR notes, decision-doc cross-refs, and conversation memory across the project.
- **Open items get a new C/W/M ID** via the mapping table at the top of `project_backlog.md`. The existing `### Old-ID` body header stays in place until the item is next picked up for work, at which point both the header and any cross-references are rewritten in the same PR.
- **All new items from 2026-05-10 onward use the new scheme exclusively.** The 8-item field-test list intake from 2026-05-10 (filed as C1–C6 + M11–M13) is the first cohort.

**Closed on the refactor:** `X6` (Create Library UI hang) — 3+ weeks no recurrence on v1.2.5+; F3/X13 work touched the surface heavily. Reopen as a new C-item if it returns.

**Parity rule reinforcement (extends 2026-05-07 dual-OS review pass entry):** the user formalized a stronger version 2026-05-10:

> *"since we are closer or at parity for features between mac version and windows version when ever we start a new task mandatory review must be done when planning out new tasks to evaluate whats need (if anything) for each OS. if needed break them up into two PR's/tasks. if it is broken into seperate tasks the task for the other OS should always come next. I do not want one to fall behind or miss features/bug fixes."*

The 2026-05-07 rule mandated a dual-OS review pass during planning. The 2026-05-10 reinforcement adds a sequencing constraint:

- **When a task is split into per-OS work, the other-OS follow-up is the very next task — not deferred behind feature work.**
- File the follow-up before merging the first half. Mark it priority-next in the backlog. Do not pull a new feature item until the parity follow-up ships.
- The X13 → M12 case (Mac chat-UI parity to X13's structured-failure surface) is the canonical example of what this rule prevents: X13 shipped 2026-04-20 with Windows-runner UI surfacing only, the Mac follow-up was never filed, and the parity gap surfaced 3 weeks later as part of the v1.3.22 field-test "no fail message" complaint. Filing M12 immediately when X13 shipped would have closed it 3 weeks earlier.

**Exit ramps:** if Free-AI-SSD ever drops back to a single supported OS, the parity rule becomes trivially satisfied and the scheme can collapse to a single counter. As long as both OSes are actively supported, both the scheme and the sequencing rule stand.

Established 2026-05-10 in PR for `refactor/unified-task-labels`.

---

## 2026-05-10 — PrepApp is the sole party for embedding-model provisioning; runner UI is fallback only [C2]

PrepApp's `PrepViewModel.EnsureEmbeddingModelInstalledAsync` runs at
the tail of `DownloadAsync` (reusing the temp Ollama server already
running for the chat-model loop) AND as an idempotent guard at the
start of `FinalizeAsync` (spinning its own temp server only when the
disk-truth check shows the embedder is missing). Both paths share the
same disk-truth check via `_modelService.DiscoverModelsOnDisk`, so a
re-Download or re-Finalize on a fully-prepped drive is free.

The runner-side `ModelManagementService.PullEmbeddingModelAsync` +
WPF Runner's `PullEmbeddingModel_Click` button stays **as fallback
only** — a recovery action for the case where the user somehow lands
on a Runner with a missing embedder (manually-tampered SSD, partial
prep, etc.). The Mac runner does not currently have an equivalent UI
button; that parity gap is tracked as **M14** (Mac runner "Pull
embedding model" UI parity) — filed per the 2026-05-10 parity rule
rather than bundled into C2.

Why not the alternatives:
- **Runner is the sole party (push the responsibility downstream).**
  Reopens MAC35's deferred concern: pulling against the long-running
  in-process daemon mid-chat would either need to restart the daemon
  (interrupting any active stream) or stand up a parallel temp daemon
  (port allocation + lifecycle complexity). PrepApp already has the
  temp-server infrastructure for the chat-model loop, so reusing it
  is strictly cheaper. MAC35 explicitly deferred this exact path
  ("Filed as a follow-up if the embedding-pull pathology actually
  surfaces"); C2 is that pathology surfacing, but the right fix is
  upstream (PrepApp) rather than the deferred runner-side path.
- **Both PrepApp AND runner pull eagerly.** Doubles the failure
  surface for the same job; PrepApp running first means the runner
  call is a no-op in the happy path. Keep responsibility in one place.
- **Bundle Mac runner UI parity into C2.** The Mac runner button
  reopens the MAC35 daemon-restart question and would balloon the
  PR scope without changing the field-test outcome (PrepApp's auto-
  pull is the actual fix; the runner button is defense-in-depth for
  edge cases). Filed as M14 per the parity rule.

Ships unrevisited unless: (a) a class of users emerges who skip
PrepApp entirely (manually-staged SSDs); (b) the embedder model
churn becomes high enough that runner-side update needs to happen
without a re-prep; (c) M14 lands and the Mac runner button proves
robust enough that PrepApp's eager pull becomes redundant.

Established PR #247 (`1df4431`).

---

## 2026-05-10 — Mac LAN-exposure toggle is user intent; only `.crashed` clears it [M13]

Under MAC34's reshape, the "Expose API on LAN" toggle controls the
sidecar's bind address (loopback vs. configured) — it does NOT control
sidecar lifecycle. The sidecar always runs after unlock. Therefore
`networkModeEnabled` is pure user intent and must not be derived from
host-controller status transitions.

Concretely, `handleHostStatusChange(.stopped)` no longer touches
`networkModeEnabled`. `.stopped` fires routinely on every
`restartHostSidecar()` because `MacRunnerHostController.shutdown()` sets
`status = .stopped` synchronously and `didSet` dispatches the listener
to main async — so the listener fires on a later runloop turn, after
the new sidecar has already been started with the correct bind. Prior
to M13, that callback's `if networkModeEnabled { networkModeEnabled =
false }` ran after every toggle-ON click, snapping the UI back to OFF
even though the underlying sidecar was correctly bound to the LAN
address (v1.3.22 mac field-test report: "toggles for a split second
then it un-toggles").

`.crashed` (`terminationStatus != 0`) remains the right home for
involuntary state changes — it surfaces a message and clears
`networkModeEnabled` so the user sees they're back on loopback.
`lockSession` and `stopChatHost` continue to clear the toggle directly
because those represent the user walking away from intent.

Why not the alternatives:
- **Suppress `.stopped` during deliberate restart with a flag.** Adds
  state and a cancellation window where a real crash during a
  user-initiated restart would be misclassified. `.crashed` already
  distinguishes the two via terminationStatus; extra state is
  redundant.
- **Re-derive `networkModeEnabled` from `effectiveBind`.** Loses user
  intent the moment configuredBind is empty/loopback. The toggle is a
  declaration, not a computed view of bind state.

Ships unrevisited unless: (a) the runner gains a path where the
sidecar can stop without going through `shutdown()` AND without
`terminationStatus != 0` (e.g., external `kill -TERM` with a clean
exit shim); (b) a separate UI state distinct from "user wants LAN"
needs to flow through the same toggle.

Established PR #249 (`e6b958e`).

---

## 2026-05-10 — Mac chat failure decoding lives in a pure helper; library callers reuse it without a "Chat failed:" prefix [M12]

The Mac runner's previous private `apiErrorMessage(data:statusCode:)`
method bundled two responsibilities that don't belong together:
JSON-error-body decoding (ProblemDetails `detail`, `ErrorResponse`
`error`, fallback) AND a hardcoded `"Chat failed: "` prefix. It was
unused for chat (the chat-stream delegate cancelled before the body
landed) but called by six library-management callsites that had no
business prefixing library failures with "Chat failed:" — yet did,
because the helper made it the path of least resistance.

M12 splits these responsibilities:
- **Body decoding** lives in
  `mac-runner/Sources/RunnerChatErrorMessage.decode(statusCode:body:)`
  — returns the bare reason string, no prefix. Pure helper, testable
  from a standalone swiftc binary (existing pattern for
  `NdjsonFrameBuffer` / `SsdEncryption`).
- **Caller-specific framing** is the caller's problem. Chat callers
  set `chatError = message` (red banner). Library callers set
  `libraryStatus = message`. Each path knows its own surface.

This shape also makes future failure surfaces (M3 STT when it lands,
or any new sidecar endpoint) reuse the same decoder without leaking
chat semantics.

Why not the alternatives:
- **Keep one helper that returns "Chat failed: X" and have library
  callers strip the prefix.** Worse: every caller knows about chat,
  and the prefix is a coincidence not a contract.
- **Inline the decoding at each callsite.** Six copies of the same
  `detail`-then-`error`-then-fallback logic, no shared test surface,
  drift guaranteed.
- **Subclass / hierarchy of error types per surface.** Overkill for
  ~10 LOC of body decoding.

Ships unrevisited unless: (a) the server adopts a third error shape
(currently only `ErrorResponse` `{"error"}` and ProblemDetails
`{"detail"}`); (b) a caller needs structured error data (status code
+ category + message) rather than a flat string — at which point
the helper's return type widens to a struct.

Established PR #251 (`a52572c`).
