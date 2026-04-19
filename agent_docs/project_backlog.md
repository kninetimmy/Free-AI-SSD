# Project Backlog

## How to pull from this file

When I ask you to "tackle section X" or "pick up backlog item Y":
1. Read only the item in question plus any items it references.
2. Check the item's status marker — skip if `done` or `blocked`
   without first unblocking.
3. Re-read `project_arch.md` if the item touches architecture.
4. Check `project_decisions.md` for constraints that shape the
   approach.
5. Before implementing, confirm scope with me if the item is more
   than a few weeks old — conditions may have changed.

Each item carries: scope, affected files, staging (if multi-stage),
model recommendation (Sonnet/Opus), design decisions, status
(triaged / planning / in-progress / blocked / done).

## Tackle workflow

When Stephen says "tackle section X":
1. Claude reads the section's entry below.
2. Claude outputs a well-formed implementation prompt (per global
   CLAUDE.md's prompt-refinement rule) covering: intent, scope,
   affected files, staging, constraints. If the section is
   multi-stage, the prompt targets **Stage 1 only** unless Stephen
   says otherwise.
3. Claude states the recommended model for that prompt (the
   `Model:` line from the section). If the current model doesn't
   match, Claude pauses for Stephen to switch before implementing.
4. If the section is flagged for Opus planning, Claude drafts the
   plan first and waits for Stephen's approval before
   implementation.
5. Claude asks clarifying questions if the section's scope has gaps
   (e.g., unresolved design decisions inside the section).
6. After implementation ships and is confirmed working, Claude
   updates `README.md` with a meaningful user-facing summary of the
   change.

## README update rule

Per Stephen's requested workflow: after each **section** ships (not
each stage), update `README.md` with a meaningful description of
the user-facing change — not a changelog dump. The current README
was refreshed in PR #123 (`796719d`) with real screenshots; that's
the style to match.

## Priority order (most recent triage: 2026-04-18 — v1.2.1 field test)

