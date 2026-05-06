# Project Backlog

## How to pull from this file

When I ask you to "tackle section X" or "pick up backlog item Y":
1. Read only the item in question plus any items it references.
2. Check the item's status marker â€” skip if `done` or `blocked`
   without first unblocking.
3. Re-read `project_arch.md` if the item touches architecture.
4. Check `project_decisions.md` for constraints that shape the
   approach.
5. Before implementing, confirm scope with me if the item is more
   than a few weeks old â€” conditions may have changed.

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
the user-facing change â€” not a changelog dump. The current README
was refreshed in PR #123 (`796719d`) with real screenshots; that's
the style to match.

## Priority order (most recent triage: 2026-04-18 â€” v1.2.1 field test)

**v1.2.x patch stream (each ships as its own PR + release â€” see decision 2026-04-18):**
1. **B3-Redux phase 2** â€” shipped 2026-04-18 (PR #133, `b20dd67`).
2. **X2** â€” Runner window ScrollViewer â€” shipped 2026-04-18 (PR #134, `5247d2a`, released as v1.2.2).
3. **X3** â€” Runner Ollama Start/Stop button state swap â€” shipped 2026-04-18 (PR #135, `353e54b`, released as v1.2.3).
4. **X1** â€” voice pipeline hang after TTS completes â€” shipped 2026-04-18 (PR #136, `a9862e3`, cut as v1.2.4). **Field test 2026-04-18 FAILED** â€” hang reproduces on example-prompt Send; v1.2.4 tag deferred. See **X1-Redux**.

**Shipped post-v1.2.4 (rolls into v1.2.5):**
4a. **X8** â€” shipped 2026-04-18 (PR #138, merged commit `fa34828`). Initial fix (`591a39b`) split model teardown from `Dispose()` so the shared semaphore survived re-init; follow-up (`9c3a054`) folded in the three races Gemini + Codex flagged â€” single `_lifecycleGate` serializes Init / Transcribe / Dispose, `_shutdownCts` drains in-flight `ProcessAsync` on window close, and `CancellationToken` is now threaded through `ISpeechToTextService` into PTT + network API callers.

**Dormant (could not reproduce on 2026-04-19 v1.2.5 field test):**
4b. **X1-Redux** â€” hang regression from v1.2.4 did not reproduce across 10+ varied prompts on `main` at commit `54b276a`. Chat, TTS, library creation, and PTT all healthy. Diag branch `diag/x1-redux-send-hang` stays on remote unmerged, ready if the hang returns. No longer blocking v1.2.6 tag. **Status updated 2026-04-19.**

**Codex deep-review findings (intake 2026-04-18 — slot between X1-Redux and feature queue):**
5. **X9** — encrypted config persistence lifecycle (Critical; Opus planning)
6. **X10** — document replacement + rebuild consistency (High) *(plan locked 2026-04-19; queued for Sonnet — see plan doc)*
7. **X11** — companion keyboard PTT + first-run validation (High)
8. **X12** — download verify-before-move (Medium, security-adjacent)
9. **X13** — chat/STT surface real failures (Medium) *(expanded 2026-04-19 RAG audit: + RAG retrieval-failure variant)* — **done** (PR #162, `40f41fd`)
10. **H2** — repo hardening pass (housekeeping batch)

**After hardening batch ships — reordered 2026-04-19 (RAG audit; see decision):**
11. **X21** — embedding provenance + compat gating — **done** (PR #157, `449ec2e`)
11a. **X21b** — PrepApp reindex prompt — **done** (PR #158, `92625a9`)
12. **F3** — PrepApp 2-tab restructure + UX simplification — **done** (PR #164, `953fb1b`)
12a. **H3** — F3 manual PrepApp/FTUE smoke follow-up — deferred; post-merge validation pass if we want one more Windows check before the next feature branch
13. **F4** — profile FTUE in PrepApp + companion install target selector (multi-stage; Stage 1 done in PR #166, Stages 2-4 pending)
14. **B2** — build LAN discovery (multi-stage, Opus planning; can run in parallel with F4)
15. **F2** — live model list fetch (smaller feature)
16. **R1 Stage 2** — `/api/documents` + `/api/documents/reindex` server endpoints + `/docs` / `/reindex` CLI commands (follow-up to R1 Stage 1)

**v1.3.x territory:**
18. **X4** â€” Bundle a real web chat UI (static SPA served from Runner's Kestrel, reusing existing `/api/chat` endpoints)
19. **Runner tab restructure** â€” follow-up to X2 once F3's tabbed aesthetic lands
20. **X5** â€” GPU/CPU compute indicator (read-only first, selector later)

**Field-test surface from v1.2.4 walkthrough (2026-04-18):**
- **X6** â€” "Create Library" click hangs UI, crashes, library created on reopen (separate hang from X1). *Did not reproduce on 2026-04-19 v1.2.5 field test; leave open until retested on fresh SSD.*
- **X7** â€” DCS bindings scan finds aircraft but reports "no custom bindings" against real `.diff.lua` files on disk.
- **F5** â€” No in-app TTS settings UI (backend selector + voice-model picker). Blocks field-testing Piper/SAPI/disabled paths.

**Field-test surface from v1.2.5 walkthrough (2026-04-19):**
- **X14** â€” 50 MB upload limit silently rejects files with no user-facing hint (140 MB PDF case). Small UX fix.
- **X15** â€” revisit RAG file-size and chunk-size caps so large DCS airframe manuals (Chuck's Guides, 120-160 MB, 800-900 pages) ingest cleanly. Investigation + tuning pass â€” paired follow-up to X14. Not near-term; slot after F3 once the v1.2.x patch stream clears. *(rescoped 2026-04-19 RAG audit into 4 stages â€” see X15 entry)*

**RAG audit backlog (2026-04-19 plan session â€” slot into v1.3.x after F3; full plan at `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`):**
- **X18** â€” ingest observability (Sonnet, quick; surfaces FailedChunks, textless pages, parse errors in UI)
- **X15 (expanded)** â€” streaming + batched ingest + background job (Opus)
- **X19** â€” hybrid retrieval: dense + FTS5 lexical + neighbor expansion (Opus; eval harness first)
- **X20** â€” section-aware chunking + richer metadata (Opus; depends on X21 schema)
- **X22** â€” prompt packing + grounding enforcement (Sonnet)
- **X23** â€” representative test fixtures (Sonnet; public-domain PDFs)
- **X17** â€” textless-page diagnostic only (Sonnet; full OCR deferred per decision 2026-04-19)
- **X10-Redux** â€” stable document GUID (deferred; revisit only if X10 path-capture insufficient)

**Also outstanding:** README update for F1 (USB SSD detection fix). Small, can be bundled with any doc PR â€” or folded into H1 below.

**Housekeeping â€” slot between bug fixes/features, not during in-flight work:**
- **H1** â€” shipped 2026-04-18 (PR #137, `a894862`). 4 stale files deleted; README + QUICKSTART refreshed for v1.2.x UX. X1 deliberately omitted pending X1-Redux.

`I1` (architecture diagram) is folded into F4 Stage 1 â€” no standalone entry.

Items `B1`â€“`F4` below were triaged from Stephen's `Downloads/# Free-AI-SSD Project TODO.md` (dictated-while-driving notes â€” treat TODO assumptions with skepticism). Items `X1`â€“`X5` were added 2026-04-18 from the v1.2.1 field-test findings (`C:\Users\Kninetimmy\Documents\ai ssd issues.txt`). Each section is addressable independently.

---

## Items

### R1 â€” Runner CLI REPL (headless SSH/Tailscale client)

**Status:** Stage 1 done (PR #130, `bb59a6c`); Stage 2 triaged
**Scope:** Multi-stage (2 stages)
**Model:** Sonnet 4.6 for Stage 1; Sonnet 4.6 for Stage 2
**Stephen confirmed (2026-04-17):** yes, option 3 (thin HTTP client against existing `RunnerLocalApiService`, not a headless host).

**Intent:** New `runner-cli/` console project (`net8.0`, not `-windows`) that speaks to a running Runner's LAN API. Primary use case: SSH from iPad via Tailscale into the Runner host, run a terminal REPL against the existing chat / RAG pipeline.

**Existence:** Server-side endpoints already present in `runner-core/Services/RunnerLocalApiService.cs`:
- `GET /api/health` (unauth) â€” line 112
- `GET /api/models` â€” line 121
- `POST /api/chat` â€” line 134 (returns `{responseText, sources, usedRagContext}`)
- `POST /api/chat/stream` â€” line 149 (NDJSON: `start` â†’ many `token` â†’ `complete`)
- Auth via `Authorization: Bearer` or `X-API-Key` when `NetworkRequireApiKey` is set.

No "list documents" or "reindex" endpoint exists today. See Stage 2.

**Config discovery (industry-standard precedence):** `--url` flag > `FREEAI_URL` env > hardcoded `http://127.0.0.1:41555`. Same pattern for `--api-key` / `FREEAI_API_KEY` (no default).

**Security:**
- API key only from env or flag â€” never logged, echoed, or persisted.
- URL parsed with `Uri.TryCreate`, scheme restricted to `http`/`https`.
- No shell-outs, no `Process.Start` â€” pure HTTP client.

**Staging:**

**Stage 1 â€” REPL v1 (this PR):**
- New project `runner-cli/FreeAiSsd.RunnerCli.csproj` (`net8.0`, `Exe`, `PublishSingleFile`, `SelfContained`).
- Add to `FreeAiSsd.sln` + `build.ps1` publish list.
- `Program.cs` â€” arg parsing, env resolution, REPL entry.
- `RunnerApiClient.cs` â€” typed wrapper over health/models/chat/chat-stream.
- `Repl.cs` â€” prompt loop, slash-command dispatch (`/help`, `/models`, `/model <name>`, `/health`, `/clear`, `/quit`; also `quit`, `exit`, EOF, Ctrl-C exit cleanly).
- Plain `Console.ReadLine()` â€” **no readline NuGet** (SSH-robust, avoids new dep).
- Streaming: write tokens as they arrive; print `â€” sources: [...]` or `â€” (no RAG context)` on `complete`.
- Ctrl-C during stream cancels that request only; second Ctrl-C at idle prompt exits.
- Tests in existing `tests/` project: mock `HttpMessageHandler` to verify NDJSON parsing, auth header wiring, slash-command dispatch.
- `docs/QUICKSTART` snippet: "Connecting over SSH/Tailscale."

**Stage 2 â€” Document management endpoints + CLI commands (follow-up PR):**
- New server endpoints on `RunnerLocalApiService`:
  - `GET /api/documents` â€” list ingested documents (name, path, chunk count, last-modified). Consumes `DocumentOperationsService`.
  - `POST /api/documents/reindex` â€” trigger re-ingestion. Return a job id + status endpoint, or block until complete for small libraries.
- New CLI commands: `/docs`, `/reindex`.
- Tests: server-side endpoint tests + CLI command tests.
- Reuse existing auth middleware â€” these endpoints sit behind the same API-key gate.

**Minor tech debt observed (not in scope):** `41555` appears as a literal in `PortableConfig.cs:122`, `CompanionConfig.cs:8`, `PrepViewModel.cs:42`. A `SsdDefaults.RunnerApiPort` constant in `shared/` would prevent drift.

**Affected files (Stage 1):**
- New: `runner-cli/FreeAiSsd.RunnerCli.csproj`
- New: `runner-cli/Program.cs`, `runner-cli/RunnerApiClient.cs`, `runner-cli/Repl.cs`
- `FreeAiSsd.sln` â€” add project
- `build.ps1` â€” add publish step
- `tests/FreeAiSsd.Tests.csproj` + new test files
- `docs/QUICKSTART.md` (or equivalent) â€” SSH usage section

---

### B2 â€” Build LAN discovery (Runner broadcasts, Companion listens) + relocate host IP field

**Status:** triaged
**Scope:** Multi-stage (4 stages)
**Model:** Opus 4.7 for planning
**Stephen confirmed (2026-04-17):** yes, build discovery.

**Existence:** Host IP field confirmed in `prep-app/MainWindow.xaml:380-397` (`CompanionHostAddress` + `CompanionHostPort`) â€” bound into `Finalize` to pre-write `companion-config.json` onto the SSD (`PrepViewModel.cs:863-896`). No discovery code exists in runner/, companion/, or shared/. `NetUtils.cs` is just loopback port availability checking. Companion (`CompanionRuntime.cs`) requires `HostAddress` to be set â€” no fallback, no probe.

**Design decisions needed (surface before implementation):**
- **Protocol:** UDP broadcast (simple, firewall-friendly on LAN) vs mDNS (Bonjour, more robust but heavier). UDP broadcast recommended â€” matches project's "keep it simple" posture and works without extra dependencies.
- **Port:** Pick a dedicated discovery port (not 11434 Ollama, not 41555 Runner API). Suggest 41556 or similar.
- **Payload:** Runner advertises `{hostname, ip, runnerApiPort, apiKey-fingerprint-or-nothing}`. Companion matches and auto-fills settings.
- **Security:** Discovery should NOT leak the API key. Companion still validates via `/api/health` with the key the user has configured.

**Affected files:**
- New: `shared/Services/LanDiscoveryBroadcaster.cs` (Runner side) + `shared/Services/LanDiscoveryListener.cs` (Companion side) â€” or single file with both
- `runner-core/Services/RunnerLocalApiService.cs` â€” kick off broadcast when API starts
- `runner/MainWindow.xaml(.cs)` + ViewModel â€” Advanced Options section with manual host IP fallback (per TODO)
- `companion/CompanionRuntime.cs` â€” consume discovery on startup; fall back to manual settings if not found
- `companion/SettingsWindow.xaml(.cs)` â€” "Searching â†’ Found [hostname @ ip] / Not Found" status + "Retry Discovery" button + manual entry inline
- `prep-app/MainWindow.xaml:380-397` â€” once discovery is in, the PrepApp host IP field can be gated behind Advanced Options (or removed entirely, since discovery replaces the need to pre-configure)
- `shared/ViewModels/PrepViewModel.cs:863-896` â€” adjust companion-config staging accordingly
- `tests/` â€” discovery tests with mocked UDP socket

**Staging:**
- **Stage 1** — done. Shipped in PR #166: two-machine architecture explainer as Step 1 of FTUE overlay; profile selection in PrepApp FTUE; Finalize writes `ActiveProfile` and applies `ProfileDefaults`; Runner no longer blocks first run on a required profile prompt.
- **Stage 2** â€” Wire Runner to broadcast on API start; wire Companion to listen on startup. Integration-test on two machines.
- **Stage 3** â€” Companion Settings UX (searching / found / not found / retry / manual fallback).
- **Stage 4** â€” Relocate/remove PrepApp host IP field; Runner Advanced Options manual-entry fallback.

---

### B3 â€” "Format & Prepare Drive" button actually formats

**Status:** shipped 2026-04-17 (PR #131, `efc5f56`) â€” **REOPENED 2026-04-18** as **B3-Redux** below. Field test showed the format command runs to exit 0 but the drive isn't actually wiped (pre-existing `my library` folder persists, volume label never changes). Keep this section for historical context; see B3-Redux for the active work.
**Scope:** One-shot
**Model:** Sonnet 4.6
**Stephen confirmed (2026-04-17):** yes, format to correct FS, then ensure folder structure.

**Existence:** Bug confirmed. `FormatPrepareAsync` (`PrepViewModel.cs:741-790`) **does not format**. It only calls `_driveService.EnsureSsdStructure(root)` (folder layout â€” keep this, it's already correct) and saves a fresh `PortableConfig`. No `format.exe`, no `diskpart`, no `Format-Volume` PowerShell call anywhere in the repo (verified via grep). The `VolumeLabel` TextBox binding exists in `MainWindow.xaml:356-360` but is never consumed by `FormatPrepareAsync` â€” dead binding.

**Intended flow:** Format drive â†’ `EnsureSsdStructure` â†’ save config. The folder-structure piece already exists and works; only the format step is missing.

**Affected files:**
- `shared/Services/IDriveService.cs` â€” add `FormatAsync(string root, string label, string fileSystem, CancellationToken)` method
- `prep-app/Services/DriveService.cs` â€” implement via `ProcessRunner.ArgumentList` + PowerShell `Format-Volume -DriveLetter X -FileSystem NTFS -NewFileSystemLabel ... -Confirm:$false`
- `shared/ViewModels/PrepViewModel.cs:741` â€” call `FormatAsync` first, then the existing `EnsureSsdStructure`; consume `VolumeLabel` binding; show "Drive will be formatted now" confirmation before proceeding
- `prep-app/MainWindow.xaml:361` â€” button already reads "Format & Prepare Drive" (correct); no rename needed
- `tests/` â€” unit tests for the new `IDriveService.FormatAsync` (mock ProcessRunner + assert argument list)

**File system default:** NTFS (matches the warning in `DriveInspector.DriveWarning`). Allow exFAT as secondary option if user wants cross-platform compat with macOS side of the app â€” but flag strongly that NTFS is recommended.

**Security:**
- Format requires admin elevation. Must check admin status (`WindowsIdentity.GetCurrent().Owner` vs `WellKnownSidType.BuiltinAdministratorsSid`) up-front and fail-closed with a clear "relaunch as admin" message.
- Use `ProcessRunner.ArgumentList` â€” never string concat. Drive letter must be validated (single letter A-Z) before invocation.
- Re-confirm erase via the existing `ConfirmErase` dialog.

**âš  Staging note:** Test in a VM or with a spare USB stick first. Don't exercise against Stephen's live SSD until manually verified.

---

### F2 â€” Live model list fetch (HuggingFace / Ollama library)

**Status:** triaged
**Scope:** One-shot for v1
**Model:** Sonnet 4.6

**Existence:** Current catalog loads from `prep-app/Resources/starter-models.json` with an embedded fallback (`StarterModelCatalogLoader.Load`). Not hardcoded as TODO states â€” **correction: it's JSON-file-based already**, just not live-fetched.

**Affected files:**
- `prep-app/StarterModelCatalog.cs` â€” add `LoadFromNetworkAsync` path with fallback to existing file/embedded loaders
- New: `prep-app/Services/LiveModelCatalogService.cs` (or similar) â€” handles HuggingFace / Ollama API fetch
- `shared/ViewModels/PrepViewModel.cs` â€” add `RefreshCatalogCommand` + `LastCatalogUpdate` display
- `prep-app/MainWindow.xaml:93-118` â€” add "Refresh Model List" button + timestamp caption
- Consider: `shared/OllamaPackageTrustPolicy.cs` pattern â€” any new outbound HTTP endpoint should go through a trust policy to match project's security posture

**Source choice:** Ollama library is simpler (curated, already size-tagged). HuggingFace requires heavier filtering. Recommend Ollama-first, HuggingFace as optional advanced source.

**âš  Security note:** This introduces outbound HTTP from PrepApp. Per global CLAUDE.md, flag dependency/network additions to Stephen before installing any JSON parser beyond what's available, though `System.Text.Json` should cover it.

---

### F3 â€” PrepApp 2-tab restructure + UX simplification

**Status:** **done** — merged 2026-04-21 (PR #164, merge commit `953fb1b`). Stage 1 committed (`26d9a14` — VM command consolidation); Stages 2-3 shipped in the same PR. Follow-up merged-grid safety pass landed: configured/downloaded rows are no longer auto-selected, `Remove` now applies one action to all checked rows, and the dead standalone `VerifyCommand` path is gone. Full build + tests passed locally before merge, and PR CI cleared (`windows-build` green). Plan at `C:\Users\Kninetimmy\.claude\plans\im-in-plan-mode-elegant-lightning.md`.
**Scope:** Multi-stage (3 stages). The working branch was `feat/f3-prepapp-3-tab-restructure` as a legacy name; feature/PR naming used the **2-tab** wording by ship time.
**Model:** Sonnet 4.6 for all 3 stages (planning complete, mechanical implementation)

**Locked design** (full detail in plan file):
- **2 tabs, not 3:** Models + Drive. Drive absorbs Finalize/readiness (they operate on the selected drive â€” one mental model).
- **Monolithic `PrepViewModel` retained.** Sub-VM split evaluated and rejected â€” tight cross-cutting helpers (`AppendLog`, `SetModelOperationState`, `EnsureWritable`, `RefreshModelStatusesAsync`, `ConfirmSizingWarningsIfNeeded`) would become thin wrappers.
- **Merge Starter + Configured Models grids into one grid** with a `Status` column (`Not downloaded` / `Downloaded` / `On drive only`). Collapses the old two-step "add to config â†’ pull" into a single Download.
- **Auto-verify on download**, standalone Verify button deleted. On SHA mismatch: delete `.part`, log "Download failed verification â€” please retry".
- **Verbage overhaul for non-technical users:** Pull/Install â†’ Download, Finalize SSD â†’ Finish setup, Check SSD Readiness â†’ Run checks, Check for prereq updates â†’ Check for updates, Remove/Deleteâ€¦ â†’ Remove, Cancel Current Operation â†’ Cancel. Model Manager tab â†’ Models, Drive Setup â†’ Drive.
- **FTUE re-target:** `_ftueTargetTabIndex = { 1, 0, 0 }`, Step 3 body updated.
- **Runner `AddFilesButton` disabled tooltip** bundled in (polish item folded into this PR).

**Affected files (major rewrite):**
- `prep-app/MainWindow.xaml` â€” rewrite to 2 tabs; merge model grids; verbage swap
- `shared/ViewModels/PrepViewModel.cs` â€” rename `PullInstallCommand` â†’ `DownloadCommand`, semantics .Take(1) â†’ all checked; delete `PullSelectedCommand`, `VerifyCommand`, `AddStarterModelsCommand`; auto-verify in `PullModelsAsync`; add `ModelRow.Status`
- `prep-app/MainWindow.xaml.cs` â€” FTUE tab-index + step 3 text; delete orphan `OnBrowseStarterModelsClick`
- `runner/MainWindow.xaml` (line ~461) â€” `ToolTipService.ShowOnDisabled` + disabled tooltip on `AddFilesButton`
- `tests/` â€” retire tests on deleted commands

**Staging (single bundled PR, 3 commits):**
- **Stage 1 âœ…** â€” VM updates (rename + delete + auto-verify). Commit `26d9a14`.
- **Stage 2 âœ…** â€” XAML rewrite to 2 tabs with merged grid + verbage.
- **Stage 3 âœ…** â€” FTUE re-target + Runner tooltip + doc updates.

**Deferred follow-up (not blocking F3 completion):** manual FTUE / PrepApp smoke on a real SSD with `FtueCompleted=false`. Captured as backlog item **H3** below.

---

### H3 â€” F3 manual PrepApp / FTUE smoke follow-up

**Status:** deferred 2026-04-21. Pull forward only if F3/F4 review, CI, or later smoke finds a regression.
**Scope:** One-shot verification pass. No code unless a regression is found.
**Model:** Sonnet 4.6

**Intent:** Come back post-merge when we want the manual Windows PrepApp smoke that was deferred to keep F3 moving: FTUE tab targeting, merged-grid golden path, warning-strip visibility, and the disabled tooltip polish.

**Checklist:**
- Launch PrepApp with `FtueCompleted=false`; verify Step 1 targets `TargetDriveRow` on Drive, Steps 2-3 target `StarterModelsCard` / `DownloadButton` on Models.
- In the merged grid, check a recommended model, click Download, confirm status moves to `Downloaded`, then confirm Clear selection visibly unticks rows.
- Check multiple rows, run Remove, and confirm one chosen action applies to all checked rows.
- On the Drive tab, verify the warning strip stays prominent when a risky/fixed drive is selected.
- In Runner, verify disabled `Add files` shows "Create or select a library first."
- If any regression is found, spin it into a dedicated backlog item rather than silently reopening F3.

---

### F4 â€” Profile FTUE moves entirely to PrepApp (+ companion install target selector)

**Status:** Stage 1 done 2026-04-21 (PR #166, feature tip `34e5f5b`); Stages 2-4 pending
**Scope:** Multi-stage (4 stages)
**Model:** Opus 4.7 for planning
**Stephen confirmed (2026-04-17):** move FTUE entirely to PrepApp; Runner silently reads `ActiveProfile` from SSD config at launch.

**Execution handoff (historical, 2026-04-21):** Stage 1 plan and approved execution prompt were saved at `agent_docs/f4_stage1_execution_prompt.md`. Recommended execution model was `gpt-5.4`.

**Includes I1** â€” two-machine architecture diagram becomes the first step of F4's rebuilt FTUE flow (before profile selection). Flow is: *see two-machine architecture â†’ choose profile â†’ finish drive prep â†’ launch Runner*. Include as Step 1 of Stage 1.

**Existence:**
- Profile system exists in **Runner only**: `shared/Profile/UserProfile.cs` (enum: `GeneralAssistant`, `FlightSim`), `shared/Profile/ProfileDefaults.cs` (applies defaults), `runner/ProfileSelectionDialog.xaml(.cs)` (required on first run, `isRequired:true` blocks close).
- PrepApp has **zero profile awareness** â€” grepped prep-app/ for `UserProfile`, no matches.
- Companion install in PrepApp is a **single SSD-only checkbox** (`MainWindow.xaml:374-379`, `InstallVrCompanion`). No local-install option. No "both" option. Staging is at SSD `/companion` only.

**Stable decision update needed:** Current decision "First-run profile dialog is required â€” user must choose before the app proceeds" (enforced in Runner at `ProfileSelectionDialog.xaml.cs:18-24`). New behavior: **PrepApp is the sole FTUE owner; Runner reads `ActiveProfile` from config and never prompts on first run.** In-Runner profile toggle stays (for mid-session switching per existing pill-toggle UX). Append the superseding decision to `project_decisions.md` when this ships.

**Companion install target (Stephen's guidance):**
- Industry standard for Windows: **Program Files (x86/x64)** for shared installs (requires admin), **`%LocalAppData%`** for per-user installs (no admin). Start Menu shortcut is standard. Desktop shortcut optional.
- Stephen wants: **Program Files default** + **desktop shortcut optional** + **custom path input** so the user can drop the executable anywhere (portable-app use case).
- Research action: look at how comparable portable Windows apps (Obsidian, VS Code "System" vs "User" installers, Steam) handle the "install to Program Files vs portable anywhere" split. Use that pattern.

**Affected files:**
- Move: `runner/ProfileSelectionDialog.xaml(.cs)` â†’ `shared/UI/ProfileSelectionDialog.xaml(.cs)` (reusable by both apps) â€” or create new `prep-app/ProfileSelectionDialog.xaml` and delete the Runner version
- `prep-app/MainWindow.xaml(.cs)` â€” integrate profile card into FTUE overlay flow (`MainWindow.xaml:533-578`); gate advancement until selected
- `shared/ViewModels/PrepViewModel.cs` â€” track `ActiveProfile`, write to `PortableConfig` on Finalize
- `runner/MainWindow.xaml.cs` â€” remove first-run force-prompt path; silently read `ActiveProfile` from loaded config
- `runner/App.xaml.cs` â€” update boot flow to skip profile dialog
- `prep-app/MainWindow.xaml` â€” replace `InstallVrCompanion` single checkbox with target selector: **[â—‰ Program Files] [â—‹ Custom path...] [â—‹ SSD only] [â—‹ Both]** + optional "Add desktop shortcut" checkbox
- New: `prep-app/Services/LocalCompanionInstaller.cs` â€” handles Program Files install, shortcut creation, custom path copy
- `project_decisions.md` â€” superseding entry for first-run profile dialog behavior

**Staging:**
- **Stage 1** — done. Shipped in PR #166: two-machine architecture explainer as Step 1 of FTUE overlay; profile selection in PrepApp FTUE; Finalize writes `ActiveProfile` and applies `ProfileDefaults`; Runner no longer blocks first run on a required profile prompt.
- **Stage 2** â€” Post-setup launch flow ("SSD ready â€” launch Runner now?" â†’ Flight Sim: bindings import + doc ingest walkthrough; General: doc ingest only).
- **Stage 3** â€” Companion install target selector UI (Program Files / custom path / SSD / both).
- **Stage 4** â€” Local installer logic (copy to target, create Start Menu + optional desktop shortcut, register uninstaller?).

**âš  Architecture clarity:** TODO's two-machine description (ðŸ–¥ï¸ VR PC runs Companion, ðŸ’¾ SSD machine runs Runner) is accurate and matches current code â€” Companion connects to Runner over LAN (`CompanionRuntime.TryBuildBaseUri`).

---

### B3-Redux â€” Format regression: root cause is UAC two-click UX trap

**Status:** phase 1 complete 2026-04-18 (branch `claude/b3-redux-diagnostics`, commit `5910460`); phase 2 triaged, not started
**Scope:** Two-phase â€” (1) diagnostic pass âœ“, (2) UX fix + integration test
**Model:** Sonnet 4.6 for phase 2

**Phase 1 finding (2026-04-18):** B3 code is correct. The diagnostic build logged a real SSD format end-to-end: PowerShell command built correctly, exit 0, label changed "Portable AI" â†’ "Portable AItest2", no drive-letter swap. The v1.2.1 "regression" was the **UAC two-click UX trap** â€” user clicks Format in non-elevated instance â†’ accepts UAC relaunch â†’ new elevated window appears â†’ user doesn't realize they must click Format *again*. The `my library` folder persisting was leftover from a prior successful Finalize on the still-unwiped drive.

**Phase 2 approach (Stephen picked 2026-04-18):** belt-and-suspenders.

**Symptom (v1.2.1 field test, real SSD on Stephen's hardware):**
- "Format & Prepare Drive" button reports success and the drive appears prepped at end of flow.
- But: volume label does NOT change to the value in the `VolumeLabel` TextBox.
- And: a `my library` folder created in a prior session persists after the supposed format â€” meaning the drive was NOT wiped.
- Tests all pass (352/352) because `IDriveService.FormatAsync` is mocked with `Task.CompletedTask` in `PrepViewModelTests.cs:375â€“390`. The B3 test suite never exercised the real PowerShell.

**What the code looks like post-PR #131 (reviewed 2026-04-18):**
- Control flow is correct: confirm â†’ elevate â†’ format â†’ `EnsureSsdStructure` â†’ save config â†’ re-enumerate (`PrepViewModel.cs:745` onward).
- `VolumeLabel` binding is wired end-to-end: `MainWindow.xaml:359` â†’ `_volumeLabel` â†’ `DriveService.FormatAsync` â†’ `DriveFormatCommand` â†’ `FREEAI_FORMAT_LABEL` env var â†’ `Format-Volume -NewFileSystemLabel $env:FREEAI_FORMAT_LABEL`.
- `ProcessRunner.ArgumentList` usage is correct; the PowerShell invocation is argument-safe.

**Plausible root causes (to discriminate in phase 1):**
1. **UAC relaunch race** â€” non-elevated instance calls `TryRelaunchElevated()` in `WindowsElevationService.cs:21â€“50`, which `Process.Start`s an elevated child and calls `Application.Current?.Shutdown()`. If the user doesn't click Format in the elevated instance (or if the relaunch silently fails), the original un-elevated instance could fall through `EnsureSsdStructure` on an unwiped drive. Stephen reports "drive was created" â€” `EnsureSsdStructure` runs regardless of whether format ran.
2. **`Format-Volume` exit 0 without formatting** â€” certain drive conditions (open handles, non-removable bus type, enclosure quirks) can cause `Format-Volume -Confirm:$false` to return success without wiping. Need to verify `-Force` semantics and whether stdout contained warnings we're ignoring.
3. **Drive-letter mismatch under UAS/NVMe** â€” same class of bug F1 hit (`MSFT_PhysicalDisk.DeviceID` â‰  `MSFT_Partition.DiskNumber` on some enclosures, fixed in `3b20db8`). Possible the letter being formatted isn't the letter the user sees at end of flow.
4. **Something else entirely** â€” don't fix phantom causes; diagnose first.

**Phase 1 â€” diagnostic (DONE, commit `5910460`):**
- `DriveFormatCommand.Describe()` renders the built command for logging.
- `DriveService.FormatAsync` logs full command + env + working dir pre-run; captures all stdout/stderr (not just 10-line tail); logs exit code explicitly on every path.
- `PrepViewModel.FormatPrepareAsync` logs elevation status at moment of call, pre-format drive snapshot, post-format drive enumeration with label-actually-changed check.
- Sidecar file sink at `%TEMP%\freeai-format-diagnostic.log` (overwrites each run) â€” necessary because the UI LogListBox binds to `LogLines` and WPF ListBoxes don't support free-text selection.
- Phase 1 produced a working-format log artifact proving the code is correct. Root cause identified as UX, not code.

**Phase 2 â€” UX fix + integration test (TODO):**
1. **Auto-resume across UAC relaunch** â€” when `TryRelaunchElevated` fires, pass command-line args that encode the intent: `--autoresume-format=<root> --autoresume-label=<label>`. The new elevated instance parses these in `App.xaml.cs` / `MainWindow` startup, auto-selects the drive by root, pre-fills the label, and **prompts one confirm dialog** ("Format G:\\ with label 'Portable AI' now?") before proceeding. Never auto-formats silently â€” the confirm is the safety gate.
2. **Signpost banner in the elevated instance** â€” even when auto-resume runs, show a persistent banner at the top of the PrepApp window: "Running as administrator. Format operation ready to continue." Provides visual confirmation the relaunch completed and the user is in the right window. If auto-resume fails for any reason (e.g. drive no longer present, invalid args), the banner stays and instructs: "Click Format & Prepare Drive to continue."
3. **Real-ProcessRunner integration test** â€” new test in `tests/` that invokes `DriveService.FormatAsync` through the real `ProcessRunner` against a VHD (preferred; created via `New-VHD` + `Mount-DiskImage` in test setup, torn down after). Asserts post-format label matches requested label. Per `~/.claude/projects/.../memory/feedback_integration_tests_for_shellouts.md` â€” the mock-only suite is what let phase-1's regression hypothesis go unchecked.
4. **Remove (or gate) phase-1 diagnostic logging** â€” the verbose logging + sidecar file are noisy for production. Either remove entirely (relying on the new integration test to catch future regressions), or gate behind a `--diag` command-line flag. Decide during implementation based on how useful the sidecar turned out in the field.

**Affected files (phase 2):**
- `prep-app/App.xaml.cs` â€” parse `--autoresume-format` and `--autoresume-label` args, surface to `MainWindow` / `PrepViewModel`
- `prep-app/Services/WindowsElevationService.cs` â€” accept the args to forward in `TryRelaunchElevated`'s `ProcessStartInfo.ArgumentList` (don't use string concatenation â€” same security posture as `DriveFormatCommand`)
- `shared/ViewModels/PrepViewModel.cs` â€” on startup, if auto-resume args present: select drive by root, pre-fill `VolumeLabel`, fire a "continue format?" confirm, invoke `FormatPrepareAsync`
- `prep-app/MainWindow.xaml` â€” new banner element at top (visible when `IsElevated && HasAutoResumeIntent`)
- `prep-app/Services/DriveService.cs` + `shared/ViewModels/PrepViewModel.cs` â€” revert or gate the phase-1 verbose logging
- `shared/Services/DriveFormatCommand.cs` â€” the `Describe()` helper can stay (useful for future debugging, zero runtime cost)
- New: `tests/DriveServiceIntegrationTests.cs` â€” real-ProcessRunner test against a VHD target
- `tests/FreeAiSsd.Tests.csproj` â€” may need PowerShell script helpers for VHD setup/teardown

**âš  Safety notes for phase 2:**
- The confirm dialog in the elevated instance is **non-negotiable** â€” never auto-format a drive the user didn't explicitly re-confirm after relaunch, even if they clicked Format pre-relaunch. Intent can drift across the UAC gap.
- Command-line args must be validated: drive root parsed through `DriveFormatCommand.ParseDriveLetter`; label length-capped via `DriveFormatCommand.SanitizeLabel`. Anything invalid â†’ fall back to signpost banner ("Click Format & Prepare Drive to continue"), don't crash.
- The integration test should run on a **VHD**, not a USB stick. CI machines don't have USBs. `New-VHD` / `Mount-DiskImage` is Windows-admin-required â€” test may need to be skipped on non-elevated CI (fine; mock test still runs everywhere).
- **DO NOT merge phase 2 without first verifying the auto-resume path against Stephen's real SSD.** The integration test covers the format, not the UAC relaunch flow.

---

### X1 â€” Voice pipeline hang after TTS completion

**Status:** shipped 2026-04-18 (PR #136, `a9862e3`, cut as v1.2.4). **Field test FAILED 2026-04-18** â€” hang reproduced via example-prompt Send. Reopened as **X1-Redux** below.
**Scope:** One-shot
**Model:** Sonnet 4.6 (implemented on Opus 4.7 â€” Stephen had usage headroom)

**Symptom:** User clicks Send with voice input; the voice response plays back correctly; but then the "generatingâ€¦" indicator never clears, the UI freezes, and the app never recovers. Stephen reports it as a "hard crash."

**Root cause (reviewed 2026-04-18):**
`PttVoicePipelineService.cs:256â€“272` has a `finally` block that polls `_tts.IsSpeaking` every 100ms, capped at a 60-second timeout, before transitioning state back to `Idle`. `IsSpeaking` is a plain non-thread-safe bool in `PiperTextToSpeechService.cs:35` and `SystemTextToSpeechService.cs:28`, set in a `try/finally` around the background `Task.Run(() => RunPiperAndPlay(...))` at `PiperTextToSpeechService.cs:84`. The inner play loop uses a `ManualResetEventSlim.Wait(100)` (`PiperTextToSpeechService.cs:349`).

If the NAudio completion event delivers late, or the Piper child process lingers, `IsSpeaking` can stay true for the full 60s timeout before `SetState(PttState.Idle)` runs at line 271 â€” during which the UI sits on "generatingâ€¦" and feels frozen. Stephen's "never recovered" is consistent with him force-quitting the app before the 60s polling timeout expired.

**Fix:** Replace the polling loop with event-driven completion:
- `ITextToSpeech` gains a `Task SpeakAsync(...)` contract that completes **only after playback fully finishes** (not just after the process starts).
- `PiperTextToSpeechService` signals completion via a `TaskCompletionSource<bool>` flipped from the NAudio `PlaybackStopped` handler (and also on `ct.Cancel()` / process exit).
- `PttVoicePipelineService` `await`s that task in the finally block instead of polling `IsSpeaking`. No timeout needed on the happy path; keep a safety timeout (~30s) only as a defense against stuck processes.

**Affected files:**
- `runner/Services/PiperTextToSpeechService.cs` â€” expose completion via TCS; fire from `PlaybackStopped`
- `runner/Services/SystemTextToSpeechService.cs` â€” same pattern for the fallback TTS path
- `runner/Services/PttVoicePipelineService.cs:256â€“272` â€” replace polling loop with `await` on the new contract
- Interface in `runner/Services/ITextToSpeech.cs` (or wherever defined) â€” update contract
- `tests/` â€” add test that verifies `SpeakAsync` doesn't complete until playback finishes, and that `PttState` transitions to `Idle` without the 60s polling path

**âš  Watch for:** the fallback `SystemTextToSpeechService` uses `System.Speech.Synthesis` which has its own `SpeakCompleted` event â€” wire that similarly. Don't leave one of the two TTS implementations on the old polling contract.

---

### X2 â€” Runner window ScrollViewer patch (interim; tab restructure later)

**Status:** shipped 2026-04-18 (PR #134, `5247d2a`, released as v1.2.2)
**Scope:** One-shot (this item). A future Runner tab restructure is a separate bigger item â€” don't bundle.
**Model:** Sonnet 4.6

**Symptom:** `runner/MainWindow.xaml:6` declares `Height="1020" Width="1100"` with no ScrollViewer around the root grid. On monitors shorter than ~1020px usable (common with taskbar + window chrome), the DCS bindings import section (Grid.Row="4" at `runner/MainWindow.xaml:541â€“679`) falls off the bottom of the screen and is unreachable. Stephen can't test bindings import without it.

**Fix:** Wrap the root content in a `ScrollViewer VerticalScrollBarVisibility="Auto"`. Mind the existing interior scrollable regions (the response textbox on Row 6 has `Height="*"` â€” needs a max-height or a conversion to `Auto` so it doesn't fight the outer scrollviewer for extra space).

**Affected files:**
- `runner/MainWindow.xaml` â€” wrap `RootContent` grid in a `ScrollViewer`; adjust Row 6 sizing so the outer scroll is the one that activates when content exceeds window height
- Visual-regression check: ensure the FTUE overlay (if any in the Runner) still positions correctly over the scrolled content
- `tests/` â€” N/A (pure XAML)

**Follow-up:** Once F3's tabbed PrepApp ships, do a matching tab restructure for the Runner (Chat / Library / Integrations / Settings). Separate backlog item, not this one.

---

### X3 â€” Runner Start/Stop Ollama button state swap

**Status:** shipped 2026-04-18 (PR #135, `353e54b`, released as v1.2.3)
**Scope:** One-shot (trivial)
**Model:** Sonnet 4.6

**Symptom:** Both Start and Stop Ollama buttons are always visible; the Stop button uses `TactileMagentaButton` style unconditionally (`runner/MainWindow.xaml:236â€“250`), so it looks like the active CTA even when Ollama is already running. Confusing â€” users think the magenta "Stop" button means "Ollama isn't running, click here."

**Fix:** Swap button styles (or visibility) based on `_ollamaService.IsRunning`:
- When stopped: Start button = `TactileMagentaButton` (active CTA), Stop button = `GhostSecondaryButton` or hidden.
- When running: Start button = `GhostSecondaryButton` or hidden, Stop button = `TactileMagentaButton`.
- Keep the running-LED indicator unchanged.

Could be implemented via a `DataTrigger` binding on `IsRunning` or via visibility bindings with a converter. Pick whichever is consistent with how other buttons in the project swap styles (see existing Controls.xaml triggers).

**Affected files:**
- `runner/MainWindow.xaml:236â€“250` â€” button style/visibility bindings
- Possibly `runner/MainWindow.xaml.cs:374â€“418` â€” Start_Click / Stop_Click may need to trigger a PropertyChanged for `IsRunning` if the binding doesn't pick it up automatically
- `tests/` â€” N/A (visual)

**Ship solo** (revised 2026-04-18) â€” X2 already shipped as v1.2.2; X3 gets its own PR + v1.2.3.

---

### X4 â€” Bundle a real web chat UI (v1.3.x)

**Status:** triaged 2026-04-18
**Scope:** Multi-stage â€” design pass first, then implementation
**Model:** Opus 4.7 for the design pass
**Stephen confirmed (2026-04-18):** yes, bundle a real chat UI â€” "a general assistant user will absolutely want some sort of real tangible chat interface," plus post-session chat-log review is a genuine use case even for VR/voice users.

**Symptom (today):** Runner's "Open Chat UI" button (`runner/MainWindow.xaml.cs:557â€“561`) just does `Process.Start("http://{host}")` which lands on Ollama's "Ollama is running" root page â€” not a chat UI.

**Approach options to evaluate in the design pass:**
- **Bundle OpenWebUI** â€” well-known, full-featured, Ollama-native. Heavy: requires Docker or Python runtime; clashes with the portable-SSD posture.
- **Bundle a lightweight static SPA** (recommended starting point) â€” find a permissively-licensed open-source chat SPA (chatbot-ui-lite, similar), serve it from the Runner's existing Kestrel on 41555 under `/chat/` or similar, talking to the existing `/api/chat` / `/api/chat/stream` endpoints. No new runtime, no new port, no Docker. Ships as static files under `runner/wwwroot/`.
- **Build minimal in-house** â€” ~500 lines HTML/JS. Smallest dep surface, most work, full design control.

**Security + posture:**
- API-key auth already protects `/api/chat` (when `NetworkRequireApiKey` is set). The chat UI needs to prompt for or receive the key â€” don't hardcode.
- Static files should be served through the existing Kestrel, not a second web server.
- Chat-log persistence (the main user ask beyond "have a chat window") needs a decision: browser `localStorage`? Server-side store on the SSD? â€” punt to design pass.

**Affected files (sketch, to be confirmed in design):**
- `runner-core/Services/RunnerLocalApiService.cs` â€” add static file middleware + `/chat/` route
- New: `runner-core/wwwroot/chat/` â€” static SPA assets (must live in RunnerCore so both Windows and Mac hosts serve them; **not** under `runner/wwwroot/`)
- `runner/MainWindow.xaml.cs:557â€“561` â€” button opens `http://{host}:41555/chat/`
- `runner-core/FreeAiSsd.RunnerCore.csproj` â€” embed or copy static assets at publish time
- Mac Runner host wiring â€” ensure RunnerCore's wwwroot ships inside `mac/Runner.app` so the Mac Kestrel serves the same `/chat/` route
- `docs/` â€” add a "Web Chat UI" section to QUICKSTART

**Cross-platform note (post-MAC6):** `RunnerLocalApiService` now lives in `runner-core/` and (post-PR #181) wires `UseDefaultFiles` + `UseStaticFiles` against `runner-core/wwwroot/`. X4 only needs to drop SPA assets at `runner-core/wwwroot/chat/` â€” both Windows and Mac Kestrel serve them automatically.

**âš  Licensing:** any bundled SPA must have a compatible license (MIT, Apache-2, BSD). Flag to Stephen before adding it as a dep.

---

### X5 â€” GPU/CPU compute indicator (+ optional selector, deferred)

**Status:** triaged 2026-04-18
**Scope:** Two-phase â€” (1) read-only indicator, (2) optional selector (may never ship)
**Model:** Sonnet 4.6

**Symptom:** User (Radeon RX 9070 XT + gemma2:9b test model) has no visibility into whether Ollama is running inference on GPU or CPU. `PortableConfig.PreferredCompute` exists (`shared/PortableConfig.cs:69`, defaults `"cpu"`) but is never read by the Runner â€” it's set during prep and has no consumer.

**Phase 1 â€” read-only indicator (recommended scope for v1.x):**
- After each model load, Runner calls Ollama's `GET /api/ps` (not currently called anywhere in the codebase â€” verified via grep of `ChatService.cs` and `OllamaLifecycleService.cs`).
- Parse the response: each loaded model has `size` and `size_vram` fields. `size_vram == 0` â†’ CPU only; `size_vram >= size` â†’ full GPU; anything else â†’ hybrid.
- Display in the status area near the model-selector: "CPU" / "GPU" / "Hybrid (GPU: 80%)" or similar.
- No selection, no overriding â€” just surface the current reality.

**Phase 2 â€” selector (deferred, may skip):**
- Would require restarting Ollama with `OLLAMA_NUM_GPUS=0` for CPU-only mode. High friction; user has to sit through a model reload.
- Stephen hasn't asked for this â€” only the indicator. Don't build it unless a real use case emerges.

**Affected files (phase 1):**
- `runner/Services/OllamaLifecycleService.cs` or new `runner/Services/OllamaStatusService.cs` â€” `/api/ps` client
- `runner-core/Services/ChatService.cs` â€” trigger a status refresh on model load completion
- `runner/MainWindow.xaml` â€” indicator UI next to model selector
- `runner/MainWindow.xaml.cs` â€” wire the refresh to the UI
- `tests/` â€” mock HTTP test for `/api/ps` parsing

**âš  `PreferredCompute` status:** The existing unused field in `PortableConfig` should either get wired up (phase 2) or removed. Flag at phase 1 implementation time which way to go â€” Stephen likely won't want a dead field sitting in the config.

---

### X1-Redux â€” Voice/TTS pipeline hang still present after PR #136

**Status:** **Dormant as of 2026-04-19 v1.2.5 field test** â€” hang did not reproduce across 10+ varied prompts on `main` at `54b276a`; chat / TTS / library creation / PTT all healthy. Diag branch `diag/x1-redux-send-hang` stays on remote unmerged, ready to rebuild if the hang returns. No longer blocking v1.2.6. Prior status: phase 1 diagnostic branch pushed 2026-04-18, awaited repro log that never produced a repro.
**Scope:** Diagnose first, then fix (two-phase, B3-Redux style)
**Model:** Sonnet 4.6 for phase 1 (diagnostic); re-triage for phase 2 once cause is known

**Symptom (v1.2.4 field test, 2026-04-18):**
- Runner crashed on the first TTS attempt from the PTT path â€” Section 2 of the checklist could not be exercised at all (2aâ€“2g all skipped).
- Section 4a reproduced the hang via the **example-prompt Send button** (text path, not voice): AI replies correctly, but the Send button stays magenta, "generatingâ€¦" indicator never clears, app transitions to Not Responding, and force-close is required to recover.
- The entire point of PR #136 was to eliminate this "generatingâ€¦"-stuck state by awaiting `StreamingTtsSpeaker.Completion`. Field behaviour is unchanged or cosmetically identical to the pre-fix state.

**What's known to NOT be the cause (from PR #136 review):**
- `PttVoicePipelineService` does `await ttsSpeaker.Completion` with a try/catch (commit `a9862e3`); the old 60s polling loop is gone.
- Regression test `LiesAboutIsSpeakingTts` passes â€” if anything re-introduced the old polling branch, that test would fail. So the hang is **not** the original polling path returning.

**Plausible root causes (phase 1 should discriminate):**
1. **`Completion` never signals** â€” `StreamingTtsSpeaker` exposes `Completion` as a `Task`, but if the underlying TCS is never flipped (e.g. when Piper's stdout/stderr drains differently than expected, or when the example-prompt non-voice path uses a different TTS entry that doesn't wire `PlaybackStopped`), the `await` blocks forever. No timeout on the new path.
2. **Example-prompt Send goes through a different path** â€” the example-prompt button may invoke `ChatService.Send` on the UI thread or through a code path that doesn't use `PttVoicePipelineService` at all; the hang may be in the chat send/UI state machine, unrelated to TTS completion semantics. Verify which service handles the Send-button click before assuming this is TTS.
3. **Runner startup crash on first TTS attempt** â€” separate bug from the example-prompt hang, but compounds test coverage. Logs from `%LOCALAPPDATA%\FreeAiSsd\logs` during the crash should identify whether it's a Piper process spawn failure, a missing voice model, or an unhandled exception in `StreamingTtsSpeaker` construction.
4. **UI-thread deadlock** â€” if any path in the Send â†’ TTS chain captures the WPF SynchronizationContext and then awaits on a task that needs to resume on that same context, the classic WPF deadlock pattern is possible. The try/catch around `await Completion` wouldn't catch this â€” it's not an exception, it's a stall.

**Phase 1 â€” diagnostic branch live on `diag/x1-redux-send-hang`:**
- Runner log from Stephen's crashed-TTS attempt pulled (`G:\logs\runner-20260419.log`) â€” already surfaced a separate bug (see X8) but did not pinpoint the text-Send hang.
- New `runner/Services/X1ReduxDiag.cs`: static file-sink logger at `%TEMP%\freeai-x1redux-diagnostic.log` with timestamps, elapsed-ms, managed thread id.
- Instrumentation points in `MainWindow.xaml.cs`: `Send_Click` (enter / exit / all early-exit branches), `StopTts` (enter / post-Cancel / post-Dispose / exit), `SendStreamingAsync` (enter / pre+post `SendPromptStreamingAsync` / pre+post `Finish` / post-assignResponse / finally enter/exit), token callback (1st + every 20th, entry/exit around `Dispatcher.InvokeAsync`).
- Twin heartbeats: `[watchdog-bg]` via `Task.Run` every 500 ms (process-level), `[ui-hb]` via `DispatcherTimer(Background)` every 500 ms (UI-thread). Gap pattern discriminates process-hang vs UI-thread-starvation vs HTTP-stream-never-ends.
- Confirmed during investigation: example-prompt Send and voice Send **both** go through `Send_Click` â†’ `SendStreamingAsync`. No wrapper. Notably, `SendStreamingAsync` does NOT await `ttsSpeaker.Completion` â€” so the "indicator never clears" symptom cannot be the `Completion`-never-signals hypothesis; would fire the finally *too early*, not stall it.
- **Awaiting:** Stephen to run the built Release on the SSD, reproduce, and return the diagnostic log. Fix scope decided after reading the log.

**Phase 2 â€” fix (scope depends on phase 1 findings):**
- Likely options: add a safety timeout on `await Completion` (5â€“10s, not the old 60s) with explicit logged failure; fix the `TrySetResult` wiring; fix the UI-thread deadlock; or all three. Decide after phase 1.
- Any fix must keep the `LiesAboutIsSpeakingTts` regression test green â€” we don't want to reintroduce the polling path even as a fallback.

**Affected files (expected; revise per phase 1):**
- `runner/Services/PttVoicePipelineService.cs` â€” the `await Completion` call site and surrounding state transition.
- `runner/Services/StreamingTtsSpeaker.cs` â€” the TCS/Completion plumbing.
- `runner/MainWindow.xaml.cs` or the Send-button handler â€” the example-prompt send path.
- `runner-core/Services/ChatService.cs` â€” if the text-only send path routes through here.

**âš  Release implication:** v1.2.4 tag is **deferred** until this is resolved. Per the checklist's own release rule ("Defer tag if X1 shows any hang regression â€” that's the whole point of the release"), we do not tag.

---

### X6 â€” "Create Library" click hangs UI, crashes, library created on reopen

**Status:** triaged 2026-04-18
**Scope:** One-shot (diagnose + fix UI-thread blocking)
**Model:** Sonnet 4.6

**Symptom (v1.2.4 field test Section 4a, 2026-04-18):**
- User clicks "Create Library" in the Runner. UI hangs, app transitions to Not Responding, eventually crashes / is force-closed.
- On next launch, the library is present â€” i.e. the background work actually completed before or despite the crash, but the UI never recovered.

**Plausible root cause:**
- Library creation is running synchronously on the WPF UI thread (likely file enumeration + initial embedding index build). No `async`/`await` around the long-running step, or the work is kicked off via `Task.Run` but a subsequent `.Result` / `.Wait()` blocks the UI thread.
- Distinct from X1-Redux â€” the hang is triggered by library creation, not TTS. Keep separate.

**Diagnostic first pass:**
- Identify the Create Library command handler (search for button binding name or `CreateLibrary` in `runner/`).
- Check whether the handler is `async` all the way down. Any `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` on a task that captures the UI SynchronizationContext is the likely culprit.
- Confirm there's a progress indicator / busy state â€” users shouldn't be guessing whether the app has crashed.

**Affected files (expected):**
- `runner/MainWindow.xaml(.cs)` â€” Create Library button + handler.
- `runner/Services/LibraryService.cs` (or equivalent) â€” library creation logic.
- `runner/ViewModels/` â€” busy-state / progress bindings.

**âš  UX note:** Even after the blocking fix, Stephen's field-test notes mention the post-create flow is unclear â€” "ux is unclear of what steps to do after this so user can be unsure if thats all they have to do." Separate UX item, but worth flagging in this fix's PR as a follow-up rather than scope-creeping.

---

### X7 â€” DCS bindings scan finds aircraft but reports "no custom bindings"

**Status:** triaged 2026-04-18
**Scope:** One-shot (investigate parser / scanner path)
**Model:** Sonnet 4.6

**Symptom (v1.2.4 field test Section 5, 2026-04-18):**
- Runner's DCS bindings scan enumerates installed aircraft correctly.
- Reports "no custom bindings found" despite real `.diff.lua` files existing on disk.
- Concrete repro file: `C:\Users\Kninetimmy\Saved Games\DCS\Config\Input\FA-18C_hornet\joystick\ VKBsim Gladiator EVO R   {4C912ED0-C95D-11f0-8009-444553540000}.diff.lua`

**Prime suspect â€” path quirks DCS writes:**
- Note the **leading space** in `joystick\ VKBsim` (space between the backslash and the device name).
- Triple-space between `VKBsim Gladiator EVO R` and the `{GUID}`.
- If the scanner uses `Directory.EnumerateFiles` with a restrictive pattern, or normalises/validates filenames before parsing, these could silently drop real files. A glob like `*.diff.lua` should match, but any name-trimming or regex validation on the filename would.
- Also possible: the parser finds the file but fails to parse (leading-space handling, BOM, encoding) and is counted as "no bindings" rather than "parse error." Error vs empty needs to be distinguished.

**Diagnostic first pass:**
- Find the DCS bindings scanner in the codebase (likely `shared/Services/` or `runner/Services/Dcs*`).
- Run it against Stephen's actual `Saved Games\DCS\Config\Input\FA-18C_hornet\joystick\` path with logging to report: files matched by the glob, files attempted to parse, files that parsed successfully, and per-file reasons for parse failure.
- Known-good fixture files live in the test suite â€” diff behaviour against those to isolate whether the issue is discovery or parsing.

**Affected files (expected):**
- The DCS bindings scanner / parser (exact path TBD at investigation time).
- `tests/` â€” the existing `DcsBindingParserTests` fixtures are the baseline; add a fixture with a leading-space and GUID-tail filename to cover this case.

**âš  Watch:** DO NOT change the parser to be lenient in a way that matches non-DCS `.diff.lua` files. The fix should be about correctly discovering and reading the files DCS actually writes, not relaxing input validation.

---

### X8 â€” Whisper `_transcriptionGate` disposed during model re-init

**Status:** in review â€” PR #138 (`fix/x8-whisper-semaphore-disposal`, `591a39b`) 2026-04-18. 379/379 tests green, CI green. Merge-scope decision pending (see below).
**Scope:** One-shot minimal fix shipped; review-driven hardening decision pending
**Model:** Sonnet 4.6 (mechanical fix done); Opus for merge-scope decision if we harden

**Symptom (from Stephen's Runner log, v1.2.4 field test):**
- On PTT / voice path, after a Whisper state reset, the next call to `TranscribeStreamAsync` threw `ObjectDisposedException` from `_transcriptionGate.WaitAsync`. Surfaced during X1-Redux log triage â€” separate bug from the text-Send hang.

**Root cause:**
- `WhisperSpeechToTextService.InitializeAsync` called the public `Dispose()` as a state-reset shortcut. Public `Dispose()` disposes `_transcriptionGate` along with `_factory`/`_processor`. Re-init then left the service "initialized" but with a disposed semaphore, so every subsequent transcription call blew up on first `WaitAsync`.

**Fix (shipped on PR #138):**
- New private `ReleaseModel()` disposes only `_factory` + `_processor` (the re-initable resources).
- Public `Dispose()` calls `ReleaseModel()` then disposes `_transcriptionGate` (full teardown only on service destruction).
- `InitializeAsync` now calls `ReleaseModel()` for its state reset; catch-path on init failure also uses `ReleaseModel()`.
- 4 reflection-based regression tests in `tests/WhisperSpeechToTextServiceTests.cs` pin the contract.

**Review findings (all pending merge-scope decision):**
1. **Gemini (HIGH):** `InitializeAsync` doesn't acquire `_transcriptionGate` before calling `ReleaseModel()`. A concurrent `TranscribeStreamAsync` mid-`await foreach` over `_processor.ProcessAsync` can still hit `ObjectDisposedException` on the processor itself.
2. **Gemini (LOW):** `InitializeAsync_FailsOnMissingModel` test leaks its tempdir on assertion failure â€” wrap in try/finally.
3. **Codex adversarial (HIGH):** Window-close `MainWindow.Dispose()` (line ~152) calls the service's public `Dispose()` while an in-flight `TranscribeStreamAsync` may still hold the gate. Shutdown needs quiescence (wait for active transcribes to drain, or cancel + await) before disposing.
4. **Codex adversarial (HIGH):** Singleton service has no init lock. Voice UI, HOTAS PTT, and the LAN API can each trigger `InitializeAsync` concurrently â€” two concurrent init paths can dispose a processor the other just created.

**Decision to make before merge:**
- **Minimal:** merge PR #138 as-is, open X8a for findings #1/#3/#4 and a trivial PR for #2. Tag v1.2.4 sooner.
- **Harden:** fold all four findings into PR #138. Slower tag, but one cohesive fix with fewer follow-ups and no partially-safe interim state.

**Affected files (if hardening path chosen):**
- `runner/Services/WhisperSpeechToTextService.cs` â€” gate acquisition in InitializeAsync, service-level init lock, shutdown quiescence hook.
- `runner/MainWindow.xaml.cs` â€” shutdown sequencing (await pipeline drain before Dispose).
- `tests/WhisperSpeechToTextServiceTests.cs` â€” add concurrency tests + try/finally tempdir cleanup.

---

### F5 â€” In-app TTS settings UI (backend selector + voice model picker)

**Status:** triaged 2026-04-18
**Scope:** One-shot feature (new Settings surface in Runner)
**Model:** Sonnet 4.6 (design small enough to skip Opus plan unless it grows)

**Why now:** Blocker for field-testing Piper / SAPI / disabled paths (Checklist sections 2c / 2d / 2e â€” all skipped in v1.2.4 field test because there's no way for the user to switch backends without editing config files). Also user-facing ask: *"i realize i have no idea how to activate piper. i found no settings/options to preload or enable it in the runner or prep app. note to add ability to configure that in software with the various models and descriptions of quality."*

**Intent:**
- A Settings page inside the Runner (not a config file the user edits in Notepad â€” explicitly rejected by Stephen).
- **TTS backend selector:** Piper / System SAPI / Disabled.
- **Voice model picker** when Piper is selected â€” list installed voices from `windows/tools/piper/voices/` (or wherever the prereq bundle stages them) with a short quality/size/sample-rate description next to each. Ideally a Play Sample button.
- **SAPI voice picker** when System SAPI is selected â€” enumerate installed Windows voices via `System.Speech.Synthesis.SpeechSynthesizer.GetInstalledVoices()`.
- Persist selection to `PortableConfig` so it survives restart and syncs to the SSD.

**Existence check (before implementation):**
- `PortableConfig.cs` probably already has some TTS-related fields (there's a voice-model one for Piper somewhere in the PrepApp flow) â€” audit and reuse rather than adding parallel settings.
- Decide whether this Settings surface lives in the Runner only, or also exposed in the PrepApp's F3 2-tab restructure (likely Runner-only, since Piper voices are downloaded via PrepApp's model-manager flow but selected at runtime).

**Affected files (sketch, confirm at implementation time):**
- New: `runner/SettingsWindow.xaml(.cs)` or a Settings tab inside `MainWindow` â€” depends on whether Runner tab restructure ships first.
- `runner/Services/PiperTextToSpeechService.cs` â€” voice-model discovery API.
- `runner/Services/SystemTextToSpeechService.cs` â€” SAPI voice enumeration.
- `shared/PortableConfig.cs` â€” TTS fields (or confirm existing fields and extend).
- `shared/ViewModels/` â€” new SettingsViewModel.

**âš  Scope discipline:** This is the *TTS* settings UI only. Do not scope-creep into a general-purpose Settings page covering every Runner option. F3 is the surface for broader settings restructure; keep F5 targeted so it unblocks TTS field-testing without waiting on F3.

**âš  Interaction with X1-Redux:** If X1-Redux phase 1 shows that switching TTS backends is part of reproducing the hang, F5 may get pulled forward into the X1-Redux fix PR. Otherwise F5 slots after v1.2.4 tag.

---

### H1 â€” Repo spring cleaning

**Status:** shipped 2026-04-18 (PR #137, squash-merged to `main` at `a894862`)
**Scope:** One-shot (housekeeping). Slot **between** the next bug fix and feature add â€” not in the middle of an in-flight feature branch.
**Model:** Sonnet 4.6 (no design work; mechanical)

**Intent:** Strip stale artefacts that predate the current UX and refresh the two public-facing docs (`README.md`, `docs/QUICKSTART.txt`) so a fresh downloader sees instructions that match what the app actually does now.

**Concrete targets identified 2026-04-18 â€” confirm each still looks stale before deleting:**

*Deletions â€” old review dumps and pre-screenshot assets:*
- `CODE_REVIEW.md` (root) â€” old codex review, predates current code.
- `Claude_code_review.md` (root) â€” older review dump.
- `docs/CODEX_PROMPTS_UX_FIXES.md` â€” old prompt notes.
- `docs/images/prep-app-mockup.svg` â€” pre-screenshot mockup; real screenshots now live next to it (`prep-app-drive-setup.png`, `prep-app-model-manager.png`). Confirm no remaining README/doc refs before removing.
- Anything else in the repo root or `docs/` that looks like a one-off review or migration note older than ~3 months.

*Refreshes â€” bring in line with current UX:*
- `README.md` â€” the v1.2.x UX (X2 ScrollViewer, X3 Start/Stop button swap, B3-Redux auto-resume, X1 fix) isn't reflected in the instructions. Don't just append; audit the full "how to use" flow top to bottom. Keep real screenshots, replace any that show old layout (check against live app).
- `docs/QUICKSTART.txt` â€” same exercise. Ensure step ordering matches what PrepApp actually asks for in 2026-04-18 form.
- Fold in the outstanding **F1 README update** (USB SSD detection fix, backlog line 68) â€” don't open a separate doc PR for it.

**Deliberately out of scope for H1:**
- Code refactors, dead-code deletion in source files â€” separate task.
- `agent_docs/` framework files â€” those live on a different restructure track (memory: "Docs restructure in flight").
- `CLAUDE.md` â€” also on the docs restructure track.

**âš  Watch for:**
- Before deleting any `.md`, grep for its filename across the repo â€” reviews sometimes get linked from README or CI docs.
- Before deleting `prep-app-mockup.svg`, grep for `mockup.svg` in README.md and docs/ to make sure nothing still references it.
- If any deletion surfaces that a file is actually load-bearing (linked from a workflow, referenced in code comments), **keep it and flag for Stephen** rather than silently leaving the reference dangling.

**Exit criterion:** One PR, one commit per logical grouping (deletions in one commit, README refresh in another, QUICKSTART refresh in a third). After merge, `README.md` and `docs/QUICKSTART.txt` both reflect the current v1.2.x UX exactly.

---

### X9 â€” Encrypted config persistence lifecycle

**Status:** **DONE. All 4 stages shipped. Final PR #147 (`b75e42a`) merged 2026-04-19.**
**Scope:** Multi-concern; single cohesive fix across shared lib + Runner + Prep.
**Model:** Opus 4.7 for planning, Sonnet 4.6 for implementation stages

**Symptom:** On an encrypted SSD, changes made post-unlock silently revert after restart, and a plaintext `portable-config.json` containing secrets (API key, etc.) lives on disk alongside the encrypted blob.

**Root cause (verified against live code 2026-04-18):**
- `PortableConfig.SaveAsync` (`shared/PortableConfig.cs:291-320`) always writes plaintext JSON. The fail-closed guard at 299-306 only blocks when Network Mode + Require API Key is on AND the drive is NOT effectively encrypted. On an encrypted drive the guard *passes*, and plaintext is written anyway.
- `MainWindow.LoadConfig` (`runner/MainWindow.xaml.cs:215-242`) unlocks from the encrypted blob on every startup when `IsEffectivelyEncryptedForWriteGuard` returns true â€” the plaintext file written by the previous session's saves is ignored.
- `SsdEncryption.EnableConfigEncryptionAsync` (`shared/SsdEncryption.cs:130-195`) is one-way: plaintext â†’ encrypted + state files, then plaintext deleted. No symmetric "save encrypted from in-memory config" path exists, so nothing in Runner or Prep can update the encrypted payload after initial setup.
- Finalize bootstrap (`shared/ViewModels/PrepViewModel.cs:1222-1226`) sets `config.IsEncrypted = true` then calls `SaveConfigAsync` *before* `EnableConfigEncryptionAsync` creates any encrypted artifact. If Network Mode + Require API Key is on at finalize time, the guard throws because encrypted artifacts don't yet exist â€” finalize fails-closed in that combo.
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

*Key caching.* Cache the 32-byte derived key + salt + iterations + scheme. **Never** cache the password. Key lives in a private `byte[]`, zeroed via `CryptographicOperations.ZeroMemory` on `LockSession()`. Reuse the existing salt across saves; rotate a fresh random GCM nonce per save (safe because (key, nonce) pairs stay unique). Process-kill leaves the key in memory pages until OS reclaim â€” inherent; do not try to patch around it.

*Symmetric encrypted save.* New `SsdEncryption.SaveEncryptedConfigAsync(ssdRoot, config, material, ct)`. Serializes `config` in memory, encrypts with cached key + fresh nonce, writes **both** the encrypted blob and the state file atomically: both to `.tmp`, rename encrypted first, rename state second; if the second rename fails, roll back the first. No plaintext ever touches disk.

*In-memory finalize overload.* New `SsdEncryption.EnableConfigEncryptionAsync(ssdRoot, config, password, ct)` that accepts the `PortableConfig` object directly. `PrepViewModel.FinalizeAsync` uses this; no plaintext-then-encrypt dance. Eliminates the Network-Mode-blocks-finalize bug as a side effect.

*Unlock API.* `SsdEncryption.TryUnlockPortableConfig` grows an additional out-param (or sibling `TryUnlockPortableConfigWithMaterial`) returning the `UnlockMaterial` alongside the decrypted `PortableConfig`. Callers pass that straight to `ConfigStore.UnlockSession`.

*Serialized save queue.* `ConfigStore` owns a `SemaphoreSlim(1,1)`; all saves drain sequentially. No more `.tmp` races.

*Shutdown drain.* `MainWindow.OnClosing` calls `await ConfigStore.FlushAsync(TimeSpan.FromSeconds(5))` **before** `LockSession()`. Bounded so a stuck save can't hang app close â€” log and proceed on timeout. Prevents queued-save-after-key-zero â†’ silent edit loss.

*Migration (upgrade from broken v1.2.x).* Every post-unlock save before this fix silently landed in plaintext while the encrypted blob went stale, so the plaintext on disk **may hold the user's most recent edits**. On first unlock after this ships, detect stale plaintext beside the encrypted blob and compare `File.GetLastWriteTimeUtc`:
- Plaintext newer â†’ modal dialog: *"Found unsaved edits from before the security fix. Load them, re-encrypt, then delete the plaintext?"* Default Yes. Loads plaintext, merges over the just-unlocked config, saves via `ConfigStore` (re-encrypt), deletes plaintext.
- Encrypted newer â†’ modal dialog: *"Found a plaintext config from before the security fix. Delete it?"* Default Yes.
- Never silently delete. Stephen sees the prompt every time.

*Fail-closed guard.* `ConfigStore` enforces: Network Mode + Require API Key + would-write-plaintext â†’ throw `InvalidOperationException`. Semantics unchanged from today; only the chokepoint moves.

**Affected files:**
- `shared/PortableConfig.cs` â€” deprecate direct `SaveAsync` callers; keep load + serialize helpers.
- `shared/SsdEncryption.cs` â€” add `SaveEncryptedConfigAsync`, in-memory encrypt overload, `TryUnlockPortableConfigWithMaterial`.
- **New:** `shared/Services/IConfigStore.cs`, `shared/Services/ConfigStore.cs`, `shared/Services/UnlockMaterial.cs`.
- `runner/MainWindow.xaml.cs` â€” route all saves through `IConfigStore`; capture `UnlockMaterial` on unlock; `OnClosing` calls `FlushAsync` then `LockSession`.
- `runner-core/Services/DocumentOperationsService.cs` â€” use `IConfigStore`.
- `prep-app/Services/ModelService.cs` â€” use `IConfigStore`.
- `prep-app/Services/ReadinessService.cs:92,98` â€” use `IConfigStore`.
- `shared/ViewModels/PrepViewModel.cs:1212-1226` â€” encrypt from memory; no plaintext intermediate.
- `tests/PortableConfigSaveGuardTests.cs:73-96` â€” rewrite. Preserve the "Network Mode + unencrypted + API key â†’ refuse" axis; replace the plaintext-after-encryption axis with encrypted-round-trip semantics.
- **New tests:** real-crypto fixtures (no mocks). Post-unlock edit round-trip on encrypted drive; concurrent save serialization; finalize + Network Mode + API key; migration with plaintext-newer-than-encrypted; migration with encrypted-newer-than-plaintext; flush-on-close drains queued save.

**Staging:**
- **Stage 1 â€” plan (Opus). DONE 2026-04-19.**
- **Stage 2 â€” shared lib. DONE 2026-04-19 (PR #144, `49ce6a0`).** `IConfigStore` + `ConfigStore` + `UnlockMaterial`, symmetric encrypted save with two-file atomic commit, in-memory encrypt overload, `TryUnlockPortableConfigWithMaterial`. 10 real-crypto tests. No wiring yet.
- **Stage 3 â€” Runner wiring. DONE 2026-04-19.** `IConfigStore` wired into `MainWindow` (unlock captures `UnlockMaterial`, all save sites, `OnClosing` flush+lock) and `DocumentOperationsService`. `DocumentLibraryWorkflowTests` updated. 1 new integration test (`RunnerWiring_UnlockSaveLockReUnlock_RoundTrips`). Suite 393/393. Runtime smoke test pending field validation.
- **Stage 4 â€” Prep finalize + migration + guard rewrite. DONE 2026-04-19 (PR #147, `b75e42a`).** In-memory finalize; `TryMigratePlaintextAsync` (Branch A = absorb + confirmation modal, Branch B = silent delete + log); 7 new real-crypto guard tests. UX note: Branch B is silent per design â€” only Branch A shows the "Settings Recovery" dialog. 400/400 pass.

**âš  Security / data safety:**
- No plaintext on disk at any transitional step. In-memory only until encrypted payload is written.
- Key bytes: in-process only; zeroed via `CryptographicOperations.ZeroMemory` on `LockSession`; never logged, never serialized. Process-kill leaves key in memory pages until OS reclaim â€” inherent.
- Two-file atomic commit for encrypted blob + state file. Roll back first rename if second fails.
- Shutdown flush bounded at 5s â€” prefer lost edit on stuck save over hung app close; log the timeout.
- Migration prompt is **always** modal and **always** user-confirmed. Never silently delete or silently overwrite. Plaintext-newer branch is the one that matters for Stephen's field drive.

---

### X10 â€” Document replacement + rebuild consistency

**Status:** Stages 1â€“4 DONE 2026-04-19 (PR #150 `b6536b3`, PR #151 `a430ab0`, PR #152 `af77abc`, Stage 4 review direct-in-conversation); v1.2.7 tagged on `af77abc`, win-x64 release dispatched (run 24646703518). CLOSED.
**Scope:** One cohesive fix; transactional replace + rebuild-from-stored.
**Model:** Sonnet 4.6

**Symptom:**
- Re-ingesting a changed document leaves stale chunks in the vector DB and stale stored files in the library folder.
- "Rebuild index" silently drops any document whose original source file has been moved or deleted, even though the SSD still has the stored library copy.
- **Field log 2026-04-19** (`G:\logs\runner-20260419.log:381`): *"Rebuild failed: The process cannot access the file 'vectors.db' because it is being used by another process."* Observed mid-session with Ollama running â€” suggests rebuild path doesn't coordinate with concurrent readers or a prior failed attempt leaked the file handle. Reproduce and fix as part of X10.

**Root cause (verified 2026-04-18):**
- Stored filenames are `{sha[..12]}_{fileName}` (`shared/Documents/DocumentIngestor.cs:70`). When content changes, the SHA prefix changes, producing a new `StoredRelativePath`.
- `VectorIndex.UpsertFileChunks` (`shared/Documents/VectorIndex.cs:303-338`) deletes rows keyed on the *new* `storedRelativePath`. Old rows tied to the previous path survive. Old stored file on disk also survives â€” nothing removes it on replacement.
- `DocumentIngestor.RebuildIndexAsync` (`shared/Documents/DocumentIngestor.cs:285-296`) enumerates `manifest.Files.Select(f => f.SourceOriginalPath).Where(File.Exists)` â€” i.e. originals, not the stored library copies. Moving/deleting originals breaks rebuild even though the SSD is self-contained.
- Per-file ingestion failure ordering: vectors are committed (`DocumentIngestor.cs:189`) before the manifest save (`:208`). The catch block at `:210-219` deletes the staged file but not the just-written vectors, so late I/O failure can leave vectors orphaned from manifest state.

**Fix:**
- On replacement in `IngestFilesAsync`: capture `current.StoredRelativePath` *before* overwriting it; after the new vectors + manifest commit successfully, delete the old vectors (via `VectorIndex.RemoveFile(libraryId, oldStoredRelativePath)`) and the old stored file on disk.
- `RebuildIndexAsync` rebuilds from `StoredRelativePath` under the library folder, not `SourceOriginalPath`. Re-parse and re-embed from the stored copy. The original path becomes informational metadata only.
- Tighten per-file transactionality: either (a) save manifest before committing vectors so a failed manifest save leaves no orphaned vectors, or (b) on exception, remove the just-written vectors as part of rollback. Pick whichever matches the existing transactional story best.
- Watch-folder sweep (`SweepFoldersAsync`, `:256-283`) uses `EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true }` or catches per-subtree so one protected folder doesn't abort the whole sweep.

**Affected files:**
- `shared/Documents/DocumentIngestor.cs` â€” replacement cleanup, rebuild from stored, sweep resilience, per-file rollback.
- `shared/Documents/VectorIndex.cs` â€” confirm `RemoveFile` is sufficient for old-path cleanup, or add a helper.
- `tests/` â€” new tests: changed-file replacement removes old vectors + old stored file; rebuild works with originals missing; watch-folder sweep survives inaccessible subtree; late-failure rollback leaves no orphaned vectors.

**âš  Watch for:**
- Don't change `StoredRelativePath` naming scheme â€” it's baseline for tests and existing SSDs. Fix the cleanup, not the key.
- `DocumentFileEntry` has no "previous stored path" field. Capture locally inside the replace loop; don't mutate the entry until after cleanup succeeds.
- Rebuild from stored means the embedding model must be compatible with what generated the existing stored files. If a user changes models, rebuild may still need to re-embed from scratch â€” confirm behaviour matches user expectations.

**Expansion 2026-04-19 (RAG audit fallout):**
- **SQLite PRAGMAs on `VectorIndex` connection:** `journal_mode=WAL`, `busy_timeout=5000`, `synchronous=NORMAL`. Same code path as rebuild work; lands in the same PR. `VectorIndex.cs:44` currently uses a bare `new SqliteConnection($"Data Source={_dbPath}")` â€” no journal mode, no busy timeout. Eliminates the concurrent-reader/rebuild file-lock class of failures (see field log) independent of the rebuild-from-stored fix itself.
- **Stable document GUID spun out as X10-Redux** (not this PR) â€” see decision 2026-04-19. X10 ships capture-old-path + WAL + rebuild-from-stored as the principled fix for the current symptoms. Identity-layer upgrade revisited only if path-capture proves insufficient in field use.

**2026-04-19 plan lock** â€” full staged plan at
`C:\Users\Kninetimmy\.claude\plans\x10-doc-replace-rebuild.md`. Locked
decisions: path-primary + sha256-assisted rename detection (X10-Redux
GUID deferred); WAL + busy_timeout on every SQLite open;
rebuild-from-stored gated on X21 provenance (dead code until X21 lands).
3 implementation PRs + review â†’ v1.2.7.

**Stage 3.5 explicitly deferred to X21 (2026-04-19 implementation decision)** â€”
provenance gate (`vectors.db.old` snapshot + skip-re-embed when model matches
stored `embedding_model_id`+`version`) requires X21's schema columns to be
meaningful. Skipped the snapshot for simplicity; X21 adds the schema, then the
gate activates. Stub test in `DocumentRebuildTests.cs` (`Skip = "Depends on X21"`)
keeps the contract visible.

---

### X11 â€” Companion keyboard PTT + first-run validation

**Status:** shipped 2026-04-20. PR #159 merged `329bcf2e`.
**Scope:** One-shot; three related companion defects in a single PR.
**Model:** Sonnet 4.6

**Symptoms:**
1. Keyboard fallback PTT records only ~100 ms regardless of actual key-hold duration. Hotkey registration failures are silent.
2. Canceling the first-run Settings dialog still starts the app in an invalid state â€” HOTAS polling kicks off against default button 0 on null device.
3. API key is shown in plain text in the Settings window, editing is disabled once a key exists, and blank textbox on save silently preserves the old key.

**Root cause (verified 2026-04-18):**
- `companion/KeyboardPttHotkey.cs:25-47`: uses `RegisterHotKey` (which delivers `WM_HOTKEY` on key-down only) and fakes release with `Task.Delay(100)`. `RegisterHotKey` return value is discarded at line 27. This is not PTT â€” it's a 100 ms one-shot trigger that lies about being PTT.
- `companion/CompanionRuntime.cs:64-67`: if `_config.IsComplete()` returns false, `OpenSettings()` is called but control falls through to `InitializeBindings()` and the health loop regardless of whether the user cancelled the dialog.
- `companion/CompanionRuntime.cs:460-470` (`ParseHotasBinding`): empty/malformed input falls through with `deviceName=null`, `buttonIndex=0`. `_hotas.Start(null, 0)` then polls for nothing in particular.
- `companion/SettingsWindow.xaml` uses a `TextBox` for the API key (plaintext on-screen); save logic preserves old key when textbox is blank, making rotation/reset awkward.
- `shared/Models/CompanionConfig.cs:43-47`: `IsComplete()` hard-requires an API key even when the Runner may not require one (Network Mode off, or auth disabled).

**Fix:**
- Replace `RegisterHotKey`-based approach with a low-level keyboard hook (`SetWindowsHookEx` with `WH_KEYBOARD_LL`) that delivers real `WM_KEYDOWN` / `WM_KEYUP` events. Fire `_onPress` on down, `_onRelease` on up. Log registration failure; surface to UI.
- In `CompanionRuntime.Start`: if config is incomplete after `OpenSettings()` returns (user cancelled or saved incomplete data), show a clear error and either block startup (tray icon with "Configure to continue" state) or exit cleanly. Do not start `InitializeBindings` / health loop against invalid config.
- Validate HOTAS binding before starting the poll â€” refuse to start with null device or button-0-default fallback. Surface the invalid-binding state to the user.
- Replace the API key `TextBox` with a `PasswordBox`. Add an explicit "Replace key" / "Clear key" flow; blank `PasswordBox` preserves the existing key unless the user explicitly clears it or enters a replacement.
- Make `CompanionConfig.IsComplete()` conditional: if the Runner's server has Network Mode off or auth disabled, an API key is not required. Detect via health probe at first-run setup, or let the user explicitly mark "server does not require a key."

**Affected files:**
- `companion/KeyboardPttHotkey.cs` â€” rewrite around low-level hook.
- `companion/CompanionRuntime.cs:55-72, 83-99, 460-470` â€” startup gating, binding validation.
- `companion/SettingsWindow.xaml` + `.xaml.cs` â€” `PasswordBox` + explicit reset flow.
- `shared/Models/CompanionConfig.cs:43-47` â€” conditional completeness check.
- `tests/CompanionConfigTests.cs` â€” new coverage for conditional completeness; first-run cancel behaviour; binding validation.

**âš  Watch for:**
- Low-level keyboard hook runs on the UI thread and must be non-blocking. Dispatch to a background worker for any non-trivial work in `_onPress`/`_onRelease` handlers.
- `SetWindowsHookEx` with `WH_KEYBOARD_LL` captures *all* key events system-wide. Be surgical: only intercept the configured PTT key; pass everything else through unchanged.
- API key masking in UI: do NOT log the key anywhere â€” not in `CompanionLog`, not in error messages, not in health probe debug output.

---

### X12 â€” DownloadManager verify-before-move

**Status:** PR #161 open 2026-04-20, CI green â€” pending merge. **Medium (security-adjacent).**
**Scope:** One-shot.
**Model:** Sonnet 4.6

**Symptom:** A corrupted or tampered download lands at its final destination path *before* SHA-256 verification runs. Mismatch throws, but the bad file is already in place.

**Root cause (verified 2026-04-18):**
- `shared/DownloadManager.cs:95-101`: `File.Move(tempPath, request.DestinationPath, overwrite: true)` runs before `VerifySha256(request.DestinationPath, ...)`. If verification throws, the bad file sits at the destination.

**Fix:**
- Reorder: close stream â†’ `VerifySha256(tempPath, expected)` â†’ `File.Move` only on success.
- On mismatch: delete temp file and throw. If the destination already exists from a prior successful download, do NOT overwrite it based on a verification that hasn't run yet.
- If resume semantics (the existing `FileMode.Append` path at `:80`) are affected by this, confirm resumed partial downloads still verify correctly before being promoted.

**Affected files:**
- `shared/DownloadManager.cs:75-102`.
- `tests/` â€” new test: mismatched SHA leaves no file at destination and temp is cleaned up.

**âš  Watch for:**
- Callers (PrereqFetch tool, `OllamaPackageService`) that expect the file to exist at `DestinationPath` after `DownloadAsync` returns â€” verification failure becomes the exception path, destination file is absent. Confirm no caller treats "destination exists" as implicit success without catching the throw.

---

### X13 â€” Chat/STT surface real failures

**Status:** **done** — merged 2026-04-20 (PR #162, `40f41fd`). 449 tests pass (+12 new).
**Scope:** One-shot; two services, one PR.
**Model:** Sonnet 4.6

**Symptom:** Backend / transport failures in `ChatService` and `WhisperSpeechToTextService` are flattened into empty-string success. Callers (UI, LAN API) cannot distinguish "model returned no answer" from "system failed" â€” users see silent empty responses instead of actionable errors.

**Root cause (verified 2026-04-18):**
- `runner-core/Services/ChatService.cs` â€” catch returns `new ChatResponse(string.Empty, null, false)` on any exception. Streaming path is slightly better (injects `[Error: â€¦]` into the token stream when partial content exists) but still returns success-shaped object.
- `runner/Services/WhisperSpeechToTextService.cs:127-131` â€” catch returns `string.Empty` on exception.

**Fix:**
- Add `ChatResult` / `TranscriptionResult` record types that are either success (with payload) or failure (with error message + optional inner exception). Callers switch on the union.
- `RunnerLocalApiService` translates failure results to proper HTTP error responses (500 with error body, or 502 if the failure is clearly a downstream issue like Ollama unreachable).
- UI (Runner MainWindow) translates failure results to a visible error log line + appropriate state transition (don't leave "generatingâ€¦" stuck).
- Keep the streaming `[Error: â€¦]` in-band injection as a UX nicety *in addition to* a structured failure return so the API consumer also sees the error.

**Affected files:**
- `runner-core/Services/IChatService.cs`, `ChatService.cs` â€” new result type; update all callers.
- `runner-core/Services/ISpeechToTextService.cs`, `runner/Services/WhisperSpeechToTextService.cs` â€” same.
- `runner-core/Services/RunnerLocalApiService.cs` â€” error response mapping.
- `runner/MainWindow.xaml.cs` â€” UI error handling at Send / STT call sites.
- `runner-cli/RunnerApiClient.cs` â€” surface server error responses as CLI errors.
- `tests/RunnerLocalApiServiceTests.cs`, `ChatServiceTests.cs` (new), `WhisperSpeechToTextServiceTests.cs` â€” regression tests proving backend failure propagates end-to-end, not empty success.

**Expansion 2026-04-19 (RAG audit fallout):**
- Add a `ChatResult.RagRetrievalFailed(innerError)` variant distinct from "no hits above threshold." `ChatService.cs:159-165` currently catches the retrieval exception, logs `"RAG retrieval skipped: {ex.Message}"`, and returns an ungrounded answer with `usedContext=false` â€” indistinguishable from the threshold case. UI renders a visible warning banner ("Answering without document context â€” retrieval failed: â€¦") on the failure variant. LAN API response carries a header or JSON field (e.g. `X-RAG-Status: retrieval-failed` vs `retrieval-empty`) so API consumers can react differently.

**âš  Watch for:**
- Existing tests may assume empty string = failure. Update, don't preserve â€” the whole point is distinguishing empty vs failure.
- Don't leak backend URLs, auth headers, or stack traces into user-facing error text. Log rich details; show concise messages.

---

### H2 â€” Repo hardening batch (Codex deep-review low-severity sweep)

**Status:** merged PR #163 (2026-04-20). **Complete.**
**Scope:** One-shot housekeeping batch. Slot between bug fixes, not mid-feature.
**Model:** Sonnet 4.6 (mechanical)

**Intent:** Fold all Low-severity Codex findings into one cohesive housekeeping PR so they don't each burn a separate round-trip.

**Concrete targets:**

1. **`build.ps1:35-38`** â€” staged `runner-publish` directory is reused without cleanup. Removed artifacts linger and can be shipped. Fix: `Remove-Item -Recurse -Force` before `New-Item` / `Copy-Item`.
2. **`shared/SsdLogger.cs:40-43`** â€” unsynchronized `File.AppendAllText`. Add a lock or route through a serialized writer. Match the pattern already used by `companion/CompanionLog.cs`.
3. **`shared/SystemResources.cs`, `shared/DriveInspector.cs`** â€” `System.Management` / WMI calls are Windows-only but shared project builds cross-platform. Add `[SupportedOSPlatform("windows")]` attributes or `OperatingSystem.IsWindows()` guards. Resolves the CA1416 warnings.
4. **`.github/workflows/build.yml`** â€” first-party GitHub actions are tag-pinned; pin to exact SHAs per the repo's own TODO (lines ~55-56, 139, 155, 202, 233-238, 307-311 per Codex; verify before changing).
5. **`README.md`** â€” drift: test count says 375 (actual: 380+), target framework says net8.0 (tests are net10.0), offline voice wording is inconsistent across sections. Audit against live state and refresh.
6. **`tests/RunnerLocalApiServiceTests.cs:282-283`** â€” uses `.Result` which trips an xUnit analyzer warning. Convert to `await` in an `async` test.

**Deliberately NOT in H2:**
- Any of the X9/X10/X11/X12/X13 items â€” they get their own PRs.
- The oversized `runner/MainWindow.xaml.cs` and `shared/ViewModels/PrepViewModel.cs` â€” splitting those is a separate, larger refactor task (slot into F3 or a future R2).

**Affected files:** as listed above.

**Exit criterion:** One PR, one commit per logical grouping (build.ps1 fix; platform guards; workflow pinning; docs refresh; test cleanup). CA1416 warnings clean; README reflects live state; no new test failures.

---

### X14 â€” 50 MB upload limit silently rejects files with no user hint

**Status:** triaged 2026-04-19 (v1.2.5 field test). **Low (UX).**
**Scope:** One-shot UX nit.
**Model:** Sonnet 4.6 (trivial â€” design tiny enough to skip Opus).

**Symptom (2026-04-19 field log):**
- User dragged a 140.8 MB DCS Hornet guide PDF into the library. Log records `[WARN] Rejected oversized file (140.8 MB exceeds 50 MB limit): C:\Users\Kninetimmy\Downloads\DCS FA-18C Hornet Guide.pdf` (`G:\logs\runner-20260419.log:377`) but the UI gave no visible explanation â€” user would reasonably assume drop-target malfunction.

**Fix options (pick one at implementation time, not now):**
- Toast / modal on the drop target when a file is rejected, naming the limit and the actual size.
- Pre-drop hint in the library "add files" affordance showing the 50 MB cap.
- Both (toast on rejection + static hint in the empty state).

**Watch for:**
- Don't raise the limit without a downstream plan â€” 50 MB is paired with chunking / embedding throughput assumptions. This is a messaging fix, not a cap change. Actual cap revisit lives in **X15**.
- Check if PrepApp's staging path has the same silent-rejection gap before shipping â€” likely does.

**Affected files (expected):**
- `runner/MainWindow.xaml(.cs)` or whatever handles the library drop target.
- Whatever validator emits the current `WARN` log line â€” surface the same text via a user-visible channel.

**Exit criterion:** rejected file shows a visible, actionable message naming the limit. No silent failure path.

### X15 â€” Revisit RAG file-size and chunk-size caps for large reference PDFs

**Status:** backlog 2026-04-19. **Medium (capability).** Paired follow-up to X14 (messaging).
**Scope:** Investigation + tuning pass, not a one-liner cap bump.
**Model:** Opus planning (touches embedding throughput, index size, memory).

**Driver (2026-04-19 field observation):**
- Chuck's Guides and similar DCS airframe manuals routinely run 120-160 MB and 800-900 pages per jet. Current caps â€” 50 MB file size, 10k chunk size â€” reject these outright at the drop target (see X14 log line). Workaround today would be splitting manuals by hand, which defeats the "drop the whole manual in" UX the library is supposed to offer.
- This is a primary use case for the project (DCS pilot reference lookup), not an edge case â€” the current limit turns the most valuable documents away.

**Staging (rescoped 2026-04-19 RAG audit â€” addresses audit High #3 "slow/memory-heavy/foreground-bound ingest"):**

- **Stage 1 â€” Investigation + targets.** Benchmark current ingest on a 150 MB / 800-page Chuck's Guide: wall-clock, peak RSS, chunk count, failure rate. Decide raised caps (`MaxDocumentSizeMB`, per-doc chunk count headroom). Output: concrete numeric targets. Also decides which later stages actually ship vs can be dropped.
- **Stage 2 â€” Streaming pipeline.** Replace whole-document materialization (`ParsedDocument` â†’ `textItems` â†’ `results` in `DocumentIngestor.cs:96-159`) with a producer/consumer pipeline via `System.Threading.Channels.Channel<T>`: parse page â†’ chunk page â†’ embed chunks â†’ persist, each stage streaming. Drops peak RSS roughly proportional to pages-in-flight Ã— chunk size.
- **Stage 3 â€” Batched embeddings + retry/backoff.** Probe Ollama `/api/embed` for batch input support; if available, send N chunks per request (configurable). `EmbeddingClient.cs:14-32` currently sends one HTTP request per chunk â€” even at concurrency 4 this is throughput-bound. Add exponential backoff on transient HTTP failures. Single-item path preserved as fallback if batch rejected.
- **Stage 4 â€” Background job model.** Move ingest off the WPF UI thread through an `IIngestJobService` with `IProgress<IngestProgress>`. UI posts a job, shows progress; cancellation cooperative. Retire any remaining `.Wait()` / `.Result` in WPF handlers. Fixes the "feels hung before useful progress shows" symptom the audit called out.

**Deliverable shape (informed by Stage 1 data):**
- Raise file cap (likely ~250 MB â€” covers Chuck's Guides + headroom).
- Raise chunk cap to whatever the largest expected manual needs + margin, or remove and rely on natural embedding-time bounds.
- Possibly expose chunk-size/overlap as tunables in PrepApp library settings.
- Update X14's rejection message to match the new cap (coordinate so messages don't drift).

**Watch for:**
- PDF parser OOM on very large files â€” streaming-parse in Stage 2 should fix this; verify.
- Cancellation + progress visibility (today's UX assumes ~seconds; a 150 MB manual is minutes).
- Storage on the SSD itself â€” remember the product ships *from* the SSD, so index bloat eats user capacity.

**Affected files (expected â€” verify when the time comes):**
- Whatever validator emits the current `WARN` log line (same spot X14 touches).
- Library ingestion pipeline (PDF extraction â†’ chunking â†’ embedding).
- Any hard-coded `50 * 1024 * 1024` / `10000` constants in shared.
- `agent_docs/project_arch.md` RAG section â€” update limits + rationale.

**Exit criterion:** Chuck's Guide-sized manuals (120-160 MB, 800-900 pages) ingest cleanly, index in acceptable time with a visible progress indicator, and retrieve quality hits during chat. X14's rejection messaging reflects the new cap.

---

### X16 â€” Unlock dialog dark theme

**Status:** Logged 2026-04-19. **Low (UI polish).**
**Scope:** Runner only. Small.

**Symptom:** The "Unlock Encrypted SSD" password dialog renders in the OS
system light theme (white background, grey Cancel/Unlock buttons, light title
bar) while the rest of Runner uses the neumorphic dark theme. Screenshot
captured 2026-04-19.

**Root cause:** Dialog window likely doesn't inherit the app's dark theme
resource dictionary.

**Fix:** Apply the same dark-theme resource merge the main window uses to the
unlock dialog window.

**Exit criterion:** Unlock dialog matches Runner's dark theme visually.

---

### X17 â€” Textless-page diagnostic (RAG audit C1, scoped down)

**Status:** Backlog 2026-04-19 (RAG audit triage). **Low.**
**Scope:** Stage 1 only at this time; full OCR deferred per decision 2026-04-19.
**Model:** Sonnet 4.6.

**Driver:** Audit flagged "multimodal PDF ingest" (OCR + table extraction + image handling) as Critical #1. Product workload is text-layer DCS manuals with embedded diagrams, not scans. X17 keeps only the diagnostic so we get field data if that assumption breaks.

**Fix (Stage 1):**
- `DocumentParser.cs` flags per-page when extracted text is below a threshold (suggest <20 non-whitespace chars). Carry the flag through `ParsedSegment` / `IngestProgress`.
- Surface in X18's post-ingest summary: "3 of 42 pages in `foo.pdf` had no extractable text."
- No OCR, no behavioral change â€” silently-empty chunks become visibly-empty-and-flagged chunks.

**Affected files:**
- `shared/Documents/DocumentParser.cs`
- `shared/Documents/DocumentModels.cs` â€” add `IsTextless` on `ParsedSegment` or equivalent.
- `shared/Documents/DocumentIngestor.cs` â€” count textless pages into `IndexingProgress`.
- `runner/MainWindow.xaml.cs` â€” render count in X18 summary.
- `tests/DocumentParserTests.cs` â€” synthetic near-empty page fixture.

**Deferred indefinitely (not queued):** OCR engine integration (Tesseract.NET / bundled external / Windows.Media.OCR), table extraction via PdfPig layout analysis, image extraction, vision-model captioning. Revisit only if Stage 1 data shows scanned PDFs in active field use.

**Exit criterion:** A PDF with one or more image-only pages ingests with a visible "N textless pages" count in the post-ingest summary.

---

### X18 â€” Ingest observability

**Status:** Backlog 2026-04-19 (RAG audit triage). **Medium.**
**Scope:** Two stages; small.
**Model:** Sonnet 4.6.

**Symptom:** `DocumentIngestor` populates `IndexingProgress.FailedChunks` (`DocumentIngestor.cs:176`) but `MainWindow.xaml.cs:1002, 1045, 1069` never reads it. Partial-failure ingests show `"Indexing 5/10: example.pdf"` with no hint that N chunks failed. Parse failures caught at `DocumentIngestor.cs:82-84` are logged and swallowed; the UI has no surface for them either.

**Fix:**
- **Stage 1 â€” Surface ingest outcomes.** After every ingest, render an `IngestResult` summary (panel or modal): files imported, files skipped with reasons, textless pages detected (X17 feed), failed chunks, partial-ingest warnings. Reuses data already produced by `IndexingProgress` â€” purely a rendering + collection gap.
- **Stage 2 â€” Configurable failure threshold.** Expose `MaxEmbeddingFailureRatioBeforeAbort` in `PortableConfig` (currently hard-coded `0.50d` at `DocumentIngestor.cs:5`). Default stays 0.50; users can tighten for safety-critical libraries.

**Affected files:**
- `shared/Documents/DocumentIngestor.cs` â€” aggregate outcomes into an `IngestResult` returned from `IngestFilesAsync` / `RebuildIndexAsync`.
- `shared/PortableConfig.cs` â€” add `MaxEmbeddingFailureRatioBeforeAbort`.
- `runner/MainWindow.xaml(.cs)` â€” post-ingest summary panel.
- `tests/DocumentIngestorFailureHandlingTests.cs` â€” assert `IngestResult` shape on partial failure.

**Exit criterion:** Partial-ingest run (embedding handler failing 3/10 chunks on a file) shows a visible post-ingest warning naming the file and failure count. Tightened threshold aborts earlier as configured.

---

### X19 â€” Hybrid retrieval: dense + lexical + neighbor expansion

**Status:** Backlog 2026-04-19 (RAG audit triage). **High (v1.3.x capability).**
**Scope:** Three stages. Opus planning at kickoff (retrieval architecture choice).
**Model:** Opus.

**Driver:** Audit High #1. Retrieval is dense-vector brute-force only (`VectorIndex.cs:9-22` â€” deliberate: ANN rejected for portable/no-native-deps). No BM25/FTS5 fallback, no neighbor expansion, top-K=5. Exact facts in repetitive sections, appendices, captions, and tables are easy to miss even when present. ANN index stays rejected; the other audit points (lexical fallback, expansion) are real for this product.

**Staging:**
- **Stage 1 â€” Evaluation harness.** Small benchmark of real document questions with known correct chunks. Run current retrieval â†’ capture baseline recall@5 / recall@20. Every later stage gates on this metric not regressing. No production code change; pure test/tooling asset.
- **Stage 2 â€” Lexical fallback via SQLite FTS5.** Add a `chunks_fts` virtual table populated alongside vector writes. Merge dense top-K with lexical top-K using reciprocal rank fusion. No new NuGet dependency â€” FTS5 ships with `Microsoft.Data.Sqlite`. Does add a new DB object in the schema; flag at kickoff.
- **Stage 3 â€” Parent/adjacent expansion.** When a chunk is in top hits, also pull `chunk_index Â± 1` neighbors (same file/page) so the LLM sees surrounding context. Depends on X20's richer metadata for clean range logic.

**Explicitly dropped:** Cross-encoder reranker. Adds dependency for speculative gain against the CLAUDE.md "don't gold-plate" rule. Revisit only if Stages 2+3 don't clear the recall bar against the eval harness.

**Affected files:**
- `shared/Documents/VectorIndex.cs` â€” FTS5 virtual table, fusion logic.
- `shared/Documents/RagPromptBuilder.cs` â€” consume expanded chunk set.
- `runner-core/Services/ChatService.cs` â€” pass expansion flag.
- `tests/` â€” eval harness as a first-class test class.

**Exit criterion:** Eval harness recall@5 improves vs. baseline by a chosen delta. Integration tests cover dense-only / lexical-only / fused paths.

---

### X20 â€” Section-aware chunking + richer metadata

**Status:** Backlog 2026-04-19 (RAG audit triage). **High (v1.3.x capability). Depends on X21.**
**Scope:** Three stages. Opus planning at kickoff.
**Model:** Opus.

**Driver:** Audit High #2. Chunker (`DocumentChunker.cs:5-47`) splits text in fixed character windows inside each page; chunk metadata (`DocumentModels.cs:41-53`) stores only page, chunk index, file name, stored path, sha256 â€” no section titles, heading path, character offsets, or content type. Retrieval precision and citation quality suffer vs. what's achievable.

**Staging:**
- **Stage 1 â€” Metadata schema expansion.** Coordinate with X21's schema bump. Chunk row gains: `section`, `heading_path`, `char_offset_start`, `char_offset_end`, `content_type` (text / table / image_ref â€” tables/images populated only if future work adds them). Migration: existing rows get nulls; reindex is the upgrade path.
- **Stage 2 â€” Section-aware chunker.** Parse headings from PDF text flow using PdfPig's font-size signals (size-jump heuristic marks heading boundaries) and markdown `#` levels. Respect heading boundaries + paragraph breaks over fixed character windows. Plain text / CSV / JSON fall back to the current fixed-size chunker.
- **Stage 3 â€” Citation builder upgrade.** `CitationBuilder.BuildDistinct()` extended: `[filename p.42]` becomes `[filename Â§Engines p.42]` when section metadata present. UI SourcesList + CLI footer pick up the richer string automatically.

**Affected files:**
- `shared/Documents/DocumentModels.cs` â€” `DocumentChunk` fields.
- `shared/Documents/VectorIndex.cs` â€” schema + upsert.
- `shared/Documents/DocumentChunker.cs` â€” section-aware strategy.
- `shared/Documents/DocumentParser.cs` â€” expose font-size / heading signal.
- `shared/Documents/CitationBuilder.cs` â€” richer citation format.
- `tests/` â€” fixture with known section structure; assertions on chunk section attribution.

**Exit criterion:** A manual-style PDF with `## Engines` heading produces chunks attributed to that section; citations render with `Â§Engines`.

---

### X21 â€” Embedding provenance + compat gating

**Status:** Done â€” PR #157 (`449ec2e`), merged 2026-04-19. Stages 1â€“2 shipped; Stage 3 is X21b.
**Scope:** Three stages. Small but foundational.
**Model:** Sonnet 4.6.

**Driver:** Audit High #4. Neither manifest nor chunk rows record the embedding model, vector dimension, parser version, or chunker version (`DocumentModels.cs`, `VectorIndex.cs:72-87`). On dimension mismatch at query time, `VectorIndex.DotProductSimd` (`:480-481`) returns 0 silently â€” mismatched chunks score zero, visible hits quietly drop, no error surfaced. Any change to the embedding model (via config edit) risks silent corruption of existing indexes.

**Staging (Stage 3 split out as X21b â€” 2026-04-19):**
- **Stage 1 â€” Persist provenance.** Add columns to `chunks` and fields to manifest: `embedding_model`, `embedding_dimension`, `parser_version`, `chunker_version`. Populate at write time. Schema migration: existing rows get `"unknown"` with a recorded schema version bump.
- **Stage 2 â€” Gate at query + ingest.** On query, verify running embedding config matches chunk rows; if mismatch, refuse and emit a clear error (not a silent zero score). On ingest into an existing library, refuse writes if config differs from recorded provenance; prompt controlled reindex. Ends the silent-zero-score failure mode.

**Affected files (Stages 1â€“2):**
- `shared/Documents/DocumentModels.cs` â€” manifest + chunk fields.
- `shared/Documents/VectorIndex.cs` â€” schema, migration, query-time check.
- `shared/Documents/DocumentIngestor.cs` â€” write-time check.
- `shared/PortableConfig.cs` â€” expose current model / dim as the source of truth for comparison.
- `tests/` â€” intentional model mismatch returns clear error instead of zero-score silent failure.

**âš  Watch for:**
- X20 Stage 1 shares the schema bump â€” sequence so X21 Stage 1 lands first and X20 extends, or plan a single combined migration.

**Exit criterion:** Test with an embedding model swap returns a clear `EmbeddingModelMismatch` error at query time.

---

### X21b â€” PrepApp reindex prompt on model change

**Status:** Done â€” PR #158 (`92625a9`), merged 2026-04-19.
**Scope:** Single stage. UI-only â€” consumes X21's `EmbeddingModelMismatch` signal.
**Model:** Sonnet 4.6.

**Driver:** When the user changes the embedding model in config, PrepApp should detect the mismatch (via X21's provenance gate) and surface a one-click reindex action rather than silently producing zero-score results.

**Scope:** PrepApp detects mismatch on model config change â†’ warning dialog "Library was indexed with model X; switched to Y â€” reindex required" â†’ one-click reindex using X10's rebuild-from-stored path.

**âš  Implementation note (from X21 shipping):** The rebuild path (`RebuildIndexAsync`) must clear existing chunks for the library before re-embedding, or accept a `force` flag that bypasses `CheckProvenance`. Without this, the rebuild will immediately throw `EmbeddingModelMismatchException` on the first chunk write into a library that still has old-model rows.

**Affected files:**
- `prep-app/` â€” model change handler, reindex prompt, reindex action wired to `DocumentOperationsService.RebuildIndexAsync`.

**Exit criterion:** Changing the embedding model in PrepApp config triggers the warning dialog; confirming triggers a full reindex and clears the mismatch.

---

### X22 â€” Prompt packing + grounding enforcement

**Status:** Backlog 2026-04-19 (RAG audit triage). **Medium.**
**Scope:** Two stages.
**Model:** Sonnet 4.6.

**Driver:** Audit Medium #2. `ChatService.cs:145` truncates context to a fixed 4500 characters; not token-aware â€” under-uses large-context models, over-fills small ones. Grounding instruction in `RagPromptBuilder.cs:24-26` is a soft ask: *"Use the following reference context when answering. If context is insufficient, say so."* Citations built by `CitationBuilder` are post-hoc rather than required inline.

**Staging:**
- **Stage 1 â€” Token-aware budget.** Replace `maxContextChars=4500` with `maxContextTokens` derived from the active model's context window minus reserved output. Tokenizer choice is a flag-ahead dependency (`Tiktoken` or equivalent) per global CLAUDE.md â€” surface at kickoff before adding.
- **Stage 2 â€” Stronger grounding.** Upgrade instruction block: require inline citations for factual claims using the existing citation strings, require "not in provided context" when evidence absent. Pair with a retrieval-quality check (X19) so strict grounding doesn't mask a retrieval regression as a model refusal.

**Affected files:**
- `shared/Documents/RagPromptBuilder.cs` â€” token-aware budget + stronger instruction block.
- `runner-core/Services/ChatService.cs` â€” token budget wiring; model-window lookup.
- `shared/` â€” possible new `Tiktoken` or equivalent dependency (flag at kickoff).
- `tests/RagPipelineIntegrationTests.cs` â€” asserts inline citation presence on grounded answer.

**Exit criterion:** Token-budgeted context fills available window without exceeding it; grounded answer on a test question includes inline citations; unsupported question elicits explicit "not in provided context" rather than hallucinated detail.

---

### X23 â€” Representative test fixtures

**Status:** Backlog 2026-04-19 (RAG audit triage). **Medium.**
**Scope:** One-shot.
**Model:** Sonnet 4.6.

**Driver:** Audit Medium #3. Tests use synthetic text files (`.txt`/`.md`/`.json`/`.csv`) and tiny PDFs built in temp dirs. No real-world PDFs, no re-ingest scenario, no rebuild scenario. Regressions in parse / chunk / index behavior on actual documents won't be caught until field use.

**Fix:**
- Add PDFs to `tests/fixtures/` â€” **public-domain only**, a few MB max each:
  - text-layer PDF (exercises normal path)
  - scan-only PDF (exercises X17 textless-page diagnostic)
  - updated-document re-ingest scenario (exercises X10 replacement cleanup)
  - rebuild-with-missing-original scenario (exercises X10 rebuild-from-stored)
- Integration tests in `tests/RagPipelineIntegrationTests.cs` exercise each path end-to-end against a real local Ollama if `FREEAI_TEST_OLLAMA_HOST` env var is set, else skip with a visible message. Honors the "integration tests must hit a real command â€” mock-only tests hid regressions" feedback memory.

**Affected files:**
- `tests/fixtures/` â€” new directory with public-domain PDFs.
- `tests/RagPipelineIntegrationTests.cs` â€” new fixture-driven cases.
- `tests/DocumentParserTests.cs` â€” real-PDF cases.

**âš  Watch for:**
- Licensing: public-domain only. No "this looked free" fixtures. Document the source of each file in a `tests/fixtures/README.md`.
- Fixture size: keep each file small (a few MB max) so the repo doesn't bloat.

**Exit criterion:** Integration suite exercises text-layer / scan-only / re-ingest / rebuild-after-move scenarios against real fixtures. Skipped gracefully when no Ollama host is configured.

---

### X24 â€” Citation staleness after rename

**Status:** Shipped â€” PR #155 (`53ecdf9`), v1.2.9.
**Scope:** One-shot.
**Model:** Sonnet 4.6.

**Symptom:** After Stage 2 rename detection updates a manifest entry's `SourceOriginalPath`/`FileName`, the `chunks` table still holds the old `source_file_name`. `CitationBuilder.cs:8-9` renders `chunk.SourceFileName` into user-visible citations, so post-rename answers cite by the old filename until the document is re-embedded.

**Root cause:** `DocumentIngestor.cs:66-83` rename path updates the manifest only; no corresponding UPDATE on the `chunks` table. Retrieval still works (stored_relative_path is unchanged â€” sha didn't change), but citations are cosmetically wrong.

**Fix:**
- Add `VectorIndex.UpdateFileName(libraryId, storedRelativePath, newName)` â€” parameterized UPDATE on `chunks.source_file_name`.
- Call it from the single-sha rename branch in `DocumentIngestor.cs` right after the manifest entry is updated.

**Affected files:**
- `shared/Documents/VectorIndex.cs` â€” new helper.
- `shared/Documents/DocumentIngestor.cs` â€” invoke from rename branch.
- `tests/DocumentReplacementTests.cs` â€” extend `RenameWithSameContent_UpdatesPathAndSkipsReEmbed` to assert `chunks.source_file_name` matches new name.

**âš  Watch for:**
- Parameterize the UPDATE (security invariant).
- Don't touch chunks on the `>1 sha match` fallthrough â€” that path creates new chunks via normal ingest.

**Exit criterion:** Renamed documents cite by their current filename in RAG answers, not their old one.

---

### X25 â€” Extend File.Replace retry to remaining call sites

**Status:** Shipped â€” PR #155 (`2f7dcd8`), v1.2.9.
**Scope:** One-shot.
**Model:** Sonnet 4.6.

**Symptom:** PR #153 wrapped the three `File.Replace` calls in
`SsdEncryption.SaveEncryptedConfigAsync` with a retry helper to absorb
Windows Defender/indexer sharing-violation flakes. Two other
`File.Replace` call sites carry the same latent flake risk and remain
unprotected:
- `shared/PortableConfig.cs:314` â€” plaintext-config save.
- `shared/Documents/DocumentLibraryManager.cs:48, 136` â€” registry +
  library-manifest saves.

**Fix:** Promote `ReplaceWithRetry` (currently private in
`SsdEncryption.cs`) to a shared helper â€” e.g. `shared/Io/FileOps.cs` â€”
and route the three call sites through it. Preserve the exact retry
policy (5 attempts, 25 ms base backoff doubling, only
`IOException` / `UnauthorizedAccessException`).

**Affected files:**
- `shared/Io/FileOps.cs` (new) or move the helper out of `shared/SsdEncryption.cs`.
- `shared/PortableConfig.cs`, `shared/Documents/DocumentLibraryManager.cs`
  â€” call the shared helper.
- `tests/` â€” optional light regression test if a reasonable seam exists;
  the original CI flake is hard to reproduce deterministically.

**âš  Watch for:**
- Don't expand scope into a general retry framework. This is narrowly
  `File.Replace`-specific.
- Keep the retry policy identical â€” deviating makes one of the two
  versions silently stale.

**Exit criterion:** All four `File.Replace` call sites in the repo use
the same retry helper.
