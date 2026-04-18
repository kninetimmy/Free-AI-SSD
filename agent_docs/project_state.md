# Project State

Last updated: 2026-04-17

## Currently building

F1 fix open as PR #129 (MSFT_PhysicalDisk primary path + fallback; diagnostic script in tools/). Once merged, README update for F1 is due. Next focus: B3 — "Format & Prepare Drive" button actually formats.

## Planned work — TODO backlog triage

Source: `C:\Users\Kninetimmy\Downloads\# Free-AI-SSD Project TODO.md` (dictated-while-driving notes — treat assumptions with skepticism).

Each section below is addressable independently. Sections flagged **⚠ premise check** contain TODO claims that don't match the code — confirm with Stephen before implementing. After each section ships, update README with the user-facing change (per Stephen's rule).

---

### B2 — Build LAN discovery (Runner broadcasts, Companion listens) + relocate host IP field
**Scope:** Multi-stage (4 stages). **Model:** Opus 4.7 for planning. **Stephen confirmed (2026-04-17): yes, build discovery.**

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
**Scope:** One-shot. **Model:** Sonnet 4.6. **Stephen confirmed (2026-04-17): format to correct FS, then ensure folder structure.**

**Existence:** Bug confirmed. `FormatPrepareAsync` (`PrepViewModel.cs:741-790`) **does not format**. It only calls `_driveService.EnsureSsdStructure(root)` (folder layout — keep this, it's already correct) and saves a fresh `PortableConfig`. No `format.exe`, no `diskpart`, no `Format-Volume` PowerShell call anywhere in the repo (verified via grep). The `VolumeLabel` TextBox binding exists in MainWindow.xaml:356-360 but is never consumed by `FormatPrepareAsync` — dead binding.

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
- Re-confirm erase via the existing `ConfirmErase` dialog (which gets re-themed in B1).

**⚠ Staging note:** Test in a VM or with a spare USB stick first. Don't exercise against Stephen's live SSD until manually verified.

---

### F1 — ⚠ **REGRESSION: USB SSD detection not working in v1.2.0** (was Feature, now Bug)
**Scope:** Diagnose first (one-shot), then fix + rework. **Model:** Sonnet 4.6 for diagnosis; Opus 4.7 if a full rework is needed. **Priority: HIGH — this is blocking Stephen's live-drive test pass.**

**Stephen's finding (2026-04-17):** v1.2.0 does not show his currently-plugged-in USB SSD in the drive dropdown. PR #121's WMI `InterfaceType='USB'` detection fails for his specific drive.

**Existence:** `DriveInspector.cs:79-132` queries WMI `SELECT DeviceID FROM Win32_DiskDrive WHERE InterfaceType = 'USB'`, then chains `ASSOCIATORS OF` queries to reach the logical drive letter. Per stable decisions, "WMI failure falls back silently to DriveType-only — fail-open is acceptable here" — which is exactly what's happening for Stephen's drive. The silent fallback is masking the real problem.

**Likely root causes (ordered by probability):**
1. **`InterfaceType` is not 'USB' for his enclosure.** USB-NVMe adapters often report `InterfaceType = 'SCSI'` or `'IDE'` at the WMI layer, because the NVMe protocol tunnels through a USB Attached SCSI (UAS) translator. Many modern portable SSDs (Samsung T7, WD My Passport SSD) hit this.
2. **ASSOCIATORS query chain breaks** — one of the three `ASSOCIATORS OF` steps returns empty for his drive (possible if partition table is GPT with quirky layout).
3. **Silent `catch`** at `DriveInspector.cs:127` eats a real exception — WMI permissions, moniker issue, etc.
4. **`IsRemovable` vs `IsFixed`** — his drive may report neither, landing in a gap.

**Diagnostic plan (do this first before any code change):**
- Have Stephen run a one-shot WMI diagnostic script that dumps every `Win32_DiskDrive` with `InterfaceType`, `MediaType`, `Model`, `PNPDeviceID`, plus the `Win32_DiskDriveToDiskPartition` and `Win32_LogicalDiskToPartition` chains for each.
- Compare against his plugged-in SSD to see what Windows actually reports.
- That tells us whether to broaden the WMI query, use a different detection path (e.g., `MSFT_PhysicalDisk` with `BusType`), or add a DeviceIoControl-based fallback.

**Likely fixes (depending on diagnostic):**
- Broaden WMI: `WHERE InterfaceType = 'USB' OR PNPDeviceID LIKE 'USB%'` or add `MSFT_PhysicalDisk.BusType IN (7 USB, 8 iSCSI, 9 SAS, 17 NVMe)` filter.
- Surface detection failures in the log instead of silent swallow, so the next test pass tells us what's happening.
- Add the `DriveKind` enum + badge work from the original TODO as a follow-up after detection is fixed.

**Affected files:**
- `shared/DriveInspector.cs` — broaden detection, log failures non-silently for diagnosis
- `shared/SsdLogger.cs` or equivalent — route WMI exceptions somewhere visible
- `prep-app/MainWindow.xaml:31-40` — badge (after detection fixed)
- `tests/DriveInspectorTests.cs` — regression test with mocked WMI outputs for various enclosure types

**⚠ Stable decision update needed:** Current decision says "WMI failure falls back silently to DriveType-only — fail-open is acceptable here." That behavior is now actively hiding a bug. Change to: log failures + still fail-open for enumeration, but make the failure visible.

---

### F2 — Live model list fetch (HuggingFace / Ollama library)
**Scope:** One-shot for v1. **Model:** Sonnet 4.6.

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
**Scope:** Multi-stage (4 stages). **Model:** Opus 4.7 for the planning prompt (as TODO flags), Sonnet 4.6 for implementation stages.

**Existence:** Current layout is 2 tabs (`MainWindow.xaml:77-442`): Model Manager (line 83) and Drive Setup (line 302). TODO's observations are accurate:
- "Add selected to configuration" (line 111) and "Add to config" (line 203) are redundant.
- "Pull/Install" (line 186) and "Pull Selected" (line 208) also overlap.
- Warning strip (`SelectedDriveWarning` at Row 2) is collapsible per PR #120's stable decision, but positioned below the log on the Model Manager side — visibility concern is valid.
- Log panel (Row 3, `1.5*` height) is squeezed.

**Affected files (major rewrite):**
- `prep-app/MainWindow.xaml` — split into 3 tabs; rewire bindings
- `shared/ViewModels/PrepViewModel.cs` (1154 lines currently) — may need splitting into per-tab view models
- `prep-app/MainWindow.xaml.cs` — FTUE step targets may need updating (the overlay targets elements by name)
- Stable decisions file (this doc) — update decisions about warning strip placement

**Staging (recommend Opus draft the detailed plan first):**
- **Stage 1** — Extract Tab 1 "Model Downloader": move Starter Models + new "Send to Configuration →" button; remove Configured Models from this tab.
- **Stage 2** — Extract Tab 3 "Configuration / Finalize": move Configured Models + Finalize + Check Readiness here.
- **Stage 3** — Clean Tab 2 "Drive Setup": relocate warning strip into this tab prominently; remove host IP (gated on B2 resolution).
- **Stage 4** — Eliminate redundant buttons; consolidate Pull/Install flow.

**⚠ Watch for:** FTUE overlay (MainWindow.xaml:533-578) hard-references element names — any renames break the onboarding tour.

---

### F4 — Profile FTUE moves entirely to PrepApp (+ companion install target selector)
**Scope:** Multi-stage (4 stages). **Model:** Opus 4.7 for planning. **Stephen confirmed (2026-04-17): move FTUE entirely to PrepApp; Runner silently reads `ActiveProfile` from SSD config at launch.**

**Existence:**
- Profile system exists in **Runner only**: `shared/Profile/UserProfile.cs` (enum: `GeneralAssistant`, `FlightSim`), `shared/Profile/ProfileDefaults.cs` (applies defaults), `runner/ProfileSelectionDialog.xaml(.cs)` (required on first run, `isRequired:true` blocks close).
- PrepApp has **zero profile awareness** — grepped prep-app/ for `UserProfile`, no matches.
- Companion install in PrepApp is a **single SSD-only checkbox** (`MainWindow.xaml:374-379`, `InstallVrCompanion`). No local-install option. No "both" option. Staging is at SSD `/companion` only.

**Stable decision update needed:** Current decision "First-run profile dialog is required — user must choose before the app proceeds" (enforced in Runner at `ProfileSelectionDialog.xaml.cs:18-24`). New behavior: **PrepApp is the sole FTUE owner; Runner reads `ActiveProfile` from config and never prompts on first run.** In-Runner profile toggle stays (for mid-session switching per existing pill-toggle UX).

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
- Stable decisions block at bottom of this doc — update the first-run profile decision

**Staging:**
- **Stage 1** — FTUE profile selection in PrepApp FTUE overlay; write `ActiveProfile` to SSD config at Finalize. Delete or soften Runner first-run prompt.
- **Stage 2** — Post-setup launch flow ("SSD ready — launch Runner now?" → Flight Sim: bindings import + doc ingest walkthrough; General: doc ingest only).
- **Stage 3** — Companion install target selector UI (Program Files / custom path / SSD / both).
- **Stage 4** — Local installer logic (copy to target, create Start Menu + optional desktop shortcut, register uninstaller?).

**⚠ Architecture clarity:** TODO's two-machine description (🖥️ VR PC runs Companion, 💾 SSD machine runs Runner) is accurate and matches current code — Companion connects to Runner over LAN (`CompanionRuntime.TryBuildBaseUri`).

---

### I1 — Two-machine architecture diagram in PrepApp onboarding
**Scope:** Folded into F4. **Model:** inherits F4's (Opus planning, Sonnet implementation). **Stephen confirmed (2026-04-17): build as part of F4's FTUE rebuild.**

**Existence:** No such diagram exists. PrepApp's FTUE overlay (`MainWindow.xaml:533-578`) is a spotlight-ring tutorial, not a full illustrated step.

**Integration into F4:** The diagram becomes the **first step of F4's FTUE flow**, before profile selection. Flow is: *see two-machine architecture → choose profile → finish drive prep → launch Runner*. Opus planning for F4 should include this as Step 1 of the rebuilt FTUE.

**Affected files (under F4):**
- `prep-app/MainWindow.xaml:533-578` — extend FTUE overlay with a full illustrated step
- Possibly new asset (SVG embedded via XAML `Path`/`Canvas`, or a PNG in Resources/)

**No standalone implementation.** When tackling F4, include this as part of the Stage 1 scope.

---

### README update rule

Per Stephen's requested workflow: after each **section** ships (not each stage), update `README.md` with a meaningful description of the user-facing change — not a changelog dump. The current README was refreshed in PR #123 (796719d) with real screenshots; that's the style to match.

## Last session

2026-04-17 — Three items shipped. PR #127 (74224c9) fixed the Codex-flagged `ThemedMessageDialog` height regression: `MaxHeight="560"` on the Window, `ScrollViewer` wrapping `MessageText` (MaxHeight=350). PR #128 (0e27815) added B1 user-facing summary to README. PR #129 (ff057e3, b4888e9) fixed F1 USB SSD detection — root cause was UAS adapters reporting `InterfaceType='SCSI'` instead of `'USB'` in `Win32_DiskDrive`, causing Stephen's SSD to be silently missed; switched primary path to `MSFT_PhysicalDisk WHERE BusType=7` (Storage namespace) with Win32_DiskDrive ASSOCIATORS as fallback; replaced silent `catch {}` with `Trace.WriteLine`. Diagnostic script added to `tools/Diagnose-UsbDrives.ps1`. PRs #127 and #128 merged; #129 open pending review.

2026-04-17 — B1 completed across 3 PRs. PR #124 (d39878c) restyled the three unthemed PrepApp dialogs (EraseConfirmDialog, EncryptionSetupDialog, RemoveModelDialog) to match the neumorphic dark theme. PR #125 (92f6872) swapped the "type ERASE" confirmation for a checkbox-gated Proceed and collapsed `ConfirmFixedDrive`'s two sequential MessageBox popups into a single themed `FixedDriveConfirmDialog`. PR #126 (2fed670) built `ThemedMessageDialog` with static `ShowInfo/ShowWarning/ShowError/Confirm` helpers and replaced all 9 remaining raw `MessageBox` call sites in `DialogService` and `EncryptionSetupDialog`; `App.xaml.cs` crash handlers intentionally kept raw (must survive theme-load failures). CI failure on first push: `dotnet format` stripped `using System.Text.Json;` from `ModelOperations.cs` (tests project compiles it directly without prep-app's GlobalUsings); fixed in 1000756 by adding to `tests/GlobalUsings.cs`. Codex adversarial review flagged one unresolved medium finding: `ThemedMessageDialog` has no height cap or `ScrollViewer` — `ConfirmSizingWarnings` payloads could push buttons off-screen.

2026-04-17 — Planning/triage only (no commits). Read Stephen's
`Downloads/# Free-AI-SSD Project TODO.md` (9 items: 3 bugs, 5
features, 1 idea) and cross-walked each against the repo. Produced
the Planned work block in this doc (B1–B3, F1–F4, I1) covering
existence check, affected files, staging, and validity flags.
Flagged three TODO items with invalid premises: B2 (no LAN discovery
exists in the repo), F1 (WMI InterfaceType='USB' detection from PR
#121 is failing against Stephen's actual drive — reclassified as
regression, now priority 1), F2 (catalog is JSON-file-based, not
hardcoded as TODO claimed). Got answers to all 6 design questions.
I1 folded into F4 Stage 1. Workflow established: "tackle X" →
implementation prompt + recommended model from the section's entry.

2026-04-17 — Three PRs shipped in same-day session. PR #122 (299223e) fixed WMI resource leaks — `ManagementObjectCollection` and `ManagementObject` instances were never disposed in `DriveInspector`, `SystemCompatibility`, and `SystemResources`; also fixed missing `using` on the searcher in `SystemCompatibility`. PR #123 (796719d) updated the README: replaced static SVG mockup with real PrepApp screenshots (Model Manager + Drive Setup), added Recent Changes for PRs #120–122, corrected test count 212 → 311. v1.2.0 release workflow dispatched (Windows-only); result TBD. Codex adversarial review of PR #122 returned clean (approve, no findings).

2026-04-17 — Two PRs shipped. PR #120 (eb6f2c1) committed the prep-app
UI polish pass: implicit theme styles for DataGrid, TabControl, GroupBox,
CheckBox in Controls.xaml; Model Manager layout restructured; Drive Setup
fully themed; warning strip made collapsible. PR #121 (d9b6fd3) fixed the
drive selection bug — USB SSDs that Windows classifies as DriveType.Fixed
now appear in the default dropdown via WMI InterfaceType detection.
311/311 tests green throughout.

## Next up

1. **Merge PR #129** (F1 USB detection fix) — then update README with F1 summary
2. **B3 — Format button actually formats** (foundational for the prep flow)
3. **F4 — profile FTUE in PrepApp + companion install target selector** (multi-stage, Opus planning)
4. **B2 — build LAN discovery** (multi-stage, Opus planning; can run in parallel with F4)
5. **F3 — PrepApp 3-tab restructure** (Opus planning)
6. **F2 — live model list fetch** (smaller feature)
7. ~~I1 — architecture diagram~~ (folded into F4 Stage 1)

**Workflow when Stephen says "tackle section X":**
1. Claude reads the section's entry in the Planned work block below.
2. Claude outputs a well-formed implementation prompt (per global CLAUDE.md's prompt-refinement rule) covering: intent, scope, affected files, staging, constraints. If the section is multi-stage, the prompt targets **Stage 1 only** unless Stephen says otherwise.
3. Claude states the recommended model for that prompt (the `**Model:**` line from the section). If the current model doesn't match, Claude pauses for Stephen to switch before implementing.
4. If the section is flagged for Opus planning, Claude drafts the plan first and waits for Stephen's approval before implementation.
5. Claude asks clarifying questions if the section's scope has gaps (e.g. unresolved design decisions inside the section).
6. After implementation ships and is confirmed working, Claude updates `README.md` with a meaningful user-facing summary of the change.

## Open questions for Stephen

**Answered 2026-04-17:**
- ~~B2: build discovery? → **Yes, build it.**~~
- ~~B3: actually format? → **Yes, format to correct FS then ensure structure.**~~
- ~~F1: polish or close? → **Neither — it's broken. Diagnose and fix.**~~
- ~~F4 profile: move entirely to PrepApp? → **Yes, PrepApp owns FTUE; Runner reads config silently.**~~
- ~~F4 companion install target: → **Program Files default + desktop shortcut optional + custom path for portable use.**~~
- ~~I1: standalone or fold into F4? → **Fold into F4 as Step 1 of the rebuilt FTUE.**~~

**Still open:** None.

## Stable decisions (don't revisit)

- Only two profiles: **Flight Sim** and **General Assistant** — no custom/third profiles
- UI/UX must follow existing neumorphic dark theme and shared controls (Colors.xaml, Controls.xaml, etc.)
- Profile is switchable after first launch (not a one-time setup choice)
- Profile stored as `ActiveProfile` (`UserProfile` enum) on `PortableConfig` — no separate file
- First-run profile dialog is **required** — user must choose before the app proceeds; no default assumed
- .NET SDK/TFM bumped to 10.0 — x64 .NET 8 runtime not present on dev machine; shared lib stays net8.0, tests target net10.0, WPF apps stay net8.0-windows (runtime is installed x86 only for 8.0)
- `BindingsImportCard` and `PttCard` are the two XAML elements gated by profile — do not add a third without updating `RefreshProfileVisibility()`
- Mid-session profile changes save to config but don't re-init services — restart required for voice features; this is by design
- Pill toggle does a **direct apply** (no dialog re-open) — `ProfilePill_Checked` handler applies profile, saves config, calls `NotifyRestartRequired()` directly
- DataGrid, TabControl/TabItem, GroupBox, CheckBox all styled via implicit styles in `Controls.xaml` — do not add per-control inline styling for these in WPF hosts
- Drive warning (`SelectedDriveWarning`) lives in its own collapsible strip (Row 2 of root grid), not in the log header — keep it there for safety visibility
- Model tag input overlays the tab strip via `Panel.ZIndex=2` + `BgBaseBrush` background — intentional, not a z-order bug
- USB SSD drive detection primary path: `ROOT\Microsoft\Windows\Storage` `MSFT_PhysicalDisk WHERE BusType = 7` (USB) → `MSFT_Partition.DriveLetter`. Fallback: legacy `Win32_DiskDrive WHERE InterfaceType='USB'` ASSOCIATORS chain (kept for compatibility but misses UAS adapters that report SCSI). Both paths log failures via `Trace.WriteLine` instead of silently swallowing. Established F1 fix (PR #129). Internal drives still require the ShowFixedDrives toggle. Fail-open is acceptable here (drive enumeration, not a security gate).
- WMI disposal pattern: always `using var collection = searcher.Get()` then `using (obj) { ... }` for each loop variable — `ManagementObjectCollection` and `ManagementObject` hold COM handles and must be explicitly disposed. Established PR #122.
- TODO backlog workflow: "tackle section X" → Claude outputs a well-formed implementation prompt + states the recommended model from the section's `**Model:**` line in the Planned work block. Multi-stage sections target Stage 1 by default unless Stephen overrides. README update follows each completed section, not each stage.
- `ThemedMessageDialog` is PrepApp's general-purpose dialog primitive. All new PrepApp dialogs use it (or a custom Window with the same theme resources). `App.xaml.cs` crash handlers are the explicit exception — stay as raw `MessageBox` with zero dependency on the app resource graph.
- Files compiled by the tests project via `<Compile Include>` must carry their own explicit `using` directives — don't rely on the owning project's `GlobalUsings.cs`. The test project's `GlobalUsings.cs` is the correct fix location (not suppressions in source files). Established PR #126.