**v1.2.x patch stream (each ships as its own PR + release — see decision 2026-04-18):**
1. **B3-Redux phase 2** — shipped 2026-04-18 (PR #133, `b20dd67`).
2. **X2** — Runner window ScrollViewer — shipped 2026-04-18 (PR #134, `5247d2a`, released as v1.2.2).
3. **X3** — Runner Ollama Start/Stop button state swap — shipped 2026-04-18 (PR #135, `353e54b`, released as v1.2.3).
4. **X1** — voice pipeline hang after TTS completes — shipped 2026-04-18 (PR #136, `a9862e3`, cut as v1.2.4). **Field test 2026-04-18 FAILED** — hang reproduces on example-prompt Send; v1.2.4 tag deferred. See **X1-Redux**.

**Shipped post-v1.2.4 (rolls into v1.2.5):**
4a. **X8** — shipped 2026-04-18 (PR #138, merged commit `fa34828`). Initial fix (`591a39b`) split model teardown from `Dispose()` so the shared semaphore survived re-init; follow-up (`9c3a054`) folded in the three races Gemini + Codex flagged — single `_lifecycleGate` serializes Init / Transcribe / Dispose, `_shutdownCts` drains in-flight `ProcessAsync` on window close, and `CancellationToken` is now threaded through `ISpeechToTextService` into PTT + network API callers.

**Blocking v1.2.5 tag:**
4b. **X1-Redux** — hang regression still present after PR #136; phase 1 diagnostic branch `diag/x1-redux-send-hang` pushed 2026-04-18, awaiting repro log from SSD. **NEW 2026-04-18.**

**Codex deep-review findings (intake 2026-04-18 — slot between X1-Redux and feature queue):**
5. **X9** — encrypted config persistence lifecycle (Critical; Opus planning)
6. **X10** — document replacement + rebuild consistency (High)
7. **X11** — companion keyboard PTT + first-run validation (High)
8. **X12** — download verify-before-move (Medium, security-adjacent)
9. **X13** — chat/STT surface real failures (Medium)
10. **H2** — repo hardening pass (housekeeping batch)

**After hardening batch ships:**
11. **F3** — PrepApp 3-tab restructure (Opus planning) — also folds in the "Add File button disabled" tooltip hint
12. **F4** — profile FTUE in PrepApp + companion install target selector (multi-stage, Opus planning)
13. **B2** — build LAN discovery (multi-stage, Opus planning; can run in parallel with F4)
14. **F2** — live model list fetch (smaller feature)
15. **R1 Stage 2** — `/api/documents` + `/api/documents/reindex` server endpoints + `/docs` / `/reindex` CLI commands (follow-up to R1 Stage 1)

**v1.3.x territory:**
16. **X4** — Bundle a real web chat UI (static SPA served from Runner's Kestrel, reusing existing `/api/chat` endpoints)
17. **Runner tab restructure** — follow-up to X2 once F3's tabbed aesthetic lands
18. **X5** — GPU/CPU compute indicator (read-only first, selector later)

**Field-test surface from v1.2.4 walkthrough (2026-04-18):**
- **X6** — "Create Library" click hangs UI, crashes, library created on reopen (separate hang from X1).
- **X7** — DCS bindings scan finds aircraft but reports "no custom bindings" against real `.diff.lua` files on disk.
- **F5** — No in-app TTS settings UI (backend selector + voice-model picker). Blocks field-testing Piper/SAPI/disabled paths.

**Also outstanding:** README update for F1 (USB SSD detection fix). Small, can be bundled with any doc PR — or folded into H1 below.

**Housekeeping — slot between bug fixes/features, not during in-flight work:**
- **H1** — shipped 2026-04-18 (PR #137, `a894862`). 4 stale files deleted; README + QUICKSTART refreshed for v1.2.x UX. X1 deliberately omitted pending X1-Redux.

`I1` (architecture diagram) is folded into F4 Stage 1 — no standalone entry.

Items `B1`–`F4` below were triaged from Stephen's `Downloads/# Free-AI-SSD Project TODO.md` (dictated-while-driving notes — treat TODO assumptions with skepticism). Items `X1`–`X5` were added 2026-04-18 from the v1.2.1 field-test findings (`C:\Users\Kninetimmy\Documents\ai ssd issues.txt`). Each section is addressable independently.

---

## Items

### R1 — Runner CLI REPL (headless SSH/Tailscale client)

**Status:** Stage 1 done (PR #130, `bb59a6c`); Stage 2 triaged
**Scope:** Multi-stage (2 stages)
**Model:** Sonnet 4.6 for Stage 1; Sonnet 4.6 for Stage 2
**Stephen confirmed (2026-04-17):** yes, option 3 (thin HTTP client against existing `RunnerLocalApiService`, not a headless host).

**Intent:** New `runner-cli/` console project (`net8.0`, not `-windows`) that speaks to a running Runner's LAN API. Primary use case: SSH from iPad via Tailscale into the Runner host, run a terminal REPL against the existing chat / RAG pipeline.

**Existence:** Server-side endpoints already present in `runner/Services/RunnerLocalApiService.cs`:
- `GET /api/health` (unauth) — line 112
- `GET /api/models` — line 121
- `POST /api/chat` — line 134 (returns `{responseText, sources, usedRagContext}`)
- `POST /api/chat/stream` — line 149 (NDJSON: `start` → many `token` → `complete`)
- Auth via `Authorization: Bearer` or `X-API-Key` when `NetworkRequireApiKey` is set.

No "list documents" or "reindex" endpoint exists today. See Stage 2.

**Config discovery (industry-standard precedence):** `--url` flag > `FREEAI_URL` env > hardcoded `http://127.0.0.1:41555`. Same pattern for `--api-key` / `FREEAI_API_KEY` (no default).

**Security:**
- API key only from env or flag — never logged, echoed, or persisted.
- URL parsed with `Uri.TryCreate`, scheme restricted to `http`/`https`.
- No shell-outs, no `Process.Start` — pure HTTP client.

**Staging:**

**Stage 1 — REPL v1 (this PR):**
- New project `runner-cli/FreeAiSsd.RunnerCli.csproj` (`net8.0`, `Exe`, `PublishSingleFile`, `SelfContained`).
- Add to `FreeAiSsd.sln` + `build.ps1` publish list.
- `Program.cs` — arg parsing, env resolution, REPL entry.
- `RunnerApiClient.cs` — typed wrapper over health/models/chat/chat-stream.
- `Repl.cs` — prompt loop, slash-command dispatch (`/help`, `/models`, `/model <name>`, `/health`, `/clear`, `/quit`; also `quit`, `exit`, EOF, Ctrl-C exit cleanly).
- Plain `Console.ReadLine()` — **no readline NuGet** (SSH-robust, avoids new dep).
- Streaming: write tokens as they arrive; print `— sources: [...]` or `— (no RAG context)` on `complete`.
- Ctrl-C during stream cancels that request only; second Ctrl-C at idle prompt exits.
- Tests in existing `tests/` project: mock `HttpMessageHandler` to verify NDJSON parsing, auth header wiring, slash-command dispatch.
- `docs/QUICKSTART` snippet: "Connecting over SSH/Tailscale."

**Stage 2 — Document management endpoints + CLI commands (follow-up PR):**
- New server endpoints on `RunnerLocalApiService`:
  - `GET /api/documents` — list ingested documents (name, path, chunk count, last-modified). Consumes `DocumentOperationsService`.
  - `POST /api/documents/reindex` — trigger re-ingestion. Return a job id + status endpoint, or block until complete for small libraries.
- New CLI commands: `/docs`, `/reindex`.
- Tests: server-side endpoint tests + CLI command tests.
- Reuse existing auth middleware — these endpoints sit behind the same API-key gate.

**Minor tech debt observed (not in scope):** `41555` appears as a literal in `PortableConfig.cs:122`, `CompanionConfig.cs:8`, `PrepViewModel.cs:42`. A `SsdDefaults.RunnerApiPort` constant in `shared/` would prevent drift.

**Affected files (Stage 1):**
- New: `runner-cli/FreeAiSsd.RunnerCli.csproj`
- New: `runner-cli/Program.cs`, `runner-cli/RunnerApiClient.cs`, `runner-cli/Repl.cs`
- `FreeAiSsd.sln` — add project
- `build.ps1` — add publish step
- `tests/FreeAiSsd.Tests.csproj` + new test files
- `docs/QUICKSTART.md` (or equivalent) — SSH usage section

---

### B2 — Build LAN discovery (Runner broadcasts, Companion listens) + relocate host IP field

**Status:** triaged
**Scope:** Multi-stage (4 stages)
**Model:** Opus 4.7 for planning
**Stephen confirmed (2026-04-17):** yes, build discovery.

**Existence:** Host IP field confirmed in `prep-app/MainWindow.xaml:380-397` (`CompanionHostAddress` + `CompanionHostPort`) — bound into `Finalize` to pre-write `companion-config.json` onto the SSD (`PrepViewModel.cs:863-896`). No discovery code exists in runner/, companion/, or shared/. `NetUtils.cs` is just loopback port availability checking. Companion (`CompanionRuntime.cs`) requires `HostAddress` to be set — no fallback, no probe.

**Design decisions needed (surface before implementation):**
- **Protocol:** UDP broadcast (simple, firewall-friendly on LAN) vs mDNS (Bonjour, more robust but heavier). UDP broadcast recommended — matches project's "keep it simple" posture and works without extra dependencies.
- **Port:** Pick a dedicated discovery port (not 11434 Ollama, not 41555 Runner API). Suggest 41556 or similar.
- **Payload:** Runner advertises `{hostname, ip, runnerApiPort, apiKey-fingerprint-or-nothing}`. Companion matches and auto-fills settings.
- **Security:** Discovery should NOT leak the API key. Companion still validates via `/api/health` with the key the user has configured.

**Affected files:**
- New: `shared/Services/LanDiscoveryBroadcaster.cs` (Runner side) + `shared/Services/LanDiscoveryListener.cs` (Companion side) — or single file with both
- `runner/Services/RunnerLocalApiService.cs` — kick off broadcast when API starts
- `runner/MainWindow.xaml(.cs)` + ViewModel — Advanced Options section with manual host IP fallback (per TODO)
- `companion/CompanionRuntime.cs` — consume discovery on startup; fall back to manual settings if not found
- `companion/SettingsWindow.xaml(.cs)` — "Searching → Found [hostname @ ip] / Not Found" status + "Retry Discovery" button + manual entry inline
- `prep-app/MainWindow.xaml:380-397` — once discovery is in, the PrepApp host IP field can be gated behind Advanced Options (or removed entirely, since discovery replaces the need to pre-configure)
- `shared/ViewModels/PrepViewModel.cs:863-896` — adjust companion-config staging accordingly
- `tests/` — discovery tests with mocked UDP socket

**Staging:**
- **Stage 1** — Design + implement `LanDiscoveryBroadcaster` and `LanDiscoveryListener` in `shared/`. Unit test in isolation.
- **Stage 2** — Wire Runner to broadcast on API start; wire Companion to listen on startup. Integration-test on two machines.
- **Stage 3** — Companion Settings UX (searching / found / not found / retry / manual fallback).
- **Stage 4** — Relocate/remove PrepApp host IP field; Runner Advanced Options manual-entry fallback.

---

### B3 — "Format & Prepare Drive" button actually formats

**Status:** shipped 2026-04-17 (PR #131, `efc5f56`) — **REOPENED 2026-04-18** as **B3-Redux** below. Field test showed the format command runs to exit 0 but the drive isn't actually wiped (pre-existing `my library` folder persists, volume label never changes). Keep this section for historical context; see B3-Redux for the active work.
**Scope:** One-shot
**Model:** Sonnet 4.6
**Stephen confirmed (2026-04-17):** yes, format to correct FS, then ensure folder structure.

**Existence:** Bug confirmed. `FormatPrepareAsync` (`PrepViewModel.cs:741-790`) **does not format**. It only calls `_driveService.EnsureSsdStructure(root)` (folder layout — keep this, it's already correct) and saves a fresh `PortableConfig`. No `format.exe`, no `diskpart`, no `Format-Volume` PowerShell call anywhere in the repo (verified via grep). The `VolumeLabel` TextBox binding exists in `MainWindow.xaml:356-360` but is never consumed by `FormatPrepareAsync` — dead binding.

**Intended flow:** Format drive → `EnsureSsdStructure` → save config. The folder-structure piece already exists and works; only the format step is missing.

**Affected files:**
- `shared/Services/IDriveService.cs` — add `FormatAsync(string root, string label, string fileSystem, CancellationToken)` method
- `prep-app/Services/DriveService.cs` — implement via `ProcessRunner.ArgumentList` + PowerShell `Format-Volume -DriveLetter X -FileSystem NTFS -NewFileSystemLabel ... -Confirm:$false`
- `shared/ViewModels/PrepViewModel.cs:741` — call `FormatAsync` first, then the existing `EnsureSsdStructure`; consume `VolumeLabel` binding; show "Drive will be formatted now" confirmation before proceeding
- `prep-app/MainWindow.xaml:361` — button already reads "Format & Prepare Drive" (correct); no rename needed
- `tests/` — unit tests for the new `IDriveService.FormatAsync` (mock ProcessRunner + assert argument list)

**File system default:** NTFS (matches the warning in `DriveInspector.DriveWarning`). Allow exFAT as secondary option if user wants cross-platform compat with macOS side of the app — but flag strongly that NTFS is recommended.

**Security:**
- Format requires admin elevation. Must check admin status (`WindowsIdentity.GetCurrent().Owner` vs `WellKnownSidType.BuiltinAdministratorsSid`) up-front and fail-closed with a clear "relaunch as admin" message.
- Use `ProcessRunner.ArgumentList` — never string concat. Drive letter must be validated (single letter A-Z) before invocation.
- Re-confirm erase via the existing `ConfirmErase` dialog.

**⚠ Staging note:** Test in a VM or with a spare USB stick first. Don't exercise against Stephen's live SSD until manually verified.

---

### F2 — Live model list fetch (HuggingFace / Ollama library)

**Status:** triaged
**Scope:** One-shot for v1
**Model:** Sonnet 4.6

**Existence:** Current catalog loads from `prep-app/Resources/starter-models.json` with an embedded fallback (`StarterModelCatalogLoader.Load`). Not hardcoded as TODO states — **correction: it's JSON-file-based already**, just not live-fetched.

**Affected files:**
- `prep-app/StarterModelCatalog.cs` — add `LoadFromNetworkAsync` path with fallback to existing file/embedded loaders
- New: `prep-app/Services/LiveModelCatalogService.cs` (or similar) — handles HuggingFace / Ollama API fetch
- `shared/ViewModels/PrepViewModel.cs` — add `RefreshCatalogCommand` + `LastCatalogUpdate` display
- `prep-app/MainWindow.xaml:93-118` — add "Refresh Model List" button + timestamp caption
- Consider: `shared/OllamaPackageTrustPolicy.cs` pattern — any new outbound HTTP endpoint should go through a trust policy to match project's security posture

**Source choice:** Ollama library is simpler (curated, already size-tagged). HuggingFace requires heavier filtering. Recommend Ollama-first, HuggingFace as optional advanced source.

**⚠ Security note:** This introduces outbound HTTP from PrepApp. Per global CLAUDE.md, flag dependency/network additions to Stephen before installing any JSON parser beyond what's available, though `System.Text.Json` should cover it.

---

### F3 — PrepApp 3-tab restructure

**Status:** triaged
**Scope:** Multi-stage (4 stages)
**Model:** Opus 4.7 for the planning prompt, Sonnet 4.6 for implementation stages

**Existence:** Current layout is 2 tabs (`MainWindow.xaml:77-442`): Model Manager (line 83) and Drive Setup (line 302). TODO's observations are accurate:
- "Add selected to configuration" (line 111) and "Add to config" (line 203) are redundant.
- "Pull/Install" (line 186) and "Pull Selected" (line 208) also overlap.
- Warning strip (`SelectedDriveWarning` at Row 2) is collapsible per PR #120's stable decision, but positioned below the log on the Model Manager side — visibility concern is valid.
- Log panel (Row 3, `1.5*` height) is squeezed.

**Affected files (major rewrite):**
- `prep-app/MainWindow.xaml` — split into 3 tabs; rewire bindings
- `shared/ViewModels/PrepViewModel.cs` (1154 lines currently) — may need splitting into per-tab view models
- `prep-app/MainWindow.xaml.cs` — FTUE step targets may need updating (the overlay targets elements by name)
- `project_decisions.md` — update decisions about warning strip placement

**Staging (recommend Opus draft the detailed plan first):**
- **Stage 1** — Extract Tab 1 "Model Downloader": move Starter Models + new "Send to Configuration →" button; remove Configured Models from this tab.
- **Stage 2** — Extract Tab 3 "Configuration / Finalize": move Configured Models + Finalize + Check Readiness here.
- **Stage 3** — Clean Tab 2 "Drive Setup": relocate warning strip into this tab prominently; remove host IP (gated on B2 resolution).
- **Stage 4** — Eliminate redundant buttons; consolidate Pull/Install flow.

**⚠ Watch for:** FTUE overlay (`MainWindow.xaml:533-578`) hard-references element names — any renames break the onboarding tour.

---

### F4 — Profile FTUE moves entirely to PrepApp (+ companion install target selector)

**Status:** triaged
**Scope:** Multi-stage (4 stages)
**Model:** Opus 4.7 for planning
**Stephen confirmed (2026-04-17):** move FTUE entirely to PrepApp; Runner silently reads `ActiveProfile` from SSD config at launch.

**Includes I1** — two-machine architecture diagram becomes the first step of F4's rebuilt FTUE flow (before profile selection). Flow is: *see two-machine architecture → choose profile → finish drive prep → launch Runner*. Include as Step 1 of Stage 1.

**Existence:**
- Profile system exists in **Runner only**: `shared/Profile/UserProfile.cs` (enum: `GeneralAssistant`, `FlightSim`), `shared/Profile/ProfileDefaults.cs` (applies defaults), `runner/ProfileSelectionDialog.xaml(.cs)` (required on first run, `isRequired:true` blocks close).
- PrepApp has **zero profile awareness** — grepped prep-app/ for `UserProfile`, no matches.
- Companion install in PrepApp is a **single SSD-only checkbox** (`MainWindow.xaml:374-379`, `InstallVrCompanion`). No local-install option. No "both" option. Staging is at SSD `/companion` only.

**Stable decision update needed:** Current decision "First-run profile dialog is required — user must choose before the app proceeds" (enforced in Runner at `ProfileSelectionDialog.xaml.cs:18-24`). New behavior: **PrepApp is the sole FTUE owner; Runner reads `ActiveProfile` from config and never prompts on first run.** In-Runner profile toggle stays (for mid-session switching per existing pill-toggle UX). Append the superseding decision to `project_decisions.md` when this ships.

**Companion install target (Stephen's guidance):**
- Industry standard for Windows: **Program Files (x86/x64)** for shared installs (requires admin), **`%LocalAppData%`** for per-user installs (no admin). Start Menu shortcut is standard. Desktop shortcut optional.
- Stephen wants: **Program Files default** + **desktop shortcut optional** + **custom path input** so the user can drop the executable anywhere (portable-app use case).
- Research action: look at how comparable portable Windows apps (Obsidian, VS Code "System" vs "User" installers, Steam) handle the "install to Program Files vs portable anywhere" split. Use that pattern.

**Affected files:**
- Move: `runner/ProfileSelectionDialog.xaml(.cs)` → `shared/UI/ProfileSelectionDialog.xaml(.cs)` (reusable by both apps) — or create new `prep-app/ProfileSelectionDialog.xaml` and delete the Runner version
- `prep-app/MainWindow.xaml(.cs)` — integrate profile card into FTUE overlay flow (`MainWindow.xaml:533-578`); gate advancement until selected
- `shared/ViewModels/PrepViewModel.cs` — track `ActiveProfile`, write to `PortableConfig` on Finalize
- `runner/MainWindow.xaml.cs` — remove first-run force-prompt path; silently read `ActiveProfile` from loaded config
- `runner/App.xaml.cs` — update boot flow to skip profile dialog
- `prep-app/MainWindow.xaml` — replace `InstallVrCompanion` single checkbox with target selector: **[◉ Program Files] [○ Custom path...] [○ SSD only] [○ Both]** + optional "Add desktop shortcut" checkbox
- New: `prep-app/Services/LocalCompanionInstaller.cs` — handles Program Files install, shortcut creation, custom path copy
- `project_decisions.md` — superseding entry for first-run profile dialog behavior

**Staging:**
- **Stage 1** — Two-machine architecture diagram (I1) as Step 1 of FTUE overlay; profile selection in PrepApp FTUE; write `ActiveProfile` to SSD config at Finalize. Delete or soften Runner first-run prompt.
- **Stage 2** — Post-setup launch flow ("SSD ready — launch Runner now?" → Flight Sim: bindings import + doc ingest walkthrough; General: doc ingest only).
- **Stage 3** — Companion install target selector UI (Program Files / custom path / SSD / both).
- **Stage 4** — Local installer logic (copy to target, create Start Menu + optional desktop shortcut, register uninstaller?).

**⚠ Architecture clarity:** TODO's two-machine description (🖥️ VR PC runs Companion, 💾 SSD machine runs Runner) is accurate and matches current code — Companion connects to Runner over LAN (`CompanionRuntime.TryBuildBaseUri`).

---

### B3-Redux — Format regression: root cause is UAC two-click UX trap

**Status:** phase 1 complete 2026-04-18 (branch `claude/b3-redux-diagnostics`, commit `5910460`); phase 2 triaged, not started
**Scope:** Two-phase — (1) diagnostic pass ✓, (2) UX fix + integration test
**Model:** Sonnet 4.6 for phase 2

**Phase 1 finding (2026-04-18):** B3 code is correct. The diagnostic build logged a real SSD format end-to-end: PowerShell command built correctly, exit 0, label changed "Portable AI" → "Portable AItest2", no drive-letter swap. The v1.2.1 "regression" was the **UAC two-click UX trap** — user clicks Format in non-elevated instance → accepts UAC relaunch → new elevated window appears → user doesn't realize they must click Format *again*. The `my library` folder persisting was leftover from a prior successful Finalize on the still-unwiped drive.

**Phase 2 approach (Stephen picked 2026-04-18):** belt-and-suspenders.

**Symptom (v1.2.1 field test, real SSD on Stephen's hardware):**
- "Format & Prepare Drive" button reports success and the drive appears prepped at end of flow.
- But: volume label does NOT change to the value in the `VolumeLabel` TextBox.
- And: a `my library` folder created in a prior session persists after the supposed format — meaning the drive was NOT wiped.
- Tests all pass (352/352) because `IDriveService.FormatAsync` is mocked with `Task.CompletedTask` in `PrepViewModelTests.cs:375–390`. The B3 test suite never exercised the real PowerShell.

**What the code looks like post-PR #131 (reviewed 2026-04-18):**
- Control flow is correct: confirm → elevate → format → `EnsureSsdStructure` → save config → re-enumerate (`PrepViewModel.cs:745` onward).
- `VolumeLabel` binding is wired end-to-end: `MainWindow.xaml:359` → `_volumeLabel` → `DriveService.FormatAsync` → `DriveFormatCommand` → `FREEAI_FORMAT_LABEL` env var → `Format-Volume -NewFileSystemLabel $env:FREEAI_FORMAT_LABEL`.
- `ProcessRunner.ArgumentList` usage is correct; the PowerShell invocation is argument-safe.

**Plausible root causes (to discriminate in phase 1):**
1. **UAC relaunch race** — non-elevated instance calls `TryRelaunchElevated()` in `WindowsElevationService.cs:21–50`, which `Process.Start`s an elevated child and calls `Application.Current?.Shutdown()`. If the user doesn't click Format in the elevated instance (or if the relaunch silently fails), the original un-elevated instance could fall through `EnsureSsdStructure` on an unwiped drive. Stephen reports "drive was created" — `EnsureSsdStructure` runs regardless of whether format ran.
2. **`Format-Volume` exit 0 without formatting** — certain drive conditions (open handles, non-removable bus type, enclosure quirks) can cause `Format-Volume -Confirm:$false` to return success without wiping. Need to verify `-Force` semantics and whether stdout contained warnings we're ignoring.
3. **Drive-letter mismatch under UAS/NVMe** — same class of bug F1 hit (`MSFT_PhysicalDisk.DeviceID` ≠ `MSFT_Partition.DiskNumber` on some enclosures, fixed in `3b20db8`). Possible the letter being formatted isn't the letter the user sees at end of flow.
4. **Something else entirely** — don't fix phantom causes; diagnose first.

**Phase 1 — diagnostic (DONE, commit `5910460`):**
- `DriveFormatCommand.Describe()` renders the built command for logging.
- `DriveService.FormatAsync` logs full command + env + working dir pre-run; captures all stdout/stderr (not just 10-line tail); logs exit code explicitly on every path.
- `PrepViewModel.FormatPrepareAsync` logs elevation status at moment of call, pre-format drive snapshot, post-format drive enumeration with label-actually-changed check.
- Sidecar file sink at `%TEMP%\freeai-format-diagnostic.log` (overwrites each run) — necessary because the UI LogListBox binds to `LogLines` and WPF ListBoxes don't support free-text selection.
- Phase 1 produced a working-format log artifact proving the code is correct. Root cause identified as UX, not code.

**Phase 2 — UX fix + integration test (TODO):**
1. **Auto-resume across UAC relaunch** — when `TryRelaunchElevated` fires, pass command-line args that encode the intent: `--autoresume-format=<root> --autoresume-label=<label>`. The new elevated instance parses these in `App.xaml.cs` / `MainWindow` startup, auto-selects the drive by root, pre-fills the label, and **prompts one confirm dialog** ("Format G:\\ with label 'Portable AI' now?") before proceeding. Never auto-formats silently — the confirm is the safety gate.
2. **Signpost banner in the elevated instance** — even when auto-resume runs, show a persistent banner at the top of the PrepApp window: "Running as administrator. Format operation ready to continue." Provides visual confirmation the relaunch completed and the user is in the right window. If auto-resume fails for any reason (e.g. drive no longer present, invalid args), the banner stays and instructs: "Click Format & Prepare Drive to continue."
3. **Real-ProcessRunner integration test** — new test in `tests/` that invokes `DriveService.FormatAsync` through the real `ProcessRunner` against a VHD (preferred; created via `New-VHD` + `Mount-DiskImage` in test setup, torn down after). Asserts post-format label matches requested label. Per `~/.claude/projects/.../memory/feedback_integration_tests_for_shellouts.md` — the mock-only suite is what let phase-1's regression hypothesis go unchecked.
4. **Remove (or gate) phase-1 diagnostic logging** — the verbose logging + sidecar file are noisy for production. Either remove entirely (relying on the new integration test to catch future regressions), or gate behind a `--diag` command-line flag. Decide during implementation based on how useful the sidecar turned out in the field.

**Affected files (phase 2):**
- `prep-app/App.xaml.cs` — parse `--autoresume-format` and `--autoresume-label` args, surface to `MainWindow` / `PrepViewModel`
- `prep-app/Services/WindowsElevationService.cs` — accept the args to forward in `TryRelaunchElevated`'s `ProcessStartInfo.ArgumentList` (don't use string concatenation — same security posture as `DriveFormatCommand`)
- `shared/ViewModels/PrepViewModel.cs` — on startup, if auto-resume args present: select drive by root, pre-fill `VolumeLabel`, fire a "continue format?" confirm, invoke `FormatPrepareAsync`
- `prep-app/MainWindow.xaml` — new banner element at top (visible when `IsElevated && HasAutoResumeIntent`)
- `prep-app/Services/DriveService.cs` + `shared/ViewModels/PrepViewModel.cs` — revert or gate the phase-1 verbose logging
- `shared/Services/DriveFormatCommand.cs` — the `Describe()` helper can stay (useful for future debugging, zero runtime cost)
- New: `tests/DriveServiceIntegrationTests.cs` — real-ProcessRunner test against a VHD target
- `tests/FreeAiSsd.Tests.csproj` — may need PowerShell script helpers for VHD setup/teardown

**⚠ Safety notes for phase 2:**
- The confirm dialog in the elevated instance is **non-negotiable** — never auto-format a drive the user didn't explicitly re-confirm after relaunch, even if they clicked Format pre-relaunch. Intent can drift across the UAC gap.
- Command-line args must be validated: drive root parsed through `DriveFormatCommand.ParseDriveLetter`; label length-capped via `DriveFormatCommand.SanitizeLabel`. Anything invalid → fall back to signpost banner ("Click Format & Prepare Drive to continue"), don't crash.
- The integration test should run on a **VHD**, not a USB stick. CI machines don't have USBs. `New-VHD` / `Mount-DiskImage` is Windows-admin-required — test may need to be skipped on non-elevated CI (fine; mock test still runs everywhere).
- **DO NOT merge phase 2 without first verifying the auto-resume path against Stephen's real SSD.** The integration test covers the format, not the UAC relaunch flow.

---

### X1 — Voice pipeline hang after TTS completion

**Status:** shipped 2026-04-18 (PR #136, `a9862e3`, cut as v1.2.4). **Field test FAILED 2026-04-18** — hang reproduced via example-prompt Send. Reopened as **X1-Redux** below.
**Scope:** One-shot
**Model:** Sonnet 4.6 (implemented on Opus 4.7 — Stephen had usage headroom)

**Symptom:** User clicks Send with voice input; the voice response plays back correctly; but then the "generating…" indicator never clears, the UI freezes, and the app never recovers. Stephen reports it as a "hard crash."

**Root cause (reviewed 2026-04-18):**
`PttVoicePipelineService.cs:256–272` has a `finally` block that polls `_tts.IsSpeaking` every 100ms, capped at a 60-second timeout, before transitioning state back to `Idle`. `IsSpeaking` is a plain non-thread-safe bool in `PiperTextToSpeechService.cs:35` and `SystemTextToSpeechService.cs:28`, set in a `try/finally` around the background `Task.Run(() => RunPiperAndPlay(...))` at `PiperTextToSpeechService.cs:84`. The inner play loop uses a `ManualResetEventSlim.Wait(100)` (`PiperTextToSpeechService.cs:349`).

If the NAudio completion event delivers late, or the Piper child process lingers, `IsSpeaking` can stay true for the full 60s timeout before `SetState(PttState.Idle)` runs at line 271 — during which the UI sits on "generating…" and feels frozen. Stephen's "never recovered" is consistent with him force-quitting the app before the 60s polling timeout expired.

**Fix:** Replace the polling loop with event-driven completion:
- `ITextToSpeech` gains a `Task SpeakAsync(...)` contract that completes **only after playback fully finishes** (not just after the process starts).
- `PiperTextToSpeechService` signals completion via a `TaskCompletionSource<bool>` flipped from the NAudio `PlaybackStopped` handler (and also on `ct.Cancel()` / process exit).
- `PttVoicePipelineService` `await`s that task in the finally block instead of polling `IsSpeaking`. No timeout needed on the happy path; keep a safety timeout (~30s) only as a defense against stuck processes.

**Affected files:**
- `runner/Services/PiperTextToSpeechService.cs` — expose completion via TCS; fire from `PlaybackStopped`
- `runner/Services/SystemTextToSpeechService.cs` — same pattern for the fallback TTS path
- `runner/Services/PttVoicePipelineService.cs:256–272` — replace polling loop with `await` on the new contract
- Interface in `runner/Services/ITextToSpeech.cs` (or wherever defined) — update contract
- `tests/` — add test that verifies `SpeakAsync` doesn't complete until playback finishes, and that `PttState` transitions to `Idle` without the 60s polling path

**⚠ Watch for:** the fallback `SystemTextToSpeechService` uses `System.Speech.Synthesis` which has its own `SpeakCompleted` event — wire that similarly. Don't leave one of the two TTS implementations on the old polling contract.

---

### X2 — Runner window ScrollViewer patch (interim; tab restructure later)

**Status:** shipped 2026-04-18 (PR #134, `5247d2a`, released as v1.2.2)
**Scope:** One-shot (this item). A future Runner tab restructure is a separate bigger item — don't bundle.
**Model:** Sonnet 4.6

**Symptom:** `runner/MainWindow.xaml:6` declares `Height="1020" Width="1100"` with no ScrollViewer around the root grid. On monitors shorter than ~1020px usable (common with taskbar + window chrome), the DCS bindings import section (Grid.Row="4" at `runner/MainWindow.xaml:541–679`) falls off the bottom of the screen and is unreachable. Stephen can't test bindings import without it.

**Fix:** Wrap the root content in a `ScrollViewer VerticalScrollBarVisibility="Auto"`. Mind the existing interior scrollable regions (the response textbox on Row 6 has `Height="*"` — needs a max-height or a conversion to `Auto` so it doesn't fight the outer scrollviewer for extra space).

**Affected files:**
- `runner/MainWindow.xaml` — wrap `RootContent` grid in a `ScrollViewer`; adjust Row 6 sizing so the outer scroll is the one that activates when content exceeds window height
- Visual-regression check: ensure the FTUE overlay (if any in the Runner) still positions correctly over the scrolled content
- `tests/` — N/A (pure XAML)

**Follow-up:** Once F3's tabbed PrepApp ships, do a matching tab restructure for the Runner (Chat / Library / Integrations / Settings). Separate backlog item, not this one.

---

### X3 — Runner Start/Stop Ollama button state swap

**Status:** shipped 2026-04-18 (PR #135, `353e54b`, released as v1.2.3)
**Scope:** One-shot (trivial)
**Model:** Sonnet 4.6

**Symptom:** Both Start and Stop Ollama buttons are always visible; the Stop button uses `TactileMagentaButton` style unconditionally (`runner/MainWindow.xaml:236–250`), so it looks like the active CTA even when Ollama is already running. Confusing — users think the magenta "Stop" button means "Ollama isn't running, click here."

**Fix:** Swap button styles (or visibility) based on `_ollamaService.IsRunning`:
- When stopped: Start button = `TactileMagentaButton` (active CTA), Stop button = `GhostSecondaryButton` or hidden.
- When running: Start button = `GhostSecondaryButton` or hidden, Stop button = `TactileMagentaButton`.
- Keep the running-LED indicator unchanged.

Could be implemented via a `DataTrigger` binding on `IsRunning` or via visibility bindings with a converter. Pick whichever is consistent with how other buttons in the project swap styles (see existing Controls.xaml triggers).

**Affected files:**
- `runner/MainWindow.xaml:236–250` — button style/visibility bindings
- Possibly `runner/MainWindow.xaml.cs:374–418` — Start_Click / Stop_Click may need to trigger a PropertyChanged for `IsRunning` if the binding doesn't pick it up automatically
- `tests/` — N/A (visual)

**Ship solo** (revised 2026-04-18) — X2 already shipped as v1.2.2; X3 gets its own PR + v1.2.3.

---

### X4 — Bundle a real web chat UI (v1.3.x)

**Status:** triaged 2026-04-18
**Scope:** Multi-stage — design pass first, then implementation
**Model:** Opus 4.7 for the design pass
**Stephen confirmed (2026-04-18):** yes, bundle a real chat UI — "a general assistant user will absolutely want some sort of real tangible chat interface," plus post-session chat-log review is a genuine use case even for VR/voice users.

**Symptom (today):** Runner's "Open Chat UI" button (`runner/MainWindow.xaml.cs:557–561`) just does `Process.Start("http://{host}")` which lands on Ollama's "Ollama is running" root page — not a chat UI.

**Approach options to evaluate in the design pass:**
- **Bundle OpenWebUI** — well-known, full-featured, Ollama-native. Heavy: requires Docker or Python runtime; clashes with the portable-SSD posture.
- **Bundle a lightweight static SPA** (recommended starting point) — find a permissively-licensed open-source chat SPA (chatbot-ui-lite, similar), serve it from the Runner's existing Kestrel on 41555 under `/chat/` or similar, talking to the existing `/api/chat` / `/api/chat/stream` endpoints. No new runtime, no new port, no Docker. Ships as static files under `runner/wwwroot/`.
- **Build minimal in-house** — ~500 lines HTML/JS. Smallest dep surface, most work, full design control.

**Security + posture:**
- API-key auth already protects `/api/chat` (when `NetworkRequireApiKey` is set). The chat UI needs to prompt for or receive the key — don't hardcode.
- Static files should be served through the existing Kestrel, not a second web server.
- Chat-log persistence (the main user ask beyond "have a chat window") needs a decision: browser `localStorage`? Server-side store on the SSD? — punt to design pass.

**Affected files (sketch, to be confirmed in design):**
- `runner/Services/RunnerLocalApiService.cs` — add static file middleware + `/chat/` route
- New: `runner/wwwroot/chat/` — static SPA assets
- `runner/MainWindow.xaml.cs:557–561` — button opens `http://{host}:41555/chat/`
- `runner/FreeAiSsd.Runner.csproj` — embed or copy static assets at publish time
- `docs/` — add a "Web Chat UI" section to QUICKSTART

**⚠ Licensing:** any bundled SPA must have a compatible license (MIT, Apache-2, BSD). Flag to Stephen before adding it as a dep.

---

### X5 — GPU/CPU compute indicator (+ optional selector, deferred)

**Status:** triaged 2026-04-18
**Scope:** Two-phase — (1) read-only indicator, (2) optional selector (may never ship)
**Model:** Sonnet 4.6

**Symptom:** User (Radeon RX 9070 XT + gemma2:9b test model) has no visibility into whether Ollama is running inference on GPU or CPU. `PortableConfig.PreferredCompute` exists (`shared/PortableConfig.cs:69`, defaults `"cpu"`) but is never read by the Runner — it's set during prep and has no consumer.

**Phase 1 — read-only indicator (recommended scope for v1.x):**
- After each model load, Runner calls Ollama's `GET /api/ps` (not currently called anywhere in the codebase — verified via grep of `ChatService.cs` and `OllamaLifecycleService.cs`).
- Parse the response: each loaded model has `size` and `size_vram` fields. `size_vram == 0` → CPU only; `size_vram >= size` → full GPU; anything else → hybrid.
- Display in the status area near the model-selector: "CPU" / "GPU" / "Hybrid (GPU: 80%)" or similar.
- No selection, no overriding — just surface the current reality.

**Phase 2 — selector (deferred, may skip):**
- Would require restarting Ollama with `OLLAMA_NUM_GPUS=0` for CPU-only mode. High friction; user has to sit through a model reload.
- Stephen hasn't asked for this — only the indicator. Don't build it unless a real use case emerges.

**Affected files (phase 1):**
- `runner/Services/OllamaLifecycleService.cs` or new `runner/Services/OllamaStatusService.cs` — `/api/ps` client
- `runner/Services/ChatService.cs` — trigger a status refresh on model load completion
- `runner/MainWindow.xaml` — indicator UI next to model selector
- `runner/MainWindow.xaml.cs` — wire the refresh to the UI
- `tests/` — mock HTTP test for `/api/ps` parsing

**⚠ `PreferredCompute` status:** The existing unused field in `PortableConfig` should either get wired up (phase 2) or removed. Flag at phase 1 implementation time which way to go — Stephen likely won't want a dead field sitting in the config.

---

### X1-Redux — Voice/TTS pipeline hang still present after PR #136

**Status:** phase 1 diagnostic branch pushed 2026-04-18 as `diag/x1-redux-send-hang` (never-merge). Awaiting Stephen to reproduce the hang on the SSD and return `%TEMP%\freeai-x1redux-diagnostic.log`. **Blocks v1.2.4 tag.**
**Scope:** Diagnose first, then fix (two-phase, B3-Redux style)
**Model:** Sonnet 4.6 for phase 1 (diagnostic); re-triage for phase 2 once cause is known

**Symptom (v1.2.4 field test, 2026-04-18):**
- Runner crashed on the first TTS attempt from the PTT path — Section 2 of the checklist could not be exercised at all (2a–2g all skipped).
- Section 4a reproduced the hang via the **example-prompt Send button** (text path, not voice): AI replies correctly, but the Send button stays magenta, "generating…" indicator never clears, app transitions to Not Responding, and force-close is required to recover.
- The entire point of PR #136 was to eliminate this "generating…"-stuck state by awaiting `StreamingTtsSpeaker.Completion`. Field behaviour is unchanged or cosmetically identical to the pre-fix state.

**What's known to NOT be the cause (from PR #136 review):**
- `PttVoicePipelineService` does `await ttsSpeaker.Completion` with a try/catch (commit `a9862e3`); the old 60s polling loop is gone.
- Regression test `LiesAboutIsSpeakingTts` passes — if anything re-introduced the old polling branch, that test would fail. So the hang is **not** the original polling path returning.

**Plausible root causes (phase 1 should discriminate):**
1. **`Completion` never signals** — `StreamingTtsSpeaker` exposes `Completion` as a `Task`, but if the underlying TCS is never flipped (e.g. when Piper's stdout/stderr drains differently than expected, or when the example-prompt non-voice path uses a different TTS entry that doesn't wire `PlaybackStopped`), the `await` blocks forever. No timeout on the new path.
2. **Example-prompt Send goes through a different path** — the example-prompt button may invoke `ChatService.Send` on the UI thread or through a code path that doesn't use `PttVoicePipelineService` at all; the hang may be in the chat send/UI state machine, unrelated to TTS completion semantics. Verify which service handles the Send-button click before assuming this is TTS.
3. **Runner startup crash on first TTS attempt** — separate bug from the example-prompt hang, but compounds test coverage. Logs from `%LOCALAPPDATA%\FreeAiSsd\logs` during the crash should identify whether it's a Piper process spawn failure, a missing voice model, or an unhandled exception in `StreamingTtsSpeaker` construction.
4. **UI-thread deadlock** — if any path in the Send → TTS chain captures the WPF SynchronizationContext and then awaits on a task that needs to resume on that same context, the classic WPF deadlock pattern is possible. The try/catch around `await Completion` wouldn't catch this — it's not an exception, it's a stall.

**Phase 1 — diagnostic branch live on `diag/x1-redux-send-hang`:**
- Runner log from Stephen's crashed-TTS attempt pulled (`G:\logs\runner-20260419.log`) — already surfaced a separate bug (see X8) but did not pinpoint the text-Send hang.
- New `runner/Services/X1ReduxDiag.cs`: static file-sink logger at `%TEMP%\freeai-x1redux-diagnostic.log` with timestamps, elapsed-ms, managed thread id.
- Instrumentation points in `MainWindow.xaml.cs`: `Send_Click` (enter / exit / all early-exit branches), `StopTts` (enter / post-Cancel / post-Dispose / exit), `SendStreamingAsync` (enter / pre+post `SendPromptStreamingAsync` / pre+post `Finish` / post-assignResponse / finally enter/exit), token callback (1st + every 20th, entry/exit around `Dispatcher.InvokeAsync`).
- Twin heartbeats: `[watchdog-bg]` via `Task.Run` every 500 ms (process-level), `[ui-hb]` via `DispatcherTimer(Background)` every 500 ms (UI-thread). Gap pattern discriminates process-hang vs UI-thread-starvation vs HTTP-stream-never-ends.
- Confirmed during investigation: example-prompt Send and voice Send **both** go through `Send_Click` → `SendStreamingAsync`. No wrapper. Notably, `SendStreamingAsync` does NOT await `ttsSpeaker.Completion` — so the "indicator never clears" symptom cannot be the `Completion`-never-signals hypothesis; would fire the finally *too early*, not stall it.
- **Awaiting:** Stephen to run the built Release on the SSD, reproduce, and return the diagnostic log. Fix scope decided after reading the log.

**Phase 2 — fix (scope depends on phase 1 findings):**
- Likely options: add a safety timeout on `await Completion` (5–10s, not the old 60s) with explicit logged failure; fix the `TrySetResult` wiring; fix the UI-thread deadlock; or all three. Decide after phase 1.
- Any fix must keep the `LiesAboutIsSpeakingTts` regression test green — we don't want to reintroduce the polling path even as a fallback.

**Affected files (expected; revise per phase 1):**
- `runner/Services/PttVoicePipelineService.cs` — the `await Completion` call site and surrounding state transition.
- `runner/Services/StreamingTtsSpeaker.cs` — the TCS/Completion plumbing.
- `runner/MainWindow.xaml.cs` or the Send-button handler — the example-prompt send path.
- `runner/Services/ChatService.cs` — if the text-only send path routes through here.

**⚠ Release implication:** v1.2.4 tag is **deferred** until this is resolved. Per the checklist's own release rule ("Defer tag if X1 shows any hang regression — that's the whole point of the release"), we do not tag.

---

### X6 — "Create Library" click hangs UI, crashes, library created on reopen

**Status:** triaged 2026-04-18
**Scope:** One-shot (diagnose + fix UI-thread blocking)
**Model:** Sonnet 4.6

**Symptom (v1.2.4 field test Section 4a, 2026-04-18):**
- User clicks "Create Library" in the Runner. UI hangs, app transitions to Not Responding, eventually crashes / is force-closed.
- On next launch, the library is present — i.e. the background work actually completed before or despite the crash, but the UI never recovered.

**Plausible root cause:**
- Library creation is running synchronously on the WPF UI thread (likely file enumeration + initial embedding index build). No `async`/`await` around the long-running step, or the work is kicked off via `Task.Run` but a subsequent `.Result` / `.Wait()` blocks the UI thread.
- Distinct from X1-Redux — the hang is triggered by library creation, not TTS. Keep separate.

**Diagnostic first pass:**
- Identify the Create Library command handler (search for button binding name or `CreateLibrary` in `runner/`).
- Check whether the handler is `async` all the way down. Any `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` on a task that captures the UI SynchronizationContext is the likely culprit.
- Confirm there's a progress indicator / busy state — users shouldn't be guessing whether the app has crashed.

**Affected files (expected):**
- `runner/MainWindow.xaml(.cs)` — Create Library button + handler.
- `runner/Services/LibraryService.cs` (or equivalent) — library creation logic.
- `runner/ViewModels/` — busy-state / progress bindings.

**⚠ UX note:** Even after the blocking fix, Stephen's field-test notes mention the post-create flow is unclear — "ux is unclear of what steps to do after this so user can be unsure if thats all they have to do." Separate UX item, but worth flagging in this fix's PR as a follow-up rather than scope-creeping.

---

### X7 — DCS bindings scan finds aircraft but reports "no custom bindings"

**Status:** triaged 2026-04-18
**Scope:** One-shot (investigate parser / scanner path)
**Model:** Sonnet 4.6

**Symptom (v1.2.4 field test Section 5, 2026-04-18):**
- Runner's DCS bindings scan enumerates installed aircraft correctly.
- Reports "no custom bindings found" despite real `.diff.lua` files existing on disk.
- Concrete repro file: `C:\Users\Kninetimmy\Saved Games\DCS\Config\Input\FA-18C_hornet\joystick\ VKBsim Gladiator EVO R   {4C912ED0-C95D-11f0-8009-444553540000}.diff.lua`

**Prime suspect — path quirks DCS writes:**
- Note the **leading space** in `joystick\ VKBsim` (space between the backslash and the device name).
- Triple-space between `VKBsim Gladiator EVO R` and the `{GUID}`.
- If the scanner uses `Directory.EnumerateFiles` with a restrictive pattern, or normalises/validates filenames before parsing, these could silently drop real files. A glob like `*.diff.lua` should match, but any name-trimming or regex validation on the filename would.
- Also possible: the parser finds the file but fails to parse (leading-space handling, BOM, encoding) and is counted as "no bindings" rather than "parse error." Error vs empty needs to be distinguished.

**Diagnostic first pass:**
- Find the DCS bindings scanner in the codebase (likely `shared/Services/` or `runner/Services/Dcs*`).
- Run it against Stephen's actual `Saved Games\DCS\Config\Input\FA-18C_hornet\joystick\` path with logging to report: files matched by the glob, files attempted to parse, files that parsed successfully, and per-file reasons for parse failure.
- Known-good fixture files live in the test suite — diff behaviour against those to isolate whether the issue is discovery or parsing.

**Affected files (expected):**
- The DCS bindings scanner / parser (exact path TBD at investigation time).
- `tests/` — the existing `DcsBindingParserTests` fixtures are the baseline; add a fixture with a leading-space and GUID-tail filename to cover this case.

**⚠ Watch:** DO NOT change the parser to be lenient in a way that matches non-DCS `.diff.lua` files. The fix should be about correctly discovering and reading the files DCS actually writes, not relaxing input validation.

---

### X8 — Whisper `_transcriptionGate` disposed during model re-init

**Status:** in review — PR #138 (`fix/x8-whisper-semaphore-disposal`, `591a39b`) 2026-04-18. 379/379 tests green, CI green. Merge-scope decision pending (see below).
**Scope:** One-shot minimal fix shipped; review-driven hardening decision pending
**Model:** Sonnet 4.6 (mechanical fix done); Opus for merge-scope decision if we harden

**Symptom (from Stephen's Runner log, v1.2.4 field test):**
- On PTT / voice path, after a Whisper state reset, the next call to `TranscribeStreamAsync` threw `ObjectDisposedException` from `_transcriptionGate.WaitAsync`. Surfaced during X1-Redux log triage — separate bug from the text-Send hang.

**Root cause:**
- `WhisperSpeechToTextService.InitializeAsync` called the public `Dispose()` as a state-reset shortcut. Public `Dispose()` disposes `_transcriptionGate` along with `_factory`/`_processor`. Re-init then left the service "initialized" but with a disposed semaphore, so every subsequent transcription call blew up on first `WaitAsync`.

**Fix (shipped on PR #138):**
- New private `ReleaseModel()` disposes only `_factory` + `_processor` (the re-initable resources).
- Public `Dispose()` calls `ReleaseModel()` then disposes `_transcriptionGate` (full teardown only on service destruction).
- `InitializeAsync` now calls `ReleaseModel()` for its state reset; catch-path on init failure also uses `ReleaseModel()`.
- 4 reflection-based regression tests in `tests/WhisperSpeechToTextServiceTests.cs` pin the contract.

**Review findings (all pending merge-scope decision):**
1. **Gemini (HIGH):** `InitializeAsync` doesn't acquire `_transcriptionGate` before calling `ReleaseModel()`. A concurrent `TranscribeStreamAsync` mid-`await foreach` over `_processor.ProcessAsync` can still hit `ObjectDisposedException` on the processor itself.
2. **Gemini (LOW):** `InitializeAsync_FailsOnMissingModel` test leaks its tempdir on assertion failure — wrap in try/finally.
3. **Codex adversarial (HIGH):** Window-close `MainWindow.Dispose()` (line ~152) calls the service's public `Dispose()` while an in-flight `TranscribeStreamAsync` may still hold the gate. Shutdown needs quiescence (wait for active transcribes to drain, or cancel + await) before disposing.
4. **Codex adversarial (HIGH):** Singleton service has no init lock. Voice UI, HOTAS PTT, and the LAN API can each trigger `InitializeAsync` concurrently — two concurrent init paths can dispose a processor the other just created.

**Decision to make before merge:**
- **Minimal:** merge PR #138 as-is, open X8a for findings #1/#3/#4 and a trivial PR for #2. Tag v1.2.4 sooner.
- **Harden:** fold all four findings into PR #138. Slower tag, but one cohesive fix with fewer follow-ups and no partially-safe interim state.

**Affected files (if hardening path chosen):**
- `runner/Services/WhisperSpeechToTextService.cs` — gate acquisition in InitializeAsync, service-level init lock, shutdown quiescence hook.
- `runner/MainWindow.xaml.cs` — shutdown sequencing (await pipeline drain before Dispose).
- `tests/WhisperSpeechToTextServiceTests.cs` — add concurrency tests + try/finally tempdir cleanup.

---

### F5 — In-app TTS settings UI (backend selector + voice model picker)

**Status:** triaged 2026-04-18
**Scope:** One-shot feature (new Settings surface in Runner)
**Model:** Sonnet 4.6 (design small enough to skip Opus plan unless it grows)

**Why now:** Blocker for field-testing Piper / SAPI / disabled paths (Checklist sections 2c / 2d / 2e — all skipped in v1.2.4 field test because there's no way for the user to switch backends without editing config files). Also user-facing ask: *"i realize i have no idea how to activate piper. i found no settings/options to preload or enable it in the runner or prep app. note to add ability to configure that in software with the various models and descriptions of quality."*

**Intent:**
- A Settings page inside the Runner (not a config file the user edits in Notepad — explicitly rejected by Stephen).
- **TTS backend selector:** Piper / System SAPI / Disabled.
- **Voice model picker** when Piper is selected — list installed voices from `windows/tools/piper/voices/` (or wherever the prereq bundle stages them) with a short quality/size/sample-rate description next to each. Ideally a Play Sample button.
- **SAPI voice picker** when System SAPI is selected — enumerate installed Windows voices via `System.Speech.Synthesis.SpeechSynthesizer.GetInstalledVoices()`.
- Persist selection to `PortableConfig` so it survives restart and syncs to the SSD.

**Existence check (before implementation):**
- `PortableConfig.cs` probably already has some TTS-related fields (there's a voice-model one for Piper somewhere in the PrepApp flow) — audit and reuse rather than adding parallel settings.
- Decide whether this Settings surface lives in the Runner only, or also exposed in the PrepApp's F3 3-tab restructure (likely Runner-only, since Piper voices are downloaded via PrepApp's model-manager flow but selected at runtime).

**Affected files (sketch, confirm at implementation time):**
- New: `runner/SettingsWindow.xaml(.cs)` or a Settings tab inside `MainWindow` — depends on whether Runner tab restructure ships first.
- `runner/Services/PiperTextToSpeechService.cs` — voice-model discovery API.
- `runner/Services/SystemTextToSpeechService.cs` — SAPI voice enumeration.
- `shared/PortableConfig.cs` — TTS fields (or confirm existing fields and extend).
- `shared/ViewModels/` — new SettingsViewModel.

**⚠ Scope discipline:** This is the *TTS* settings UI only. Do not scope-creep into a general-purpose Settings page covering every Runner option. F3 is the surface for broader settings restructure; keep F5 targeted so it unblocks TTS field-testing without waiting on F3.

**⚠ Interaction with X1-Redux:** If X1-Redux phase 1 shows that switching TTS backends is part of reproducing the hang, F5 may get pulled forward into the X1-Redux fix PR. Otherwise F5 slots after v1.2.4 tag.

---

### H1 — Repo spring cleaning

**Status:** shipped 2026-04-18 (PR #137, squash-merged to `main` at `a894862`)
**Scope:** One-shot (housekeeping). Slot **between** the next bug fix and feature add — not in the middle of an in-flight feature branch.
**Model:** Sonnet 4.6 (no design work; mechanical)

**Intent:** Strip stale artefacts that predate the current UX and refresh the two public-facing docs (`README.md`, `docs/QUICKSTART.txt`) so a fresh downloader sees instructions that match what the app actually does now.

**Concrete targets identified 2026-04-18 — confirm each still looks stale before deleting:**

*Deletions — old review dumps and pre-screenshot assets:*
- `CODE_REVIEW.md` (root) — old codex review, predates current code.
- `Claude_code_review.md` (root) — older review dump.
- `docs/CODEX_PROMPTS_UX_FIXES.md` — old prompt notes.
- `docs/images/prep-app-mockup.svg` — pre-screenshot mockup; real screenshots now live next to it (`prep-app-drive-setup.png`, `prep-app-model-manager.png`). Confirm no remaining README/doc refs before removing.
- Anything else in the repo root or `docs/` that looks like a one-off review or migration note older than ~3 months.

*Refreshes — bring in line with current UX:*
- `README.md` — the v1.2.x UX (X2 ScrollViewer, X3 Start/Stop button swap, B3-Redux auto-resume, X1 fix) isn't reflected in the instructions. Don't just append; audit the full "how to use" flow top to bottom. Keep real screenshots, replace any that show old layout (check against live app).
- `docs/QUICKSTART.txt` — same exercise. Ensure step ordering matches what PrepApp actually asks for in 2026-04-18 form.
- Fold in the outstanding **F1 README update** (USB SSD detection fix, backlog line 68) — don't open a separate doc PR for it.

**Deliberately out of scope for H1:**
- Code refactors, dead-code deletion in source files — separate task.
- `agent_docs/` framework files — those live on a different restructure track (memory: "Docs restructure in flight").
- `CLAUDE.md` — also on the docs restructure track.

**⚠ Watch for:**
- Before deleting any `.md`, grep for its filename across the repo — reviews sometimes get linked from README or CI docs.
- Before deleting `prep-app-mockup.svg`, grep for `mockup.svg` in README.md and docs/ to make sure nothing still references it.
- If any deletion surfaces that a file is actually load-bearing (linked from a workflow, referenced in code comments), **keep it and flag for Stephen** rather than silently leaving the reference dangling.

**Exit criterion:** One PR, one commit per logical grouping (deletions in one commit, README refresh in another, QUICKSTART refresh in a third). After merge, `README.md` and `docs/QUICKSTART.txt` both reflect the current v1.2.x UX exactly.

---

### X9 — Encrypted config persistence lifecycle

**Status:** Stage 1 plan locked 2026-04-19 (Opus 4.7 + advisor pass). **Critical.** Stage 2 unblocked.
**Scope:** Multi-concern; single cohesive fix across shared lib + Runner + Prep.
**Model:** Opus 4.7 for planning, Sonnet 4.6 for implementation stages

**Symptom:** On an encrypted SSD, changes made post-unlock silently revert after restart, and a plaintext `portable-config.json` containing secrets (API key, etc.) lives on disk alongside the encrypted blob.

**Root cause (verified against live code 2026-04-18):**
- `PortableConfig.SaveAsync` (`shared/PortableConfig.cs:291-320`) always writes plaintext JSON. The fail-closed guard at 299-306 only blocks when Network Mode + Require API Key is on AND the drive is NOT effectively encrypted. On an encrypted drive the guard *passes*, and plaintext is written anyway.
- `MainWindow.LoadConfig` (`runner/MainWindow.xaml.cs:215-242`) unlocks from the encrypted blob on every startup when `IsEffectivelyEncryptedForWriteGuard` returns true — the plaintext file written by the previous session's saves is ignored.
- `SsdEncryption.EnableConfigEncryptionAsync` (`shared/SsdEncryption.cs:130-195`) is one-way: plaintext → encrypted + state files, then plaintext deleted. No symmetric "save encrypted from in-memory config" path exists, so nothing in Runner or Prep can update the encrypted payload after initial setup.
- Finalize bootstrap (`shared/ViewModels/PrepViewModel.cs:1222-1226`) sets `config.IsEncrypted = true` then calls `SaveConfigAsync` *before* `EnableConfigEncryptionAsync` creates any encrypted artifact. If Network Mode + Require API Key is on at finalize time, the guard throws because encrypted artifacts don't yet exist — finalize fails-closed in that combo.
- `MainWindow.SaveConfigAsync` (`runner/MainWindow.xaml.cs:2084-2108`) is fire-and-forget and unsynchronized; rapid successive calls all target the same `.tmp` path (`PortableConfig.SaveAsync:309`), so concurrent saves can race on `File.Replace` / `File.Move`.

**Locked plan (Stage 1, 2026-04-19):**

*Contract.* New `shared/Services/IConfigStore.cs` + `ConfigStore` concrete owns all load/save. Picks encrypted vs plaintext based on drive state. Every existing `PortableConfig.SaveAsync` / `IModelService.SaveConfigAsync` caller routes through it.

```csharp
interface IConfigStore {
    Task<PortableConfig?> LoadAsync(string ssdRoot, CancellationToken ct);
    Task SaveAsync(string ssdRoot, PortableConfig config, CancellationToken ct);
    void UnlockSession(UnlockMaterial material);
    Task FlushAsync(TimeSpan timeout);   // drain pending saves before LockSession
    void LockSession();                  // zeroes DerivedKey bytes
    bool IsSessionUnlocked { get; }
}

sealed record UnlockMaterial(byte[] DerivedKey, byte[] Salt, int Iterations, string Scheme);
```

*Key caching.* Cache the 32-byte derived key + salt + iterations + scheme. **Never** cache the password. Key lives in a private `byte[]`, zeroed via `CryptographicOperations.ZeroMemory` on `LockSession()`. Reuse the existing salt across saves; rotate a fresh random GCM nonce per save (safe because (key, nonce) pairs stay unique). Process-kill leaves the key in memory pages until OS reclaim — inherent; do not try to patch around it.

*Symmetric encrypted save.* New `SsdEncryption.SaveEncryptedConfigAsync(ssdRoot, config, material, ct)`. Serializes `config` in memory, encrypts with cached key + fresh nonce, writes **both** the encrypted blob and the state file atomically: both to `.tmp`, rename encrypted first, rename state second; if the second rename fails, roll back the first. No plaintext ever touches disk.

*In-memory finalize overload.* New `SsdEncryption.EnableConfigEncryptionAsync(ssdRoot, config, password, ct)` that accepts the `PortableConfig` object directly. `PrepViewModel.FinalizeAsync` uses this; no plaintext-then-encrypt dance. Eliminates the Network-Mode-blocks-finalize bug as a side effect.

*Unlock API.* `SsdEncryption.TryUnlockPortableConfig` grows an additional out-param (or sibling `TryUnlockPortableConfigWithMaterial`) returning the `UnlockMaterial` alongside the decrypted `PortableConfig`. Callers pass that straight to `ConfigStore.UnlockSession`.

*Serialized save queue.* `ConfigStore` owns a `SemaphoreSlim(1,1)`; all saves drain sequentially. No more `.tmp` races.

*Shutdown drain.* `MainWindow.OnClosing` calls `await ConfigStore.FlushAsync(TimeSpan.FromSeconds(5))` **before** `LockSession()`. Bounded so a stuck save can't hang app close — log and proceed on timeout. Prevents queued-save-after-key-zero → silent edit loss.

*Migration (upgrade from broken v1.2.x).* Every post-unlock save before this fix silently landed in plaintext while the encrypted blob went stale, so the plaintext on disk **may hold the user's most recent edits**. On first unlock after this ships, detect stale plaintext beside the encrypted blob and compare `File.GetLastWriteTimeUtc`:
- Plaintext newer → modal dialog: *"Found unsaved edits from before the security fix. Load them, re-encrypt, then delete the plaintext?"* Default Yes. Loads plaintext, merges over the just-unlocked config, saves via `ConfigStore` (re-encrypt), deletes plaintext.
- Encrypted newer → modal dialog: *"Found a plaintext config from before the security fix. Delete it?"* Default Yes.
- Never silently delete. Stephen sees the prompt every time.

*Fail-closed guard.* `ConfigStore` enforces: Network Mode + Require API Key + would-write-plaintext → throw `InvalidOperationException`. Semantics unchanged from today; only the chokepoint moves.

**Affected files:**
- `shared/PortableConfig.cs` — deprecate direct `SaveAsync` callers; keep load + serialize helpers.
- `shared/SsdEncryption.cs` — add `SaveEncryptedConfigAsync`, in-memory encrypt overload, `TryUnlockPortableConfigWithMaterial`.
- **New:** `shared/Services/IConfigStore.cs`, `shared/Services/ConfigStore.cs`, `shared/Services/UnlockMaterial.cs`.
- `runner/MainWindow.xaml.cs` — route all saves through `IConfigStore`; capture `UnlockMaterial` on unlock; `OnClosing` calls `FlushAsync` then `LockSession`.
- `runner/Services/DocumentOperationsService.cs:130-133` — use `IConfigStore`.
- `prep-app/Services/ModelService.cs` — use `IConfigStore`.
- `prep-app/Services/ReadinessService.cs:92,98` — use `IConfigStore`.
- `shared/ViewModels/PrepViewModel.cs:1212-1226` — encrypt from memory; no plaintext intermediate.
- `tests/PortableConfigSaveGuardTests.cs:73-96` — rewrite. Preserve the "Network Mode + unencrypted + API key → refuse" axis; replace the plaintext-after-encryption axis with encrypted-round-trip semantics.
- **New tests:** real-crypto fixtures (no mocks). Post-unlock edit round-trip on encrypted drive; concurrent save serialization; finalize + Network Mode + API key; migration with plaintext-newer-than-encrypted; migration with encrypted-newer-than-plaintext; flush-on-close drains queued save.

**Staging:**
- **Stage 1 — plan (Opus). DONE 2026-04-19.**
- **Stage 2 — shared lib (Sonnet 4.6):** `IConfigStore` + `ConfigStore` + `UnlockMaterial`, symmetric encrypted save with two-file atomic commit, in-memory encrypt overload, `TryUnlockPortableConfigWithMaterial`. Real-crypto unit tests. No wiring yet.
- **Stage 3 — Runner wiring (Sonnet 4.6):** route Runner saves through store; capture `UnlockMaterial` on unlock; `OnClosing` flush+lock. Integration test with real encrypted-drive fixture.
- **Stage 4 — Prep finalize + migration + guard rewrite (Sonnet 4.6):** encrypt-from-memory finalize; modal migration prompt with mtime-aware branches; guard test rewrite. End-to-end test: finalize + Network Mode + API key.

**⚠ Security / data safety:**
- No plaintext on disk at any transitional step. In-memory only until encrypted payload is written.
- Key bytes: in-process only; zeroed via `CryptographicOperations.ZeroMemory` on `LockSession`; never logged, never serialized. Process-kill leaves key in memory pages until OS reclaim — inherent.
- Two-file atomic commit for encrypted blob + state file. Roll back first rename if second fails.
- Shutdown flush bounded at 5s — prefer lost edit on stuck save over hung app close; log the timeout.
- Migration prompt is **always** modal and **always** user-confirmed. Never silently delete or silently overwrite. Plaintext-newer branch is the one that matters for Stephen's field drive.

---

### X10 — Document replacement + rebuild consistency

**Status:** triaged 2026-04-18 (Codex deep-review intake). **High.**
**Scope:** One cohesive fix; transactional replace + rebuild-from-stored.
**Model:** Sonnet 4.6

**Symptom:**
- Re-ingesting a changed document leaves stale chunks in the vector DB and stale stored files in the library folder.
- "Rebuild index" silently drops any document whose original source file has been moved or deleted, even though the SSD still has the stored library copy.

**Root cause (verified 2026-04-18):**
- Stored filenames are `{sha[..12]}_{fileName}` (`shared/Documents/DocumentIngestor.cs:70`). When content changes, the SHA prefix changes, producing a new `StoredRelativePath`.
- `VectorIndex.UpsertFileChunks` (`shared/Documents/VectorIndex.cs:303-338`) deletes rows keyed on the *new* `storedRelativePath`. Old rows tied to the previous path survive. Old stored file on disk also survives — nothing removes it on replacement.
- `DocumentIngestor.RebuildIndexAsync` (`shared/Documents/DocumentIngestor.cs:285-296`) enumerates `manifest.Files.Select(f => f.SourceOriginalPath).Where(File.Exists)` — i.e. originals, not the stored library copies. Moving/deleting originals breaks rebuild even though the SSD is self-contained.
- Per-file ingestion failure ordering: vectors are committed (`DocumentIngestor.cs:189`) before the manifest save (`:208`). The catch block at `:210-219` deletes the staged file but not the just-written vectors, so late I/O failure can leave vectors orphaned from manifest state.

**Fix:**
- On replacement in `IngestFilesAsync`: capture `current.StoredRelativePath` *before* overwriting it; after the new vectors + manifest commit successfully, delete the old vectors (via `VectorIndex.RemoveFile(libraryId, oldStoredRelativePath)`) and the old stored file on disk.
- `RebuildIndexAsync` rebuilds from `StoredRelativePath` under the library folder, not `SourceOriginalPath`. Re-parse and re-embed from the stored copy. The original path becomes informational metadata only.
- Tighten per-file transactionality: either (a) save manifest before committing vectors so a failed manifest save leaves no orphaned vectors, or (b) on exception, remove the just-written vectors as part of rollback. Pick whichever matches the existing transactional story best.
- Watch-folder sweep (`SweepFoldersAsync`, `:256-283`) uses `EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true }` or catches per-subtree so one protected folder doesn't abort the whole sweep.

**Affected files:**
- `shared/Documents/DocumentIngestor.cs` — replacement cleanup, rebuild from stored, sweep resilience, per-file rollback.
- `shared/Documents/VectorIndex.cs` — confirm `RemoveFile` is sufficient for old-path cleanup, or add a helper.
- `tests/` — new tests: changed-file replacement removes old vectors + old stored file; rebuild works with originals missing; watch-folder sweep survives inaccessible subtree; late-failure rollback leaves no orphaned vectors.

**⚠ Watch for:**
- Don't change `StoredRelativePath` naming scheme — it's baseline for tests and existing SSDs. Fix the cleanup, not the key.
- `DocumentFileEntry` has no "previous stored path" field. Capture locally inside the replace loop; don't mutate the entry until after cleanup succeeds.
- Rebuild from stored means the embedding model must be compatible with what generated the existing stored files. If a user changes models, rebuild may still need to re-embed from scratch — confirm behaviour matches user expectations.

---

### X11 — Companion keyboard PTT + first-run validation

**Status:** triaged 2026-04-18 (Codex deep-review intake). **High.**
**Scope:** One-shot; three related companion defects in a single PR.
**Model:** Sonnet 4.6

**Symptoms:**
1. Keyboard fallback PTT records only ~100 ms regardless of actual key-hold duration. Hotkey registration failures are silent.
2. Canceling the first-run Settings dialog still starts the app in an invalid state — HOTAS polling kicks off against default button 0 on null device.
3. API key is shown in plain text in the Settings window, editing is disabled once a key exists, and blank textbox on save silently preserves the old key.

**Root cause (verified 2026-04-18):**
- `companion/KeyboardPttHotkey.cs:25-47`: uses `RegisterHotKey` (which delivers `WM_HOTKEY` on key-down only) and fakes release with `Task.Delay(100)`. `RegisterHotKey` return value is discarded at line 27. This is not PTT — it's a 100 ms one-shot trigger that lies about being PTT.
- `companion/CompanionRuntime.cs:64-67`: if `_config.IsComplete()` returns false, `OpenSettings()` is called but control falls through to `InitializeBindings()` and the health loop regardless of whether the user cancelled the dialog.
- `companion/CompanionRuntime.cs:460-470` (`ParseHotasBinding`): empty/malformed input falls through with `deviceName=null`, `buttonIndex=0`. `_hotas.Start(null, 0)` then polls for nothing in particular.
- `companion/SettingsWindow.xaml` uses a `TextBox` for the API key (plaintext on-screen); save logic preserves old key when textbox is blank, making rotation/reset awkward.
- `shared/Models/CompanionConfig.cs:43-47`: `IsComplete()` hard-requires an API key even when the Runner may not require one (Network Mode off, or auth disabled).

**Fix:**
- Replace `RegisterHotKey`-based approach with a low-level keyboard hook (`SetWindowsHookEx` with `WH_KEYBOARD_LL`) that delivers real `WM_KEYDOWN` / `WM_KEYUP` events. Fire `_onPress` on down, `_onRelease` on up. Log registration failure; surface to UI.
- In `CompanionRuntime.Start`: if config is incomplete after `OpenSettings()` returns (user cancelled or saved incomplete data), show a clear error and either block startup (tray icon with "Configure to continue" state) or exit cleanly. Do not start `InitializeBindings` / health loop against invalid config.
- Validate HOTAS binding before starting the poll — refuse to start with null device or button-0-default fallback. Surface the invalid-binding state to the user.
- Replace the API key `TextBox` with a `PasswordBox`. Add an explicit "Replace key" / "Clear key" flow; blank textbox means "no key," not "keep existing."
- Make `CompanionConfig.IsComplete()` conditional: if the Runner's server has Network Mode off or auth disabled, an API key is not required. Detect via health probe at first-run setup, or let the user explicitly mark "server does not require a key."

**Affected files:**
- `companion/KeyboardPttHotkey.cs` — rewrite around low-level hook.
- `companion/CompanionRuntime.cs:55-72, 83-99, 460-470` — startup gating, binding validation.
- `companion/SettingsWindow.xaml` + `.xaml.cs` — `PasswordBox` + explicit reset flow.
- `shared/Models/CompanionConfig.cs:43-47` — conditional completeness check.
- `tests/CompanionConfigTests.cs` — new coverage for conditional completeness; first-run cancel behaviour; binding validation.

**⚠ Watch for:**
- Low-level keyboard hook runs on the UI thread and must be non-blocking. Dispatch to a background worker for any non-trivial work in `_onPress`/`_onRelease` handlers.
- `SetWindowsHookEx` with `WH_KEYBOARD_LL` captures *all* key events system-wide. Be surgical: only intercept the configured PTT key; pass everything else through unchanged.
- API key masking in UI: do NOT log the key anywhere — not in `CompanionLog`, not in error messages, not in health probe debug output.

---

### X12 — DownloadManager verify-before-move

**Status:** triaged 2026-04-18 (Codex deep-review intake). **Medium (security-adjacent).**
**Scope:** One-shot.
**Model:** Sonnet 4.6

**Symptom:** A corrupted or tampered download lands at its final destination path *before* SHA-256 verification runs. Mismatch throws, but the bad file is already in place.

**Root cause (verified 2026-04-18):**
- `shared/DownloadManager.cs:95-101`: `File.Move(tempPath, request.DestinationPath, overwrite: true)` runs before `VerifySha256(request.DestinationPath, ...)`. If verification throws, the bad file sits at the destination.

**Fix:**
- Reorder: close stream → `VerifySha256(tempPath, expected)` → `File.Move` only on success.
- On mismatch: delete temp file and throw. If the destination already exists from a prior successful download, do NOT overwrite it based on a verification that hasn't run yet.
- If resume semantics (the existing `FileMode.Append` path at `:80`) are affected by this, confirm resumed partial downloads still verify correctly before being promoted.

**Affected files:**
- `shared/DownloadManager.cs:75-102`.
- `tests/` — new test: mismatched SHA leaves no file at destination and temp is cleaned up.

**⚠ Watch for:**
- Callers (PrereqFetch tool, `OllamaPackageService`) that expect the file to exist at `DestinationPath` after `DownloadAsync` returns — verification failure becomes the exception path, destination file is absent. Confirm no caller treats "destination exists" as implicit success without catching the throw.

---

### X13 — Chat/STT surface real failures

**Status:** triaged 2026-04-18 (Codex deep-review intake). **Medium.**
**Scope:** One-shot; two services, one PR.
**Model:** Sonnet 4.6

**Symptom:** Backend / transport failures in `ChatService` and `WhisperSpeechToTextService` are flattened into empty-string success. Callers (UI, LAN API) cannot distinguish "model returned no answer" from "system failed" — users see silent empty responses instead of actionable errors.

**Root cause (verified 2026-04-18):**
- `runner/Services/ChatService.cs:46-50` — catch returns `new ChatResponse(string.Empty, null, false)` on any exception. Streaming path at `:115-125` is slightly better (injects `[Error: …]` into the token stream when partial content exists) but still returns success-shaped object.
- `runner/Services/WhisperSpeechToTextService.cs:127-131` — catch returns `string.Empty` on exception.

**Fix:**
- Add `ChatResult` / `TranscriptionResult` record types that are either success (with payload) or failure (with error message + optional inner exception). Callers switch on the union.
- `RunnerLocalApiService` translates failure results to proper HTTP error responses (500 with error body, or 502 if the failure is clearly a downstream issue like Ollama unreachable).
- UI (Runner MainWindow) translates failure results to a visible error log line + appropriate state transition (don't leave "generating…" stuck).
- Keep the streaming `[Error: …]` in-band injection as a UX nicety *in addition to* a structured failure return so the API consumer also sees the error.

**Affected files:**
- `runner/Services/IChatService.cs`, `ChatService.cs` — new result type; update all callers.
- `runner/Services/ISpeechToTextService.cs`, `WhisperSpeechToTextService.cs` — same.
- `runner/Services/RunnerLocalApiService.cs` — error response mapping.
- `runner/MainWindow.xaml.cs` — UI error handling at Send / STT call sites.
- `runner-cli/RunnerApiClient.cs` — surface server error responses as CLI errors.
- `tests/RunnerLocalApiServiceTests.cs`, `ChatServiceTests.cs` (new), `WhisperSpeechToTextServiceTests.cs` — regression tests proving backend failure propagates end-to-end, not empty success.

**⚠ Watch for:**
- Existing tests may assume empty string = failure. Update, don't preserve — the whole point is distinguishing empty vs failure.
- Don't leak backend URLs, auth headers, or stack traces into user-facing error text. Log rich details; show concise messages.

---

### H2 — Repo hardening batch (Codex deep-review low-severity sweep)

**Status:** triaged 2026-04-18 (Codex deep-review intake). **Low.**
**Scope:** One-shot housekeeping batch. Slot between bug fixes, not mid-feature.
**Model:** Sonnet 4.6 (mechanical)

**Intent:** Fold all Low-severity Codex findings into one cohesive housekeeping PR so they don't each burn a separate round-trip.

**Concrete targets:**

1. **`build.ps1:35-38`** — staged `runner-publish` directory is reused without cleanup. Removed artifacts linger and can be shipped. Fix: `Remove-Item -Recurse -Force` before `New-Item` / `Copy-Item`.
2. **`shared/SsdLogger.cs:40-43`** — unsynchronized `File.AppendAllText`. Add a lock or route through a serialized writer. Match the pattern already used by `companion/CompanionLog.cs`.
3. **`shared/SystemResources.cs`, `shared/DriveInspector.cs`** — `System.Management` / WMI calls are Windows-only but shared project builds cross-platform. Add `[SupportedOSPlatform("windows")]` attributes or `OperatingSystem.IsWindows()` guards. Resolves the CA1416 warnings.
4. **`.github/workflows/build.yml`** — first-party GitHub actions are tag-pinned; pin to exact SHAs per the repo's own TODO (lines ~55-56, 139, 155, 202, 233-238, 307-311 per Codex; verify before changing).
5. **`README.md`** — drift: test count says 375 (actual: 380+), target framework says net8.0 (tests are net10.0), offline voice wording is inconsistent across sections. Audit against live state and refresh.
6. **`tests/RunnerLocalApiServiceTests.cs:282-283`** — uses `.Result` which trips an xUnit analyzer warning. Convert to `await` in an `async` test.

**Deliberately NOT in H2:**
- Any of the X9/X10/X11/X12/X13 items — they get their own PRs.
- The oversized `runner/MainWindow.xaml.cs` and `shared/ViewModels/PrepViewModel.cs` — splitting those is a separate, larger refactor task (slot into F3 or a future R2).

**Affected files:** as listed above.

**Exit criterion:** One PR, one commit per logical grouping (build.ps1 fix; platform guards; workflow pinning; docs refresh; test cleanup). CA1416 warnings clean; README reflects live state; no new test failures.
