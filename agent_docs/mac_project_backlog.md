# macOS Support Backlog

Purpose: staged backlog for turning the current macOS beta into a serious
supported Free-AI-SSD platform. This file is the source of truth when Stephen
asks for "mac tasks", "the next mac step", or a macOS support implementation
prompt.

Ground rule: macOS support must be based on actual repo capabilities, not
README implications. As of this backlog, `mac-runner/` is a thin Swift beta
that starts macOS Ollama and sends direct non-streaming `/api/generate`
requests. It does not provide Windows Runner parity.

## How To Use

When asked to tackle a macOS item:
1. Read this file first.
2. Read only the item in question plus referenced repo files.
3. Confirm scope if the item is older than a few weeks.
4. Keep Windows security invariants intact: encrypted config, trust checks,
   `PathGuards`, and `ProcessRunner.ArgumentList`.
5. Do not duplicate RAG, encryption, or network API logic in Swift. Extract or
   reuse shared/core services instead.

## Current macOS Reality

What exists:
- `mac-runner/Sources/main.swift`: single-file SwiftUI app.
- Optional CI path in `.github/workflows/build.yml` gated by `include_macos`.
- PrepApp can stage `Runner.app` and `mac/tools/ollama` when beta artifacts are
  present.
- SSD layout already reserves `mac/Runner.app` and `mac/tools/ollama`.

What works today:
- Select or infer SSD root.
- Refuse encrypted drives with "mac unlock not supported yet".
- Read installed model names from plaintext `portable-config.json`.
- Start/stop `mac/tools/ollama/ollama serve` with `OLLAMA_MODELS=<SSD>/models`.
- Send a non-streaming direct Ollama `/api/generate` request.
- Append minimal logs to `logs/macos-runner.log`.

Missing from current macOS Runner:
- Encrypted config unlock/save.
- Shared `ChatService` / RAG / citations.
- Document library management and ingestion.
- Streaming chat.
- Runner LAN API host.
- RunnerCli compatibility against a Mac host.
- Voice input, TTS, audio routing.
- HOTAS/PTT.
- DCS bindings import UI.
- Signing/notarization as a supported release path.

## Planning Notes

- Once macOS reaches the same practical runtime level as the Windows Runner,
  remaining feature work should be planned as cross-platform work by default:
  shared/core first, then Windows and macOS host adapters or UI surfaces as
  needed. Windows-only or Mac-only delivery should be an explicit exception,
  not the accidental default.
- Stephen currently has temporary access to a Mac with Xcode and an Apple
  Developer account. Use that window for MAC10/MAC11 validation when the Mac
  app is stable enough to make signing/notarization meaningful.
- Cross-platform PrepApp parity (MAC16/17/18) was added 2026-05-05. A Mac-only
  user must be able to download, prep, and run Free-AI-SSD without owning a
  Windows machine, and a Mac-prepped drive must be byte-for-byte interchangeable
  with a Windows-prepped drive (encrypted config roundtrips both ways). APFS is
  dropped from supported targets; exFAT is the universal target. NTFS-from-Mac
  and APFS-from-Windows are accepted OS limits, not project gaps.
- The Mac Runner is the cross-platform composition target: Mac runs Runner
  (RAG, DCS bindings, encrypted config); Windows Companion connects to it over
  LAN; X4's web chat UI is served by the same Mac Kestrel for free because
  `RunnerLocalApiService` lives in `runner-core/` post-MAC3. Companion-on-Mac
  itself stays deferred.

## Ordered Backlog

### MAC0 - Truth-in-docs + roadmap anchor

**Status:** done 2026-05-04
**Scope:** documentation only
**Risk:** Low
**Goal:** Stop public docs from implying macOS has Windows Runner feature
parity. Make the beta limitations explicit and point future development at
this backlog.

**Likely files:**
- `README.md`
- `docs/QUICKSTART.txt`
- optionally `agent_docs/project_state.md`

**Do not change:**
- Runtime behavior.
- Packaging workflow.
- SSD layout.

**Acceptance criteria:**
- README says macOS is currently a beta direct-Ollama runner, not a full Runner
  equivalent.
- QUICKSTART separates Windows stable from macOS beta and names the missing
  Mac features plainly.
- Existing Windows feature claims remain intact.

**Tests:** Not applicable beyond doc review.

---

### MAC1 - Define supported Mac baseline

**Status:** done 2026-05-05
**Scope:** planning / decision record
**Risk:** Low
**Goal:** Lock the minimum viable supported Mac release target before code
churn.

**Outcome:** The supported Mac baseline is recorded in
`agent_docs/project_decisions.md` under the 2026-05-05 MAC1 entry.

**Decisions captured:**
- Minimum supported OS: macOS 11 Big Sur.
- Hardware: Apple Silicon only; Intel Macs are unsupported.
- App artifacts: arm64-only; no x86_64 or universal Free-AI-SSD app promise.
- Shared Windows + macOS SSD format: exFAT.
- Windows-only SSD format: NTFS.
- APFS is Mac-only and deferred until a Mac-native prep/staging workflow exists.
- First supported Mac release requires encrypted config unlock/save, verified
  macOS Ollama start/stop, streaming/non-streaming chat, RAG citations,
  document library use, useful diagnostics, and honest packaging state.
- Deferred beyond first supported Mac release: voice/STT/TTS, HOTAS/PTT, DCS
  import UI, Companion split-PC workflows, and Windows-equivalent Prep UI.
- UI stance: keep Swift/SwiftUI as the native thin Mac UI over shared/core
  services unless MAC3-MAC7 prove that path blocks parity or duplicates core
  business logic.

**Likely files:**
- `agent_docs/project_decisions.md`
- `agent_docs/mac_project_backlog.md`

**Acceptance criteria:**
- A dated decision records the supported Mac baseline.
- Minimum viable Mac release is defined as: encrypted config unlock, verified
  macOS Ollama start/stop, streaming/non-streaming chat, RAG citations,
  document library use, useful diagnostics, and honest packaging state.

**Tests:** Not applicable.

---

### MAC2 - Platform dependency audit and guardrails

**Status:** done 2026-05-05
**Scope:** codebase audit + tests/build guardrails
**Risk:** Medium
**Goal:** Make the portable-vs-Windows-only boundary explicit before moving
  Runner services.

**Outcome:** Current blockers and the split plan are recorded in
`agent_docs/mac_platform_dependency_audit.md`. Guardrail tests were added in
`tests/MacPlatformBoundaryTests.cs` to keep `shared/` and `runner-cli/`
portable-shaped while MAC3+ pays down the known Windows-only shared-package
debt.

**Known blockers:**
- `shared/FreeAiSsd.Shared.csproj` references `System.Management`, `NAudio`,
  and `SharpDX.DirectInput`.
- `runner`, `prep-app`, and `companion` are WPF / `net8.0-windows`.
- Audio capture, PTT sounds, and HOTAS live under `shared/Client` but use
  Windows-oriented packages.
- `System.Speech`, DirectInput, WMI, PowerShell `Format-Volume`, Windows UAC,
  registry probing, and Windows path defaults are not Mac-portable.

**Likely files:**
- `shared/FreeAiSsd.Shared.csproj`
- `shared/Client/*`
- `shared/SystemResources.cs`
- `shared/SystemCompatibility.cs`
- tests around platform-neutral build boundaries

**Do not change:**
- Feature behavior.
- Package graph without a clear migration plan.

**Acceptance criteria:**
- A concrete split plan exists for platform-neutral core and platform adapters.
- Guardrail tests or build checks prevent new WPF/Windows-only dependencies in
  the future core.

**Tests:** `dotnet test tests/FreeAiSsd.Tests.csproj --filter
MacPlatformBoundaryTests --verbosity normal`; full suite recommended before PR
merge when time permits.

---

### MAC3 - Introduce platform-neutral Runner core

**Status:** done 2026-05-05
**Scope:** service extraction
**Risk:** Medium
**Goal:** Create a reusable home for Runner business logic currently tied to
  the WPF `runner/` project.

**Outcome:** Added `runner-core/FreeAiSsd.RunnerCore.csproj`, a plain `net8.0`
project for platform-neutral Runner business logic. The Windows WPF Runner now
references this project for chat, document operations, model management, local
API endpoint logic, and core service contracts. Windows-specific process,
voice, HOTAS/PTT, and DCS import implementations remain in `runner/`.

**Extracted services/contracts:**
- `ChatService`
- `DocumentOperationsService`
- `RunnerLocalApiService`
- `ModelManagementService`
- platform-neutral Ollama lifecycle, STT, TTS, and system-resource contracts

**Files changed:**
- `runner-core/FreeAiSsd.RunnerCore.csproj`
- `runner-core/Services/*`
- `runner/Services/WindowsSystemResourceProbe.cs`
- `runner/FreeAiSsd.Runner.csproj`
- `tests/FreeAiSsd.Tests.csproj`
- `tests/MacPlatformBoundaryTests.cs`
- `FreeAiSsd.sln`

**Do not change:**
- WPF UI behavior.
- Existing Windows Runner public workflows.

**Acceptance criteria:**
- Windows Runner still works through the extracted service boundary.
- Core can build without WPF.
- Existing `ChatService` and API tests still pass.
- Core stays compatible with the Apple Silicon/macOS 11+ baseline; Mac-specific
  implementation remains in host/adapters.

**Tests:**
- Existing chat/RAG/API tests.
- New construction tests proving core services do not need WPF.

---

### MAC4 - macOS Ollama lifecycle + runtime trust gate

**Status:** done 2026-05-05
**Scope:** platform adapter
**Risk:** Medium
**Goal:** Start and stop `mac/tools/ollama/ollama` through shared/core logic
  with the same security posture as Windows.

**Outcome:** PR #177, merge commit `648fcd9`. Generalized
`OllamaPackageTrustPolicy` so `DefaultMacPackage` (pinned to Ollama v0.5.7,
matching `DefaultWindowsPackage`) is a first-class peer. Added
`GetMacTrustAttestationPath`, `ValidateMacExecutionAttestation`, and
`WriteMacTrustAttestation`, refactored the Windows path to share a single
`ValidateExecutionAttestationCore` helper. Added pure-managed
`MachOArchInspector` so the Apple Silicon (arm64) slice check runs during
Windows-side staging without `lipo`, surfaced as
`OllamaPackageTrustFailureReason.Arm64SliceMissing`. New
`MacOllamaLifecycleService` (`runner-core/`, plain `net8.0`) implements
`IOllamaLifecycleService`: trust-gate, loopback bind, `OLLAMA_MODELS`,
argument-array `serve` launch, stdout/stderr/exit wiring.
`ArtifactStagingService.StageMacOllamaAsync` now goes through
`MacOllamaStagingPipeline` (verify SHA-256 + arm64 + write attestation;
scrub partial dir on failure). `tools/FreeAiSsd.PrereqFetch` repinned to
`DefaultMacPackage.Url` so CI, the bundled zip, and the runtime gate stay
in lockstep. Swift `mac-runner` re-checks the on-SSD attestation at every
launch and refuses on missing / malformed / URL-mismatched / SHA-mismatched
records.

**Files changed:**
- `shared/OllamaPackageTrustPolicy.cs` (Mac peer + shared validator core)
- `shared/MachOArchInspector.cs` (new)
- `shared/MacOllamaStagingPipeline.cs` (new)
- `shared/Prereqs/MacToolCatalog.cs` (pinned URL aligned with policy)
- `runner-core/Services/MacOllamaLifecycleService.cs` (new)
- `prep-app/Services/ArtifactStagingService.cs` (verify-then-attest)
- `tools/FreeAiSsd.PrereqFetch/Program.cs` (pinned to v0.5.7)
- `mac-runner/Sources/main.swift` (Swift trust gate)
- `tests/MacOllamaTrustPolicyTests.cs`,
  `tests/MachOArchInspectorTests.cs`,
  `tests/MacOllamaLifecycleServiceTests.cs`,
  `tests/MacOllamaStagingPipelineTests.cs`,
  `tests/MachOFixtures.cs` (new)

**Validation:**
- CI `windows-build` passed (full .NET test suite including new Mac suites).
- CI `mac-runner-build` passed (`swiftc` build).
- `MacPlatformBoundaryTests` still passes; `runner-core` remains plain
  `net8.0`, non-WPF, non-Windows-targeted.

**Manual-smoke gaps (deferred to a real Mac):**
- Real-Mac launch with a tampered or deleted attestation refuses cleanly.
- Real-Mac launch with a clean attestation produced by Windows PrepApp
  starts `ollama serve` cleanly.
- Staging an x86_64-only Mac payload (synthetic) refuses with
  `Arm64SliceMissing` instead of producing a broken drive.

**Tests added:**
- Mac trust validation: missing, malformed JSON, wrong URL, wrong digest,
  arm64 slice missing, missing binary, happy path.
- Mach-O parser: thin (32 + 64) LE, fat universal with arm64+x86_64, fat
  universal x86_64-only, fat 64 arm64-only, non-Mach-O file, Java class
  file (shares `0xCAFEBABE` magic), missing file.
- `MacOllamaLifecycleService`: path resolution, refusal when binary missing,
  refusal when attestation missing, env var setup via `BuildStartInfo`,
  loopback-only bind invariant, Mac trust does not pass on a Windows-only
  attestation.
- `MacOllamaStagingPipeline`: happy path writes attestation, refuses on
  hash mismatch, refuses on missing-arm64 binary, refuses on missing binary,
  no attestation written on any failure mode.

---

### MAC5 - macOS encrypted config unlock/save

**Status:** done 2026-05-05
**Scope:** Mac runtime integration
**Risk:** Medium
**Goal:** Bring encrypted-config unlock/save into the Mac runtime path so
  encrypted SSDs prepped on Windows are usable on macOS (and Mac-saved
  blobs roundtrip back to Windows).

**Outcome:** Native Swift port of `SsdEncryption` shipped at
`mac-runner/Sources/SsdEncryption.swift`. PBKDF2-HMAC-SHA256 via
CommonCrypto, AES-256-GCM via CryptoKit, two-file atomic commit
(`portable-config.encrypted.json` + `encryption-state.json`) with rollback
on state-rename failure, plaintext-migration mirror of
`TryMigratePlaintextAsync`. `mac-runner/Sources/main.swift` replaces the
"mac unlock not supported yet" short-circuit with an unlock sheet, holds an
`UnlockMaterial` while the session is unlocked, and zeroes the derived key
on manual lock, app background (`willResignActiveNotification`), and app
termination (`willTerminateNotification`).

**Cross-language format pin:** `tests/Fixtures/MacEncryptedConfig/csharp-encrypted/`
holds a Swift-produced encrypted blob the C# `MacEncryptedConfigCrossLanguageTests`
round-trip via `SsdEncryption.TryUnlockPortableConfig` on Windows CI. The
same fixture is asserted by the Swift test binary on Mac CI. Both sides
also assert the JSON key shape so a silent C# `JsonNamingPolicy` change
fails Windows CI immediately.

**Files changed:**
- `mac-runner/Sources/SsdEncryption.swift` (new — full PBKDF2 + AES-GCM port)
- `mac-runner/Sources/main.swift` (unlock sheet, lock-on-exit, save action)
- `mac-runner/Tests/SsdEncryptionTests.swift` (new — Swift test runner +
  `write-fixture` subcommand)
- `tests/MacEncryptedConfigCrossLanguageTests.cs` (new)
- `tests/Fixtures/MacEncryptedConfig/csharp-encrypted/` (new — committed
  cross-language fixture + README)
- `.github/workflows/build.yml` (mac-runner-build now compiles and runs the
  Swift test binary before Runner.app)
- `README.md`, `docs/QUICKSTART.txt` (drop "no Mac unlock" caveat)
- `agent_docs/mac_platform_dependency_audit.md` (record the deliberate
  Swift-encryption waiver)
- `agent_docs/project_decisions.md` (new dated entry —
  "MAC5 native Swift encryption: deliberate format duplication")

**Validation:**
- Swift test binary: 15 tests pass locally (PBKDF2 RFC vector, AES-GCM
  roundtrip, wrong password, tampered ciphertext, missing metadata, save
  preserves unknown fields, state file fields stay correct, plaintext
  migration branch A and B, key zeroize, cross-language fixture decrypt).
- C# `MacEncryptedConfigCrossLanguageTests` covers Swift→C# direction +
  reverse-direction format-pin assertion. Validated on Windows CI before
  merge (no local dotnet available during MAC5 development).

**Manual-smoke gaps (deferred to a real Mac):**
- Encrypt an SSD via Windows PrepApp; mount on Mac; unlock via the Swift
  UI; verify start/stop and selected-model roundtrip.
- Mount the same drive back on Windows; verify the Windows runner still
  unlocks it cleanly after a Mac-side save.
- Force a mid-save crash between blob and state writes (e.g., kill -9
  during save); verify the next launch finds a consistent blob+state pair
  via the `.bak` rollback path.

**Tests:**
- Swift unit tests as above.
- `MacEncryptedConfigCrossLanguageTests` — Swift fixture decrypts in C#,
  C# blob has Swift-compatible field shape, wrong password rejected.
- Existing `SsdEncryptionTests` and `ConfigStoreTests` unchanged and still
  pass (Windows behavior is untouched).

---

### MAC6 - Mac local API host, Companion compatibility, and X4 web UI surface

**Status:** done 2026-05-06
**Scope:** Mac host service
**Risk:** Medium
**Goal:** Run the Runner API on macOS for health, models, non-streaming chat,
  and streaming chat. Mac Runner is the cross-platform composition target:
  the Windows Companion connects to it over LAN, and X4's web chat UI is
  served from the same Mac Kestrel without a separate Mac UI track.

**Outcome:** PR #181 (`3557f9c`) added `mac-runner-host/`, a self-contained
net8.0 osx-arm64 sidecar spawned by the Swift runner. The sidecar hosts the
same `RunnerLocalApiService` used by Windows and receives the unlocked
PortableConfig over stdin so Mac encrypted-config IO remains Swift-owned.
Swift Network Mode starts/stops the sidecar with app lifecycle events. Mac CI
publishes the host, runs a smoke against `/api/health` and `/api/chat`, and
bundles the host into `Runner.app/Contents/Resources/runner-host/`. RunnerCore
now wires static-file middleware so future X4 assets under
`runner-core/wwwroot/chat/` are served by both Windows and Mac hosts.

**Deferred gaps:**
- Real-Mac Windows Companion -> Mac Runner LAN smoke with Bearer auth,
  `/api/health`, and `/api/chat`.
- Network Mode toggle with real Ollama serving a real model end-to-end.
- RAG-backed chat and citations; this is MAC7.

**Tests:**
- Mac CI sidecar smoke (spawn binary, handshake, `/api/health`, `/api/chat`,
  clean shutdown).
- RunnerLocalApi static-file tests.
- PR #181 follow-up branch adds regressions for disabled Network Mode startup
  and SSD-root `wwwroot` shadowing.

---

### MAC7 - RAG parity

**Status:** done 2026-05-06
**Scope:** document-grounded chat
**Risk:** High
**Goal:** Mac chat uses the same RAG pipeline as Windows: embeddings, vector
  search, prompt packing, and citations.

**Outcome:** MAC7 routes the Swift Mac chat UI through the MAC6
`mac-runner-host` sidecar instead of direct Ollama `/api/generate`, so normal
Mac chat uses `RunnerLocalApiService` + `ChatService` and honors an already
prepared `ActiveDocumentLibraryId`. The sidecar path returns sources and
`usedRagContext` for `/api/chat` and `/api/chat/stream`; `/api/chat` now also
returns a `ragWarning` field when retrieval fails, while preserving the
existing `X-RAG-Status` header. Swift displays returned sources and concise
RAG warnings. MAC8 remains the owner for Mac-side library CRUD, ingestion,
folder sweep, and rebuild UI.

**Likely files:**
- `shared/Documents/*`
- extracted `ChatService`
- Mac host/UI surfaces

**Acceptance criteria:**
- Active document library is honored on Mac.
- Responses include citations/sources when context is used.
- Embedding model mismatch is surfaced clearly.

**Tests:**
- RAG pipeline integration on Mac-compatible host.
- Citation/source display test.
- Embedding model missing/mismatch tests.

**Implemented coverage:**
- `MacRunnerHostRagParityTests` seeds a temporary SSD library, runs the Mac
  host's real DI wiring against a deterministic fake Ollama endpoint, and
  verifies `/api/chat`, `/api/chat/stream`, citations/sources, and embedding
  dimension mismatch warning behavior.

---

### MAC8 - Mac document management

**Status:** done 2026-05-06
**Scope:** library CRUD + ingestion surface (runner-core API + Mac UI)
**Risk:** High
**Goal:** Mac users can create/select libraries, add files/folders, sweep,
  rebuild, and remove files. Implement once in `runner-core` so the same
  endpoints serve Windows Companion / RunnerCli later. Supersedes the
  originally narrower `R1 Stage 2` plan in `project_backlog.md`.

**Outcome:** PR #185, merge commit `62d6d1d`. Eight `/api/library/*`
endpoints added to `RunnerLocalApiService` (auth-gated, multipart upload +
NDJSON progress) -- list / create / set-active / upload / delete /
add-watched-folder / sweep / rebuild. Documents UI added to
`mac-runner/Sources/main.swift` driving them through the `mac-runner-host`
sidecar (NSOpenPanel for files & folders, Create sheet, Sweep / Rebuild /
Remove buttons, library file list). Sidecar config-save split via new
`NoOpConfigStore` preserves the MAC5/MAC6 plaintext-config invariant:
mutating endpoints return updated `activeLibraryId` so Swift persists via
`SsdEncryption.swift`. Library manifests + watched folders + chunk index
stay sidecar-owned (on-SSD JSON / SQLite via `DocumentLibraryManager`),
never plaintext-config-adjacent. Two new test classes added:
`RunnerLocalApiLibraryTests` (HTTP-layer tests against real DI with fake
Ollama), and `MacRunnerHostLibraryTests` (Mac sidecar full DI with
end-to-end create -> upload -> chat-with-citations). All 526 tests green.

**Design notes worth preserving:**
- Upload semantics for ingest (multipart) chosen over local-path body so
  the same endpoint serves Mac (uploading from local disk) and a future
  Windows Companion (uploading from a remote PC). Windows' existing
  `DocumentIngestor` already copies user-picked files into the library's
  managed `files/` directory -- the multipart step maps cleanly onto
  behavior that already exists.
- Windows WPF Runner UI keeps calling `IDocumentOperationsService`
  in-process; the HTTP layer is a thin shell over the same service.
  Migrating Windows UI to loopback HTTP was explicitly not in scope --
  wasted bytes on big ingests for no MAC8 value.
- Long-running endpoints stream NDJSON frames (`start` -> many `progress`
  / `file-rejected` -> `complete` / `error`) using a sync-queue + drain
  pattern (see `project_decisions.md` 2026-05-06 entry on the pump
  pattern).
- Companion UI for library control is deferred. Remote-folder UX is the
  unanswered question, not the API shape.

**Files changed:**
- `runner-core/Services/RunnerLocalApiService.cs` -- new endpoint group +
  DTOs + `WriteNdjsonAsync` camelCase fix
- `mac-runner-host/NoOpConfigStore.cs` (new) +
  `mac-runner-host/HostLifetime.cs` DI wiring
- `mac-runner/Sources/main.swift` -- Documents UI, library API client
- `runner/App.xaml.cs` -- pass `IDocumentOperationsService` +
  `DocumentLibraryManager` to `RunnerLocalApiService` constructor
- `tests/RunnerLocalApiLibraryTests.cs`,
  `tests/MacRunnerHostLibraryTests.cs` (both new)
- `agent_docs/project_backlog.md` -- R1 Stage 2 superseded
- `agent_docs/mac_project_backlog.md` -- MAC8 status

**Validation:**
- CI `windows-build` + `mac-runner-build` both green on `c796ba7`
  (final commit before merge). 526 tests pass.
- Local Swift compile green via `swiftc` against
  `arm64-apple-macos11.0`.
- `dotnet build` / `dotnet test` not run locally (no `dotnet` on the
  Mac dev machine); CI was the only validation path. Three CI
  iterations were needed to surface and fix two real bugs (camelCase
  serialization on nested records; ASP.NET catch-all `%2F` decoding).

**Manual-smoke gaps (deferred to a real Mac):**
- Network Mode on -> Documents UI populates -> Create library -> Add
  Files (TXT) -> ingest completes -> Send chat -> returns sources from
  the uploaded file. End-to-end is covered by
  `MacRunnerHostLibraryTests` against the real Mac host DI, but not
  against an actual on-Mac launch with NSOpenPanel + a user-picked
  SSD.

**Tests added:**
- `RunnerLocalApiLibraryTests` -- direct HTTP coverage: list (empty,
  populated), create (happy, duplicate -> 409, blank -> 400), set
  active (happy, unknown -> 404, null clears), upload (TXT happy +
  NDJSON shape, unsupported extension rejected, oversized rejected,
  unknown library -> 404), watched folder (happy, nonexistent path
  -> 400), delete (happy, traversal -> 400), sweep, rebuild, auth
  required when `NetworkRequireApiKey`.
- `MacRunnerHostLibraryTests` -- full Mac sidecar DI:
  create -> upload -> chat-with-citations end-to-end, set-active
  round-trip, clear-active.

---

### MAC9 - Mac UI strategy decision

**Status:** done 2026-05-06
**Scope:** architecture decision
**Risk:** High
**Goal:** Re-check the long-term UI path after the Mac host/core has proven the
  service boundary. MAC1 sets Swift/SwiftUI as the current default, so this item
  should only change direction if the thin native UI blocks parity or causes
  real duplicated business logic.

**Outcome:** Locked in option 1 -- Swift thin-UI over local
`mac-runner-host` .NET sidecar. Avalonia replacement and CLI-first-longer
both rejected. Decision and exit-ramp criteria recorded in
`project_decisions.md` (2026-05-06 entry: "MAC9: Swift thin-UI over
.NET sidecar locked in as long-term Mac UI"). MAC4-MAC8 evidence:
~1,730 lines of Swift, zero business logic in Swift (all RAG / chat /
library / API logic in `runner-core/` net8.0), exactly one approved
business-logic duplication (`SsdEncryption.swift`, waived 2026-05-05),
zero parity blockers caused by the UI architecture. Re-open MAC9 only
if Swift starts duplicating non-trivial business logic, WPF and Swift
drift apart faster than parity work can keep up, Apple lifecycle /
signing complexity exceeds Avalonia's, or a non-Apple platform Runner
is added.

**Acceptance criteria (met):**
- Decision recorded in `project_decisions.md`.
- The chosen path does not duplicate RAG / encryption / network logic
  (only `SsdEncryption.swift` is duplicated, per the MAC5 waiver).

**Tests:** Not applicable.

---

### MAC10 - Mac packaging hardening

**Status:** planned
**Scope:** CI/package
**Risk:** Medium
**Goal:** Make the macOS beta artifact predictable and testable.

**Likely files:**
- `.github/workflows/build.yml`
- app bundle metadata / Info.plist generation
- release assembly scripts

**Acceptance criteria:**
- Build strategy is arm64-only for Free-AI-SSD app artifacts.
- macOS deployment target and `LSMinimumSystemVersion` match the macOS 11+
  Apple Silicon baseline, or a later decision records why the floor changed.
- App bundle contains or can locate required host pieces.
- External SSD launch path is tested.
- Logs and failure messages are useful.

**Tests:**
- CI artifact validation.
- Manual clean-Mac launch smoke.

---

### MAC10a - PrepApp OS compatibility filesystem selector

**Status:** done (PR pending; resolves on merge)
**Scope:** PrepApp UX + format defaults
**Risk:** Medium
**Goal:** Let the user choose target OS compatibility during Windows PrepApp
  drive preparation, then preselect the filesystem that matches the supported
  Mac baseline.

**Resolution:** Filesystem derived from the already-existing `PrepTargets`
selection (Windows / Mac checkboxes) rather than a new dropdown. Mapping:
Windows-only → `NTFS`; anything including Mac → `exFAT`. Chosen filesystem
shown in `EraseConfirmDialog` before the destructive call. APFS still
deferred per MAC1 until MAC17. See 2026-05-06 decision entry "MAC10a:
filesystem derived from existing PrepTargets, not a new selector".

**Baseline from MAC1:**
- Windows only -> NTFS.
- Windows + macOS -> exFAT.
- macOS only -> exFAT when staged from Windows; APFS remains deferred until a
  Mac-native prep/staging workflow exists.

**Likely files:**
- `prep-app/MainWindow.xaml`
- `shared/ViewModels/PrepViewModel.cs`
- `shared/Services/IDriveService.cs`
- `prep-app/Services/DriveService.cs`
- tests around filesystem default selection and validated format arguments

**Acceptance criteria:**
- Compatibility choice is visible before the destructive format/prepare action.
- Filesystem preselection follows the MAC1 baseline.
- Existing erase confirmation remains in place.
- Drive letter and process-launch security invariants are unchanged.

**Tests:**
- ViewModel tests for compatibility -> filesystem mapping.
- Existing/new format argument tests continue to use `ProcessRunner.ArgumentList`.

---

### MAC10b - Mac app icon and bundle metadata polish

**Status:** done (PR pending; resolves on merge)
**Scope:** Runner.app bundle visual identity + Windows WPF parity
**Risk:** Low
**Goal:** Replace the default macOS placeholder icon with a Free-AI-SSD app
icon so the bundle looks shipped, not built-from-CI. Tighten Info.plist
metadata while we're there.

**Resolution:** Single shared icon (`assets/icon/AppIcon.{icns,ico,png}`)
applies to all four hosts: Mac `Runner.app`, Windows Runner, Windows
PrepApp, Windows Companion. See 2026-05-06 decision entry "MAC10b: single
shared app icon across Mac Runner and all WPF hosts" for the parity
rationale.

**Asset pipeline:**
- `assets/icon/IconRenderer.swift` — Core Graphics renderer that draws the
  canonical Free-AI-SSD glyph (hexagonal chip + glowing core on a Big
  Sur squircle, indigo→violet→magenta gradient with cyan halo) at any
  size.
- `assets/icon/ico-builder.py` — assembles a PNG-embedded `.ico` from
  rasterized PNGs (no ImageMagick dependency on macOS).
- `assets/icon/build-icons.sh` — orchestrator: renders all sizes, runs
  `iconutil` for `.icns`, runs `ico-builder.py` for `.ico`, drops a
  `1024.png` master.
- Both binaries are committed so CI doesn't need to re-render on every
  build and MSBuild can reference the `.ico` directly.

**Info.plist polish bundled in:**
- `CFBundleName` + new `CFBundleDisplayName` = "Free AI SSD" (≤15 chars).
- `CFBundleVersion` = "1" (was missing — required by macOS app deployment).
- `CFBundleIconFile` = "AppIcon" (new).
- `LSApplicationCategoryType` = `public.app-category.utilities`.
- `LSRequiresNativeExecution` = true (arm64-only per MAC1).
- `NSHighResolutionCapable` = true.
- `NSHumanReadableCopyright` = "Copyright (c) 2026 Free-AI-SSD project".
- `CFBundleShortVersionString` deliberately left at "1.0" — Mac version
  tracking firms up at MAC11.

**Files changed:**
- `assets/icon/IconRenderer.swift`, `ico-builder.py`, `build-icons.sh` (new)
- `assets/icon/AppIcon.icns`, `AppIcon.ico`, `AppIcon.png` (new binaries)
- `.github/workflows/build.yml` — copies `.icns` into bundle, rewrites
  Info.plist heredoc, adds `Verify Runner.app bundle layout` step
- `runner/FreeAiSsd.Runner.csproj`, `prep-app/FreeAiSsd.PrepApp.csproj`,
  `companion/FreeAiSsd.Companion.csproj` — `<ApplicationIcon>` added

**Validation:**
- Local: renderer round-trips through `iconutil` (10 size buckets present)
  and the `.ico` opens with 6 PNG-embedded sizes (16/32/48/64/128/256).
- CI: new `Verify Runner.app bundle layout` step asserts `AppIcon.icns`
  is in `Contents/Resources/`, lints the Info.plist with `plutil`,
  confirms `CFBundleIconFile=AppIcon`, and re-extracts the iconset to
  verify all 10 sizes are present.

**Manual-smoke gaps (deferred to a real Mac):**
- Visual smoke: `Runner.app` shows the Free-AI-SSD icon in Finder, the
  Dock, and command-tab on a clean Mac.
- Visual smoke: WPF apps show the icon in the title bar and taskbar on
  Windows.

**Sequencing note:** still natural to bundle with MAC11 (signing +
notarization) since both touch Info.plist + bundle layout.

---

### MAC11 - Signing and notarization

**Status:** planned
**Scope:** release hardening
**Risk:** Medium
**Goal:** Enable supported Mac distribution without Gatekeeper workarounds.

**Dependencies:** MAC10 and stable app behavior.

**Likely files:**
- `.github/workflows/build.yml`
- entitlements file if needed
- release docs
- Developer ID setup / notarization guide. A detailed local draft exists at
  `/Users/stephenelswick/Desktop/Free-AI-SSD-macOS-signing-notarization-guide.md`;
  fold the durable parts into repo docs or release docs during this item.

**Acceptance criteria:**
- Signed and notarized `Runner.app`.
- Clean Mac launch works without right-click workaround.
- Quarantine/Gatekeeper behavior documented.
- CI/local setup steps are documented well enough to repeat with a fresh
  Apple Developer account or rotated credentials.

**Tests:**
- Notarization CI run.
- Manual clean-Mac launch smoke.

---

### MAC12 - Voice/TTS parity

**Status:** planned
**Scope:** audio platform adapters
**Risk:** High
**Goal:** Add macOS microphone, STT, and TTS support after chat/RAG parity.

**Likely files:**
- `shared/Client/*` abstractions
- platform-specific audio capture adapter
- TTS adapter
- Whisper runtime validation

**Acceptance criteria:**
- Microphone permission failures are clear.
- Recording produces Whisper-compatible PCM/WAV.
- TTS works through a Mac-native engine or a deliberate Piper strategy.

**Tests:**
- Permission-denied behavior.
- Audio conversion tests.
- TTS disabled/default mode tests.

---

### MAC13 - HOTAS/PTT support or deliberate deferral

**Status:** planned
**Scope:** HID/PTT decision
**Risk:** High
**Goal:** Either implement a real macOS HID/GameController PTT adapter or
  explicitly defer HOTAS/PTT for Mac.

**Dependencies:** MAC12 unless deliberately deferred.

**Acceptance criteria:**
- If implemented: tested device enumeration and button edge detection.
- If deferred: docs and feature matrix say so plainly.

**Tests:**
- Fake HID adapter tests.
- Manual known-device smoke if implemented.

---

### MAC14 - DCS import on Mac

**Status:** planned
**Scope:** optional feature parity
**Risk:** Medium
**Goal:** Decide whether DCS bindings import is meaningful on Mac. If yes,
  support manual path first.

**Acceptance criteria:**
- No claim of auto-detect unless a real Mac DCS path exists.
- Shared parser is reused.

**Tests:**
- Manual-path scanner/parser tests.

---

### MAC16 - Extract PrepApp core to `prep-core/`

**Status:** done 2026-05-06 (PR #191, merged `6e2eb39`)
**Scope:** service extraction (mirrors MAC3 pattern for PrepApp)
**Risk:** Low
**Goal:** Move platform-neutral PrepApp business logic out of the WPF
  `prep-app/` host into a reusable `prep-core/FreeAiSsd.PrepCore.csproj`,
  so a future macOS PrepApp can share manifest, staging, prereq, and
  encrypted-config logic without duplicating the Windows code.

**Likely files:**
- New `prep-core/FreeAiSsd.PrepCore.csproj` (plain `net8.0`, no WPF, no
  Windows-only packages).
- `prep-app/Services/ArtifactStagingService.cs` -> moves into `prep-core/`.
- Manifest, prereq catalog, starter-model catalog, and SHA-256/URL-allowlist
  download logic relocated to `prep-core/` where they aren't already shared.
- `prep-app/MainWindow.xaml.cs` Windows orchestration stays in the WPF host.
- `tests/MacPlatformBoundaryTests.cs` extended to guard `prep-core/`.

**Adapters that stay platform-specific:**
- `IDriveService` â€” drive enumeration + format. Windows already uses WMI /
  PowerShell `Format-Volume`; macOS will use `diskutil` (added in MAC17).
- UI hosts: WPF on Windows, SwiftUI on Mac.

**Do not change:**
- Existing Windows PrepApp behavior or workflow.
- Encryption format or `SsdEncryption` / `ConfigStore` shape.
- SHA-256 + URL allowlist guarantees on prereq downloads.
- `ProcessRunner.ArgumentList` invariant for any process launches.

**Acceptance criteria:**
- `prep-core/` builds plain `net8.0` with no WPF and no Windows-only package
  references.
- Windows PrepApp drives the same staging/format/finalize flow via
  `prep-core/` services.
- Existing PrepApp tests still pass.
- Guardrail tests block new Windows-only packages from entering
  `prep-core/`.

**Tests:**
- Existing PrepApp staging/finalize tests.
- New construction tests proving `prep-core/` doesn't need WPF.
- `MacPlatformBoundaryTests` extended to cover `prep-core/`.

---

### MAC17 - macOS PrepApp MVP (exFAT)

**Status:** done 2026-05-06 (PR #193 merged at `b6e7089`; follow-ups in MAC17a)
**Scope:** new SwiftUI host + `prep-core/` consumer
**Risk:** High
**Dependencies:** MAC5 (encrypted config unlock/save on Mac), MAC16
  (`prep-core/` extraction).
**Goal:** Ship a Mac-native PrepApp so a Mac-only user can download, prep,
  and run Free-AI-SSD without owning a Windows machine. Output drives must
  be byte-for-byte compatible with Windows-prepped drives so the encrypted
  config and staged artifacts work on either OS.

**Targets supported in MVP:**
- Mac-only -> exFAT.
- Cross-platform (Windows + Mac) -> exFAT.

**Out of scope for MVP (and possibly permanently):**
- APFS targets. Dropped per the 2026-05-05 cross-platform prep parity
  decision; exFAT covers the Mac-only path. Revisit only if exFAT proves
  inadequate during validation.
- NTFS targets from a Mac source. macOS cannot natively format NTFS;
  users wanting NTFS-only drives are directed to Windows PrepApp in docs.

**Likely files:**
- New `mac-prep-app/` (SwiftUI app analogous to `mac-runner/`).
- macOS `IDriveService` implementation using `diskutil list` and
  `diskutil eraseDisk` with explicit argument lists (no shell string
  concat).
- `shared/Prereqs/MacToolCatalog.cs` consumed for Mac-side prereqs.
- `prep-core/` consumed for manifest, staging, encrypted config write.
- `mac-prep-app/` packaging in `.github/workflows/build.yml` once MAC10
  hardening lands.

**Do not change:**
- Encryption format. Encrypted config written on Mac must unlock on
  Windows and vice versa.
- Manifest / staging layout.
- Security invariants: SHA-256 + URL allowlist on prereqs, explicit
  argument lists on `diskutil`, destructive-action confirmation.

**Acceptance criteria:**
- User can pick a target external drive, choose Mac-only vs cross-platform,
  and see exFAT preselected for both.
- Format runs through `diskutil` with explicit argument lists; destructive
  action requires explicit confirmation matching the Windows PrepApp UX
  posture.
- Artifact staging copies models, `mac/Runner.app`, `mac/tools/ollama`,
  prereqs, and starter-model catalog via `prep-core/`.
- Encrypted config written on Mac unlocks on Windows; encrypted config
  written on Windows unlocks on Mac (cross-platform roundtrip).
- Logs and error severity match the Windows PrepApp.
- Apple Silicon-only; macOS 11+ minimum (per MAC1 baseline).

**Tests:**
- Cross-platform encrypted config roundtrip (Mac-prepped drive opens on
  Windows; Windows-prepped drive opens on Mac).
- `IDriveService` fakes for `diskutil` argument validation.
- Manifest/staging tests covered by `prep-core/`.
- Manual dual-OS smoke: prep on Mac -> run Windows Runner; prep on
  Windows -> run Mac Runner.

---

### MAC17a - PrepApp follow-ups from PR #193 review

**Status:** done 2026-05-07 (PR #195 merged at `eba669a`). 6/7
  review items bundled (#1, #2, #3, #4, #6, #7); #5 deferred to
  MAC17b as a structural refactor that benefits from landing after
  the threading cluster.
**Scope:** mac-prep-app SwiftUI host correctness + UI responsiveness
**Risk:** Low (one latent crash on cancel, four UI hitches, two structural cleanups)
**Dependencies:** MAC17 (merged)
**Goal:** Address Gemini code review items from PR #193 that were
  knowingly deferred at MVP merge — none blocked the MVP smoke but
  several materially improve real-Mac UX, and one (#1 continuation
  leak) is a latent crash on prep-flow cancel + retry.

**Detail file:** see `agent_docs/mac17_followup_notes.md` for
per-issue file:line references, current code excerpts, proposed
fixes, and bundling/sequencing recommendations.

**Issue summary (high → medium):**
- #1 (HIGH) `PrepHostController.send` continuation leak on cancel/timeout — only correctness item.
- #2 (HIGH) `PrepViewModel.formatSelected` blocks `@MainActor` on disk format.
- #3 (HIGH) `PrepViewModel.writeEncryptionAndProceed` blocks `@MainActor` on PBKDF2.
- #4 (MED)  `PrepHostController.shutdown` busy-waits on `@MainActor`.
- #5 (MED)  SSD layout hardcoded in `PrepViewModel` — duplicates `SsdLayout.cs`.
- #6 (MED)  Encryption toggle UX mismatch — interactive control causes hard failure.
- #7 (MED)  `PrepViewModel.refreshCandidates` blocks `@MainActor` on diskutil list.

**Suggested split:** ship #1 + #6 + the background-threading cluster
(#2/#3/#4/#7) together. Defer #5 to MAC17b so the structural refactor
isn't fighting an in-flight threading change.

**Do not change:**
- Encrypted-config format / scheme name / fixture cross-language pin.
- Destructive-erase NSAlert posture.
- MAC5 plaintext invariant.

**Acceptance criteria:**
- All seven issues resolved or explicitly punted to MAC17b with
  `mac17_followup_notes.md` updated to reflect the punt.
- New unit tests for #1's cancel-then-retry path and #5's
  ensure-structure delegation (if landed in this round).
- CI green.

---

### MAC17b - PrepApp Issue #5: replace hardcoded SSD layout with sidecar delegation

**Status:** done 2026-05-07 (PR #198 merged `224f4b5`)
**Scope:** mac-prep-app SwiftUI host + mac-prep-host sidecar protocol
**Risk:** Low (single new sidecar command + one Swift call site
  swap; structural rather than behavioral)
**Dependencies:** MAC17a (merged) — threading cluster needs to be
  in before this refactor lands so the diff is purely structural.

**Goal:** Replace the hardcoded `macSubdirs` list in
  `PrepViewModel.runStaging` (mac-prep-app/Sources/PrepViewModel.swift:198-ish)
  with a sidecar `ensure-structure` command that delegates to
  `shared/SsdLayout.cs`'s `EnsureStructure(_ssdRoot)`. Today the
  Swift list duplicates the C# tree shape; if the C# side adds a
  directory the Mac PrepApp silently ships drives missing it and
  downstream operations fail in subtle ways.

**Detail file:** see `agent_docs/mac17_followup_notes.md` Issue #5
  for per-line context.

**Implementation sketch:**
- Add `ensure-structure` to the `mac-prep-host/` stdin command set;
  delegate to `SsdLayout.EnsureStructure(_ssdRoot)` (prep-core /
  shared are already ProjectReferences).
- Swap `for sub in macSubdirs { ... }` for
  `_ = try await hostController.send("ensure-structure")`.
- Test seam: extend `MacPrepHostConstructionTests` (or add a new
  case) that runs `ensure-structure` against a fresh temp dir and
  asserts every directory `SsdLayout` declares actually exists.
  This pins drift between C# and Swift the way
  `DiskutilFormatCommandTests` does for the format argv shape.

**Do not change:**
- Encrypted-config format / scheme name / fixture cross-language pin.
- MAC5 plaintext invariant (sidecar still receives PortableConfig
  over stdin only).

**Acceptance criteria:**
- Hardcoded `macSubdirs` is gone from PrepViewModel.
- New host-side test pins SsdLayout's declared directories against
  the post-`ensure-structure` filesystem state.
- CI green.

---

### MAC18 - Cross-platform prep compatibility docs

**Status:** done 2026-05-07 (PR #200 merged at `5f4a8f3`)
**Scope:** docs / release matrix
**Risk:** Low
**Dependencies:** MAC17 ships first so the matrix isn't aspirational.
**Goal:** Document the source/target compatibility matrix so users know
  which OS to run prep from for which target. Make NTFS-only-from-Windows
  and APFS-only-from-Mac explicit OS limits, not project gaps.

**Outcome:** PR #200 merged at `5f4a8f3`. Pure docs (`README.md` +
`docs/QUICKSTART.txt` + `agent_docs/project_decisions.md`); no
runtime / CI / fixture changes. README gained a new
`Source/Target compatibility` subsection (anchor
`#sourcetarget-compatibility`), a `Cross-platform PrepApp`
feature row, parallel Mac walkthrough in Phase 1, and project
structure table updates for `runner-core/`, `prep-core/`,
`mac-prep-app/`, `mac-prep-host/`, `mac-runner-host/`. QUICKSTART
gained an ASCII matrix block + filesystem-from-target-choice
rewrite. project_decisions entry locks the matrix and bounds
scope (no Runner-side beta-framing rewrite — that's MAC15's job
after MAC11). Held the line on docs-matrix-only per user
direction. CI green on first run.

**Compatibility matrix to publish:**

| Source OS | Target | Filesystem | Supported |
|-----------|--------|------------|-----------|
| Windows | Windows-only | NTFS | yes |
| Windows | Cross-platform | exFAT | yes |
| Windows | Mac-only | exFAT | yes (APFS not available from Windows) |
| Mac | Mac-only | exFAT | yes (APFS dropped from supported targets) |
| Mac | Cross-platform | exFAT | yes |
| Mac | Windows-only | NTFS | not supported â€” use Windows PrepApp |

**Likely files:**
- `README.md`
- `docs/QUICKSTART.txt`
- release notes when MAC17 ships
- `agent_docs/project_decisions.md` cross-references the matrix

**Acceptance criteria:**
- README and QUICKSTART describe the matrix in user-facing language.
- Each unsupported cell links to the supported alternative (e.g., the
  Mac -> Windows-only NTFS row points users at Windows PrepApp).
- Encrypted config compatibility (Mac <-> Windows) is explicitly called
  out so users don't think prep is one-way.

**Tests:** Doc review.

---

### MAC21 - Mac PrepApp post-format mount discovery (DiskCandidate model split)

**Status:** done 2026-05-07 (PR forthcoming — branch
  `kninetimmy/mac21-post-format-mount-discovery`). Manual smoke
  on a real Mac + external SSD remains as the field validation;
  CI exercises the parser via fixture-driven tests.
**Scope:** small-medium — reshape `DiskCandidate` model + partition
  walk in `listExternalCandidates` + new test fixture + unit test.
**Risk:** Low — fix is isolated to `mac-prep-app/Sources/`; no
  shared-core or sidecar changes.
**Dependencies:** None. Top of the queue because without it Mac
  PrepApp can't complete a single end-to-end prep, blocking all
  Mac field testing.
**Goal:** A user who launches Mac PrepApp, picks an external SSD,
  and confirms the destructive erase reaches the staging step
  successfully — instead of hitting "Selected drive has no mount
  point after format." after a successful format.

**Driver:** v1.3.1 download test on 2026-05-07 (after the MAC19
  xattr unblock for the same session) reproduced the failure
  end-to-end:
- `diskutil eraseDisk` succeeds (log shows "Mounting disk",
  "Finished erase on disk", "Format complete.").
- `formatSelected()` (`PrepViewModel.swift:182`) calls
  `refreshCandidates()` to pick up the new mount.
- `listExternalCandidates` (`DiskutilDriveService.swift:63-112`)
  reads `MountPoint` only from the parent disk's
  `diskutil info -plist <parent>` output. After
  `diskutil eraseDisk` lays down a GPT partition table + exFAT
  volume, the new volume mounts on the **child partition** (e.g.,
  `disk4s2` at `/Volumes/FREEAI`), not the parent (`disk4`). The
  parent's `MountPoint` field stays empty.
- Refreshed `DiskCandidate` for `disk4` therefore has
  `mountPoint = nil`. `runStaging()` (`PrepViewModel.swift:195`)
  hits the `guard let mount = selectedCandidate?.mountPoint`,
  trips into `.failed(message: "Selected drive has no mount point
  after format.")`, and renders the "Something went wrong" modal
  with a Restart button.

  CI didn't catch this because `listExternalCandidates` runs against
  real `diskutil` output only — there's no recorded plist fixture in
  `tests/`. Lives in MAC17's "deferred manual smoke on a real Mac +
  external SSD" gap, which the user is now exercising.

**Fix (complete, no bandaid):**

Reshape `DiskCandidate` in `mac-prep-app/Sources/DiskutilDriveService.swift`
to track both the parent identifier (what `diskutil eraseDisk`
needs) and the mounted partition state (what `runStaging` needs):

```swift
struct DiskCandidate: Identifiable, Sendable {
    let identifier: String              // parent: "disk4" — fed to eraseDisk
    let displayName: String
    let mediaName: String?
    let totalSizeBytes: Int64
    let removable: Bool
    let mountedPartition: PartitionMount?   // nil until first mount
    var id: String { identifier }
    var mountPoint: URL? { mountedPartition?.mountPoint }
}

struct PartitionMount: Sendable {
    let identifier: String      // "disk4s2"
    let volumeName: String?
    let mountPoint: URL
    let sizeBytes: Int64?
}
```

Computed `mountPoint` keeps existing call sites working (`mainwindow.swift:192`
`drive.mountPoint?.path`, `PrepViewModel.swift:67`/`195`/`329`) without
churn while making the partition identity available to anything that
needs it later.

Update `listExternalCandidates` to walk `entry["Partitions"]` (the
partitions array already in `diskutil list -plist external` output)
and emit a `PartitionMount` from the first partition with a non-empty
`MountPoint`. Schema: each `Partitions[]` entry has
`DeviceIdentifier`, `Content`, `VolumeName`, `MountPoint`, `Size`.
Falls back to the parent's `MountPoint` if non-empty (whole-disk
format case — pre-format raw exFAT volumes), so the pre-format drive
selection step still surfaces a mount when the user plugged in an
already-formatted SSD.

**Test (new fixture-driven coverage):**

- New fixture `mac-prep-app/Tests/Fixtures/diskutil-list-partitioned.plist`:
  recorded `diskutil list -plist external` output for a partitioned
  exFAT disk (`disk4` parent + `disk4s1` EFI + `disk4s2` Microsoft
  Basic Data with mount). Capture from the user's actual disk via
  `diskutil list -plist external > diskutil-list-partitioned.plist`
  (sanitize serial numbers if any leak in).
- New fixture `…/diskutil-list-wholedisk.plist` for the rare whole-
  disk-format case (pre-format raw exFAT, no partition table).
- New unit test `PrepAppTests.swift` arm
  `ListExternalCandidates_PartitionedDisk_ReturnsChildPartitionMount`:
  feeds the partitioned plist through a `DiskutilDriveService` whose
  `runDiskutil` is stubbed to return the fixture bytes, asserts the
  returned `DiskCandidate.mountedPartition` carries the child
  identifier + mount URL, and that the parent identifier on
  `DiskCandidate.identifier` is preserved (so format calls still
  target the parent).
- Mirror test for the whole-disk fixture (asserts parent mount falls
  back when there are no partitions).
- Existing `DiskutilFormatCommandTests.cs` unaffected (format argv
  still uses the parent identifier).

**Affected files:**
- `mac-prep-app/Sources/DiskutilDriveService.swift` —
  `DiskCandidate` reshape, new `PartitionMount` struct, partition-
  walking in `listExternalCandidates`, computed `mountPoint`.
- `mac-prep-app/Sources/PrepViewModel.swift` — minor: nothing
  required if computed `mountPoint` keeps the existing API. Optional:
  log the partition identifier alongside the parent in
  `appendLog("Formatting … as exFAT")` so future field reports
  identify both halves.
- `mac-prep-app/Sources/main.swift:192` — uses `mountPoint` directly;
  unaffected by computed property.
- `mac-prep-app/Tests/Fixtures/` — new fixture plist files (new dir).
- `mac-prep-app/Tests/PrepAppTests.swift` — new fixture-driven cases.
- `mac-prep-app/build-tests.sh` (or wherever the test swiftc
  invocation lives) — extend the input list if `Fixtures/` needs
  bundling for the test runner.

**Cross-OS review pass:**
- **Mac surfaces:** `DiskutilDriveService.swift`, `PrepViewModel.swift`
  (incidental), `main.swift` (incidental, computed property).
- **Windows surfaces:** None. Windows PrepApp uses a separate
  `IDriveService` implementation backed by WMI / PowerShell
  `Format-Volume`; its candidate model is independent of the Mac one.
- **Decision:** Single-OS Mac fix. Justification: the Windows code
  path doesn't share data structures with the Mac one, and the bug
  is rooted in `diskutil`'s parent/partition split which has no
  Windows analog (Windows surfaces volumes directly).

**Out of scope:**
- Auto-eject on completion.
- Multi-volume / multi-partition handling beyond first-mounted.
  exFAT format yields a single mounted volume; if a user picks a
  pre-existing multi-partition disk, first-mounted is the right
  pick because the format will collapse it to one anyway.
- Whole-disk-format capability for prep itself (we always partition).
- Reshape of WPF `IDriveService` to match — Windows works.

**Acceptance criteria:**
- Plug in an external SSD, launch Mac PrepApp, pick the drive,
  confirm erase, format completes, staging step proceeds without
  the "no mount point" failure.
- New unit tests pass on CI (mac-prep-build job runs them).
- Format-then-stage path works for both: previously partitioned
  disk and whole-disk-format raw exFAT disk.
- No regression in pre-format drive selection — disks the user
  already mounted before opening PrepApp still surface their mount
  in the selection list.

**Tests:**
- New unit tests above.
- Manual smoke deferred to a real Mac + external SSD (the same
  loop the user just exercised — should now succeed end-to-end
  through staging into encryption setup).

---

### MAC22 - Mac sidecar manifest lookup walks ancestors

**Status:** planned 2026-05-07 (field-reported by user same session;
  caught immediately after MAC21 unblocked the format step in the
  v1.3.2 field test).
**Scope:** small — extend
  `prep-core/MacArtifactAvailability.EnumerateContentRoots` to
  walk parent directories + add unit tests.
**Risk:** Low — backward-compatible (Windows still hits the
  manifest on the original second candidate); all changes confined
  to a single static helper plus tests.
**Dependencies:** None. Top of the queue (immediate field-blocker)
  the moment MAC21 ships.
**Goal:** Mac PrepApp's `stage-runner` / `stage-ollama` /
  `stage-prereqs` sidecar arms succeed instead of failing with
  "macOS preparation is available in the Cross-platform Beta
  download." when the sidecar is run from inside the .app bundle.

**Driver:** v1.3.2 field test, immediately after stripping the
quarantine xattr to clear MAC19. Format succeeded (MAC21 fix
working), then `runStaging` hung at the staging step:

```
[stderr] Command failed ("stage-runner"): macOS preparation is
available in the Cross-platform Beta download.
```

Diagnosed at `prep-core/Services/ArtifactStagingService.cs:64`:

```csharp
var macAvailability = MacArtifactAvailability.Evaluate(AppContext.BaseDirectory);
```

`AppContext.BaseDirectory` resolves to:
- **Windows PrepApp.exe**: bundle root (`Free-AI-SSD-beta-crossplatform/`).
  `EnumerateContentRoots` yields `<root>` then `<root>/payload`; the
  manifest at `<root>/payload/mac/mac-artifacts.manifest.json` is
  found on the second candidate. ✓
- **Mac mac-prep-host sidecar**:
  `Free-AI-SSD-beta-crossplatform/payload/mac/PrepApp.app/Contents/Resources/prep-host/`.
  Both candidates miss; manifest is **5 levels up**, not down.
  Pre-MAC22 the lookup never walks up, so every Mac field-prep run
  reports "macOS preparation is available in the Cross-platform
  Beta download." even though the manifest is sitting right there
  at the bundle root. ✗

The bug had been latent since MAC17 shipped — manual smoke on a
real Mac + external SSD was the deferred validation that MAC21
finally unblocked, and MAC22 was the immediate next thing it caught.

**Fix:**

Extend `EnumerateContentRoots(string appDirectory)` in
`prep-core/MacArtifactAvailability.cs` to also walk a bounded
number of ancestors after the existing two candidates:

```csharp
DirectoryInfo? cursor;
try { cursor = new DirectoryInfo(appDirectory); }
catch { yield break; }

for (var i = 0; i < 6 && cursor?.Parent is not null; i++)
{
    cursor = cursor.Parent;
    yield return cursor.FullName;
    yield return Path.Combine(cursor.FullName, "payload");
}
```

Bounded depth of 6 is enough to escape
`PrepApp.app/Contents/Resources/prep-host/` (5 levels) plus a
margin. Backward-compatible with Windows (its original second
candidate still wins on the first iteration, before the loop runs).

**Test (new fixture-driven coverage):**

New `tests/MacArtifactAvailabilityTests.cs` with five cases:
1. Windows layout — `Evaluate(<bundleRoot>)` finds the manifest at
   `<bundleRoot>/payload/mac/mac-artifacts.manifest.json` (preserves
   the existing behavior the Windows PrepApp depends on).
2. Mac sidecar layout — `Evaluate(<bundleRoot>/payload/mac/PrepApp.app/Contents/Resources/prep-host/)`
   finds the same manifest by walking ancestors (the regression pin).
3. Manifest absent anywhere up the chain — returns Unavailable
   with the canonical missing-manifest message.
4. Manifest present but referenced artifact missing — returns
   Unavailable with the IncompleteManifestMessage.
5. Bounded-depth sanity — 8-level-deep sidecar path doesn't escape
   into a parent fixture (the 6-ancestor cap holds).

**Affected files:**
- `prep-core/MacArtifactAvailability.cs` — extend
  `EnumerateContentRoots`. ~20 added lines.
- `tests/MacArtifactAvailabilityTests.cs` — new test class.

**Cross-OS review pass:**
- **Windows surfaces:** `MacArtifactAvailability.Evaluate` is called
  by Windows PrepApp's `ArtifactStagingService` to gate cross-platform
  staging. The change preserves the original 2-candidate lookup
  order, so the Windows path is hit on the same first iteration.
  No behavior change.
- **Mac surfaces:** `MacArtifactAvailability.Evaluate` is called by
  the mac-prep-host sidecar via the same `ArtifactStagingService`.
  Ancestor walk lets the sidecar find the manifest from inside the
  .app bundle.
- **Decision:** Bundle (single shared-core fix; both OSes benefit
  from the same code path).

**Out of scope:**
- MAC20 ZIP layout rework (that puts the manifest closer to the
  apps but doesn't make the lookup itself robust).
- Runtime config plumbing of bundle-root path through the handshake
  (`mac-prep-host` could be told its bundle root explicitly), which
  is more invasive and unnecessary if the lookup walks ancestors.

**Acceptance criteria:**
- v1.3.3 Mac field run reaches the encryption-setup step instead of
  failing at `stage-runner` with the missing-manifest message.
- All five new tests pass on CI.
- Existing windows-build / mac-runner-build / mac-prep-build all
  pass — backward-compatible by construction.

**Tests:**
- New unit tests above.
- Manual smoke deferred to the same real-Mac + external-SSD loop
  the user is exercising right now; MAC22 + MAC21 together should
  let prep run end-to-end through staging into encryption setup.

---

### MAC23 - ArtifactStagingService bundled-file lookup walks ancestors

**Status:** done 2026-05-08 (PR #208, squash `0382acb`). Bundled
  with MAC19 docs. Symmetric mirror of MAC22 in
  `ArtifactStagingService.EnumerateBundledContentRoots`. Released
  as v1.3.4. Field test cleared `stage-runner` then immediately
  tripped on a 4th `BaseDirectory`-only lookup — see MAC24 below.
**Scope:** small — symmetric mirror of the MAC22 fix; extend
  `prep-core/Services/ArtifactStagingService.EnumerateBundledContentRoots`
  to walk parent directories + add unit tests.
**Risk:** Low — backward-compatible (Windows still hits the
  bundled files on the original first/second candidate); change
  confined to a single static helper plus tests.
**Dependencies:** None. Top of the queue (immediate field-blocker)
  the moment the user resumes the Mac field test.
**Goal:** Mac PrepApp's `stage-runner` / `stage-ollama` sidecar
  arms succeed at copying `Runner.app.zip` / `ollama-darwin.zip` /
  `mac-tools-manifest.json` from the bundle into the SSD layout,
  instead of failing with `"Bundled macOS Runner.app archive was
  not found."` immediately after the manifest-presence check passes.

**Driver:** v1.3.3 field test, immediately after MAC22 fixed the
manifest-presence check. PrepApp launched, format succeeded
(MAC21), `MacArtifactAvailability.Evaluate` returned Available
(MAC22), then the very next line in `StageMacRunnerAsync` tripped:

```
SSD layout created.
[stderr] Command failed ("stage-runner"): Bundled macOS Runner.app archive was not found.
```

Diagnosed at `prep-core/Services/ArtifactStagingService.cs:72-73`:

```csharp
var sourceRunnerZip = ResolveBundledFile(Path.Combine("mac", "Runner.app.zip"))
    ?? throw new FileNotFoundException("Bundled macOS Runner.app archive was not found.");
```

`ResolveBundledFile` (line 237) iterates `EnumerateBundledContentRoots`
which yields only `AppContext.BaseDirectory` and `<base>/payload` —
the **same** 2-candidate pattern MAC22 just fixed in
`MacArtifactAvailability.EnumerateContentRoots`, in a sibling
helper. On the Mac sidecar `AppContext.BaseDirectory` is
`PrepApp.app/Contents/Resources/prep-host/`, so neither candidate
finds `payload/mac/Runner.app.zip` 4 levels up.

The same enumerator is used for three Mac staging arms:
- Line 72:  `ResolveBundledFile("mac/Runner.app.zip")`
- Line 98:  `ResolveBundledFile("mac/tools/ollama/ollama-darwin.zip")`
- Line 143: `ResolveBundledFile("mac/tools/ollama/mac-tools-manifest.json")`

So fixing `EnumerateBundledContentRoots` once unblocks all three.

**Fix:**

Mirror the MAC22 fix exactly. In
`prep-core/Services/ArtifactStagingService.cs:249`:

```csharp
private static IEnumerable<string> EnumerateBundledContentRoots()
{
    yield return AppContext.BaseDirectory;
    yield return Path.Combine(AppContext.BaseDirectory, "payload");

    // MAC23: mirror MAC22 — Mac PrepApp's mac-prep-host sidecar
    // runs from PrepApp.app/Contents/Resources/prep-host/, so
    // AppContext.BaseDirectory is *not* the bundle root and the
    // bundled artifacts at <bundle>/payload/mac/ live several
    // levels up. Walk a bounded number of ancestors. Backward-
    // compatible: Windows finds bundles on the first or second
    // candidate above and never enters this loop.
    DirectoryInfo? cursor;
    try { cursor = new DirectoryInfo(AppContext.BaseDirectory); }
    catch { yield break; }

    for (var i = 0; i < 6 && cursor?.Parent is not null; i++)
    {
        cursor = cursor.Parent;
        yield return cursor.FullName;
        yield return Path.Combine(cursor.FullName, "payload");
    }
}
```

**Cleanup opportunity (out of scope for MAC23 itself):**
`MacArtifactAvailability.EnumerateContentRoots` and
`ArtifactStagingService.EnumerateBundledContentRoots` are now
doing the same thing. After MAC23 ships, fold them into a single
shared helper (e.g., `prep-core/BundleContentRootEnumerator`) so
this pattern doesn't drift. File as a follow-up cleanup item;
not load-bearing for the field unblock.

**Test (mirror MAC22 coverage):**

New `tests/ArtifactStagingBundledFileLookupTests.cs` covering:
1. Windows-equivalent layout — `ResolveBundledFile` finds an
   artifact at `<bundleRoot>/payload/mac/Runner.app.zip` from
   `BaseDir = bundleRoot`.
2. Mac sidecar layout — `ResolveBundledFile` finds the same file
   from `BaseDir = bundleRoot/payload/mac/PrepApp.app/Contents/Resources/prep-host/`.
3. Missing file — returns null after exhausting all candidates.
4. Bounded-depth cap — 8-level-deep BaseDir doesn't escape into
   parent fixtures.

Note: `ResolveBundledFile` is `private static` today. To test
without test-only API surface, either:
- Make it `internal static` + extend `InternalsVisibleTo` to
  `FreeAiSsd.Tests` (mirror what MAC16's prep-core wiring did
  for ModelOperations), OR
- Wrap test calls through the public `StageMacRunnerAsync` /
  `AreMacArtifactsAvailable` paths and assert the staging
  succeeds against a synthesized bundle tree.

The first option is cleaner and matches the MAC16 precedent.

**Affected files:**
- `prep-core/Services/ArtifactStagingService.cs` — extend
  `EnumerateBundledContentRoots` (~20 added lines); flip
  `ResolveBundledFile` to `internal static` for testability.
- `prep-core/FreeAiSsd.PrepCore.csproj` —
  `<InternalsVisibleTo Include="FreeAiSsd.Tests" />` already
  added in MAC16; verify it's still there and skip if so.
- `tests/ArtifactStagingBundledFileLookupTests.cs` — new test class.

**Cross-OS review pass:**
- **Windows surfaces:** `ResolveBundledFile` is called from the Mac
  staging arms only — Windows staging uses
  `ResolveRunnerPublishDirectory` / `ResolveCompanionPublishDirectory`
  which look at `<base>/runner-publish` and `<base>/companion-publish`.
  No Windows behavior change. The Windows PrepApp's own arming code
  (which resolves `Runner.app.zip` for cross-platform Mac inclusion)
  was already working pre-MAC23 because `AppContext.BaseDirectory`
  on Windows is the bundle root — the lookup wins on the first
  iteration.
- **Mac surfaces:** All three Mac staging arms (`stage-runner`,
  `stage-ollama` payload, `stage-ollama` manifest) gain the
  ability to resolve bundled files from inside the .app bundle.
- **Decision:** Bundle (single shared-core fix; both OSes use
  the same code path).

**Out of scope:**
- The cleanup-into-shared-helper item flagged above.
- MAC20 ZIP layout rework.
- Notarization (MAC11).

**Acceptance criteria:**
- v1.3.4 Mac field run: `stage-runner` succeeds, then
  `stage-ollama` succeeds, then `stage-prereqs` succeeds, and the
  flow advances into encryption setup.
- All four new tests pass on CI.
- Existing windows-build / mac-runner-build / mac-prep-build all
  pass — backward-compatible by construction.

**Tests:**
- New unit tests above.
- Manual smoke deferred to the same real-Mac + external-SSD loop
  the user is exercising; MAC21 + MAC22 + MAC23 together should
  let prep run end-to-end through staging into encryption setup.

**Field-test pattern observed:** MAC21 → MAC22 → MAC23 is a chain
of latent bugs that all share the same root cause — prep-core was
written assuming `AppContext.BaseDirectory` is the bundle root
(Windows-true, Mac-false because the sidecar lives 5 levels deep).
Each fix unblocks the next layer down, which surfaces the next
one. The deep-cleanup follow-up (centralize bundle-root resolution)
becomes worthwhile after MAC23 to prevent a MAC24/MAC25 in any
future Mac-staging code path that adds a fourth `AppContext.BaseDirectory`-
relative lookup.

---

### MAC19 - Mac install docs + xattr quarantine workaround

**Status:** done 2026-05-08 (PR #208, squash `0382acb`). Bundled
  with MAC23. README + `docs/QUICKSTART.txt` rewritten with the
  actual `xattr -dr com.apple.quarantine` workaround replacing the
  stale "right-click → Open" instruction (right-click and "Allow
  apps from anywhere" don't clear the quarantine bit on
  ad-hoc-signed bundles, contrary to the prior docs). Optional
  helper script skipped — Compress-Archive on Windows CI doesn't
  preserve Unix exec bits, so a `.command` file would need
  `chmod +x` first which is worse UX than pasting one line.
  Obsoletes once MAC11 ships.
**Scope:** small — release-bundle docs + a one-line install helper script.
**Risk:** Low
**Dependencies:** None. Lands before MAC11 specifically because it
  unblocks Mac field testing today; MAC11 makes it obsolete.
**Goal:** A Mac user who downloads the cross-platform release ZIP
  in 2026-05-07 state can launch `Runner.app` / `PrepApp.app`
  without bouncing off the misleading "damaged → move to trash"
  Gatekeeper error.

**Driver:** v1.3.1 download (2026-05-07) reproduced the issue end-
  to-end on the user's real Mac:
- `codesign -dv` reports `Signature=adhoc`, `TeamIdentifier=not set`
  on both bundles (expected — MAC11 hasn't landed).
- `xattr -l` shows `com.apple.quarantine: 0083;…;Safari;…` on both
  bundles after Safari download.
- Combination = Gatekeeper rejection with the misleading
  "FreeAiSsd is damaged and can't be opened. You should move it
  to the Trash." message. Users who don't know the standard macOS
  workaround assume the build is broken and bounce.

**Fix:**
- **Bundle root file** `MAC-INSTALL.txt` (or section inside the
  existing `QUICKSTART.txt` — pick at execution start) explaining:
  - Why the "damaged" message appears (unsigned ad-hoc app +
    Safari quarantine bit, until MAC11 ships).
  - The two-line workaround:
    ```
    xattr -dr com.apple.quarantine /path/to/Runner.app
    xattr -dr com.apple.quarantine /path/to/PrepApp.app
    ```
  - Note that this is a temporary unblock and goes away with the
    next signed/notarized release.
- **Optional helper script** `mac/unblock-apps.command` (one
  double-click → strips quarantine from both bundles in the same
  folder, prints success). `.command` is a Terminal-launchable
  shell script; standard macOS pattern. Keep it under 20 lines and
  avoid `sudo`.
- **README update** with a one-paragraph "Mac users: first launch"
  callout pointing at MAC-INSTALL.txt.

**Out of scope:**
- Self-signing instructions for the user's own Apple Developer cert
  (advanced; MAC11 is the right answer).
- Any change to bundle layout (MAC20 owns that).
- Modifying the existing CI Mac bundle assembly (MAC20 owns that).

**Acceptance criteria:**
- Cross-platform release ZIP contains `MAC-INSTALL.txt` (or an
  enriched QUICKSTART.txt section) explaining the workaround.
- README has the "first launch on Mac" callout.
- A user who follows the doc launches both apps successfully on a
  fresh Mac with default Safari-downloaded ZIP.

**Tests:** Doc review + manual smoke on a fresh Mac.

---

### MAC20 - Cross-platform release ZIP layout rework

**Status:** planned 2026-05-07 (field-reported by user same session)
**Scope:** medium — touches CI release-assembly script + likely
  PrepApp sidecar-discovery paths.
**Risk:** Medium — restructuring publish layout can break PrepApp's
  staging if the sidecar paths it reads from move.
**Dependencies:** None hard, but coordinates with MAC11 (signing) —
  if MAC11 lands first the layout work can fold in the
  notarization-stable `.app.zip` framing cleanly.
**Goal:** Restructure the cross-platform release ZIP so a user
  unzipping it sees a clean, OS-segregated tree, not a folder of
  loose Windows DLLs with a `payload/` subfolder hiding everything
  useful.

**Driver:** v1.3.1 cross-platform download (2026-05-07, user
  feedback): root currently shows `FreeAiSsd.PrepApp.exe` plus a
  scatter of `D3DCompiler_47_cor3.dll` / `e_sqlite3.dll` /
  `PenImc_cor3.dll` / `PresentationNative_cor3.dll` /
  `vcruntime140_cor3.dll` / `wpfgfx_cor3.dll` / `*.pdb` symbol
  files / `Resources/` (WPF assets). Mac apps live three folders
  deep at `payload/mac/Runner.app`. A Mac user opening this looks
  like a Windows-only build. User direction:

  > "i'd like 4 things at root. a windows folder with relevant
  > apps. and a mac folder with relevant apps. there can be a
  > resources folder at root too thats fine. and id like the
  > quickstart.txt. this doesn't mean duplicate things and hide
  > them id like it restructured so its cleaner and doesn't have
  > random files like you can see in the folder now."

**Target layout:**

```
Free-AI-SSD-beta-crossplatform/
├── windows/
│   ├── PrepApp.exe              (was FreeAiSsd.PrepApp.exe at root)
│   ├── *.dll                    (WPF runtime DLLs that were at root)
│   ├── Resources/               (WPF resources)
│   ├── runner-publish/          (was payload/runner-publish/)
│   ├── companion/               (was payload/companion/)
│   └── tools/                   (was payload/windows/)
├── mac/
│   ├── Runner.app
│   ├── Runner.app.zip
│   ├── PrepApp.app
│   ├── PrepApp.app.zip
│   ├── runner-host/
│   ├── tools/
│   └── mac-artifacts.manifest.json
├── QUICKSTART.txt
└── LICENSE
```

Four root entries (`windows/`, `mac/`, `QUICKSTART.txt`, `LICENSE`)
matching the user's "4 things at root" call. The user's "Resources
folder at root is fine" is honored by the alternative of leaving
`Resources/` at root if moving it into `windows/` proves to break
WPF asset resolution (PrepApp.exe expects `Resources/` next to it
by default). Pick at execution start; lean toward moving inside
`windows/` to keep root spotless.

**Hard part — sidecar path coupling:** Today's PrepApp.exe stages
  the Windows runner / Companion / Windows tools onto the SSD by
  reading `./payload/runner-publish/`, `./payload/companion/`,
  `./payload/windows/` (relative to the running PrepApp.exe).
  Moving PrepApp.exe into `windows/` shifts those to
  `./runner-publish/`, `./companion/`, `./tools/` (sibling paths,
  not parent). Need to either:
  - **Option A:** Update the staging path constants in `prep-core`
    (`shared/Services/ArtifactStaging.cs` or wherever the relative
    paths live) so they look at sibling folders. Simplest; any
    out-of-tree integrators that copied the layout will break, but
    there shouldn't be any.
  - **Option B:** Symlink / hardlink `payload/` -> `.` inside the
    new `windows/` folder so old paths keep working. Rejected
    because Windows ZIP extraction doesn't preserve symlinks.
  - **Option C:** Make the staging paths configurable via
    PrepApp's existing args / config so both layouts work during
    a transition.

  Recommendation: **Option A**. Single PR, single layout, single
  truth. No shipped-to-users dependency on the old layout exists.

**Fix:**
- Update `.github/workflows/build.yml` `Assemble distributables`
  step to produce the new tree.
- Update `prep-core` staging path constants (and any defensive
  fallbacks) to read sibling folders rather than `payload/`.
- Update the existing `Validate release payload` CI step to assert
  the new shape (no loose DLLs at root, both `windows/` and `mac/`
  exist, no PrepApp.exe duplication, etc.).
- Update `README.md` walkthrough to match the new structure.
- Update `docs/QUICKSTART.txt` ASCII tree if it has one.
- **Cross-OS:** Mac side is already structurally clean (everything
  under `payload/mac/` moves wholesale to `mac/`); only the
  `mac-artifacts.manifest.json` `relativePath` values need to drop
  the `mac/` prefix or stay path-relative-to-bundle-root depending
  on whether anything reads them.

**Out of scope:**
- Splitting into separate `Free-AI-SSD-windows-*.zip` +
  `Free-AI-SSD-macos-*.zip` artifacts (a real cross-platform
  release-pipeline conversation; defer until MAC11 + MAC15 land
  and beta framing is replaced).
- `.dmg` packaging for Mac (a notarization-era item).
- Removing `*.pdb` debug symbols from the Windows publish (small
  cleanup, fold in if cheap; otherwise its own item).

**Acceptance criteria:**
- Unzipped tree has exactly four root entries: `windows/`, `mac/`,
  `QUICKSTART.txt`, `LICENSE` (plus `Resources/` if execution-time
  call is to keep it at root).
- No loose `*.dll`, `*.pdb`, or `*.exe` files at root.
- Windows-only download path: user double-clicks
  `windows/PrepApp.exe`, prep flow works end-to-end against an
  external SSD (no path-not-found errors).
- Mac download path: user opens `mac/PrepApp.app` (after MAC19's
  xattr unblock until MAC11 ships), prep flow works.
- CI `Validate release payload` step passes.

**Tests:**
- CI guardrail asserting the new shape (no loose root files except
  the four whitelisted entries).
- Manual smoke on Windows: prep an SSD with the relocated
  PrepApp.exe.
- Manual smoke on Mac: prep an SSD with the relocated PrepApp.app.

**Risk mitigations:**
- Do this on its own PR; don't combine with other release-related
  work. CI artifact diffing is the primary safety net.
- Keep the old layout's CI guard line (`payload/FreeAiSsd.PrepApp.exe`
  duplicate check) updated rather than deleted, so any regression
  back to the old shape fails CI.

---

### MAC15 - Supported Mac release docs

**Status:** planned
**Scope:** docs/release
**Risk:** Low
**Goal:** Replace beta caveats with a real supported Mac feature matrix once
  the minimum release criteria are met.

**Acceptance criteria:**
- README and QUICKSTART match actual Mac behavior.
- Unsupported/deferred features are named.
- Docs state Apple Silicon-only, macOS 11+, arm64-only app artifacts, and exFAT
  for shared Windows + macOS SSDs.
- Troubleshooting covers Gatekeeper, permissions, Ollama, encrypted drives,
  and external SSD filesystem guidance.

**Tests:** Doc review.

## Recommended Next Step

**Status as of 2026-05-07:** Mac-track packaging + cross-platform
prep parity tracks are complete. Specifically:

- Runner parity: MAC0-MAC9 done. Mac runs the same RunnerCore
  business logic via `mac-runner-host` for chat, RAG, and library
  management.
- Packaging: MAC10a (Windows PrepApp filesystem-from-PrepTargets),
  MAC10b (shared app icon + Info.plist polish) done.
- Cross-platform PrepApp parity: MAC16 (`prep-core/` extraction),
  MAC17 (macOS PrepApp MVP), MAC17a (PR #193 review threading
  cluster), MAC17b (ensure-structure sidecar), MAC18 (compatibility
  docs + matrix) all done.

**Three Mac field-blockers surfaced 2026-05-07 by the v1.3.1 download
test (filed same day, ordered by user direction):**
- **MAC21** — Mac PrepApp post-format mount discovery (DiskCandidate
  model split). Top of queue. Without this, Mac PrepApp can't
  complete a single end-to-end prep — `listExternalCandidates`
  reads `MountPoint` from the parent disk only, but
  `diskutil eraseDisk` mounts the new volume on the child
  partition. Caught during MAC17's deferred manual smoke.
- **MAC19** — Mac install docs + xattr quarantine workaround.
  Small. Lands before MAC11 to unblock Mac field testing today
  ("damaged → move to trash" Gatekeeper rejection on
  Safari-downloaded unsigned bundles). Goes obsolete the day
  MAC11 ships but is the right interim.
- **MAC20** — Cross-platform release ZIP layout rework. Medium.
  Restructures the bundle root to `windows/` + `mac/` +
  `QUICKSTART.txt` + `LICENSE` so Mac users don't see a
  Windows-DLL-and-PDB scatter at root. Touches the CI release-
  assembly script and PrepApp's sidecar-discovery paths.

**One Mac item remains before a real signed beta cut:**
- **MAC11** — Signing + notarization. Back-burnered until the
  user's Apple Developer account renews on payday. MAC10b already
  landed the bundle metadata (`CFBundleIconFile`,
  `LSApplicationCategoryType`, `LSRequiresNativeExecution`, etc.)
  this item depends on, so MAC11 is plumbing-ready when the cert
  returns.

**MAC15 (supported Mac release docs)** is gated on MAC11 — once
the bundle is signed and notarized, the "macOS beta" framing
across README + QUICKSTART can be replaced with a real supported
Mac feature matrix.

**Post-release feature-parity items** that aren't on the critical
path for the first signed Mac beta:
- MAC12 (voice / TTS — microphone, STT, TTS adapters).
- MAC13 (HOTAS / PTT — implement or deliberately defer).
- MAC14 (DCS import on Mac).

These remain backlog items per MAC1's deferral; pull forward only
when there is concrete user demand on Mac.

**Next non-Mac active work:** F2 (Live model list fetch) is the
top of the queue per `agent_docs/project_state.md`. Cross-platform
from the start — execution prompt drafted at
`agent_docs/f2_execution_prompt.md`.

---

### MAC24 - PrereqService bundled folder lookup walks ancestors

**Status:** done 2026-05-08 (PR #209, squash `0e86c47`). Released
  as v1.3.5. Fourth occurrence of the MAC22 / MAC23 ancestor-walk
  pattern, in `prep-core/Services/PrereqService.ResolveBundledPrereqDirectory`.
  v1.3.4 mac field test surfaced it the moment MAC23 unblocked
  `stage-runner` — flow advanced to `stage-prereqs` and tripped
  with `"Bundled prerequisites folder is missing: …/prep-host/
  payload/windows/tools/prereqs"`. Same root cause: helper only
  checked `<base>/<prereqs>` and `<base>/payload/<prereqs>`,
  neither of which hit when `AppContext.BaseDirectory` is the
  Mac sidecar 5 levels deep inside the bundle. Fix flipped to
  `internal static` with a `baseDirectory` overload, walked up to
  6 ancestors, preserved the canonical `<base>/payload/<prereqs>`
  fall-through path so the `Directory.Exists` failure mode message
  stays identical when the folder genuinely is missing. Four
  fixture-driven tests in `tests/PrereqBundledLookupTests.cs`
  mirror the MAC22 / MAC23 coverage shape (Windows-equiv layout,
  Mac sidecar layout, no folder anywhere returns conventional
  diagnostic path, bounded-depth at 8 levels deep doesn't escape
  the 6-ancestor walk). Audit at fix time confirmed no 5th
  `AppContext.BaseDirectory`-only lookup remains in prep-core
  (`FindRepoRoot(AppContext.BaseDirectory)` already walks
  ancestors looking for `FreeAiSsd.sln`, dev-time fallback only).
  CI green on first run.

**Cleanup follow-up (now overdue):** three identical ancestor-walk
enumerators live across `MacArtifactAvailability.EnumerateContentRoots`,
`ArtifactStagingService.EnumerateBundledContentRoots`, and
`PrereqService.EnumerateBundleRoots`. Fold into a shared
`prep-core/BundleContentRoots` helper before any new
`AppContext.BaseDirectory`-relative bundled-file lookup gets added.

---

### MAC25 - OllamaPackageService.ResolveOllamaExe Mac binary name

**Status:** planned 2026-05-08 (field-reported via v1.3.5
  screenshot; immediate field-blocker the moment MAC24 unblocked
  the staging chain).
**Scope:** small — single helper + tests.
**Risk:** Low — backward-compatible (Windows lookup keeps finding
  `ollama.exe`).
**Dependencies:** None. Top of queue.
**Goal:** Mac PrepApp's `pull-model` sidecar arm succeeds against
  the Mac-staged Ollama binary, so users actually reach a
  populated `models/` directory instead of bouncing off a
  misleading "Run stage-ollama first" message.

**Driver:** v1.3.5 mac field test cleared format → ensure-structure
→ stage-runner → stage-ollama → stage-prereqs → 399-model catalog
refresh → encryption setup, then tripped on first model pull:

```
[stderr] Command failed ("pull-model" llama3.2:3b): Mac Ollama
  binary not found under /Volumes/FREEAI/mac/tools/ollama. Run
  stage-ollama first.
```

Misleading hint — `stage-ollama` *did* run and reported `"Staged
macOS Ollama runtime and wrote trust attestation."` Root cause at
`prep-core/Services/OllamaPackageService.cs:54-58`:

```csharp
public string? ResolveOllamaExe(string ollamaDir)
{
    if (!Directory.Exists(ollamaDir)) return null;
    return Directory.EnumerateFiles(ollamaDir, "ollama.exe", SearchOption.AllDirectories).FirstOrDefault();
}
```

Hardcodes `ollama.exe`. `StageMacOllamaAsync`
(`prep-core/Services/ArtifactStagingService.cs:126`) writes the
binary as `<ssd>/mac/tools/ollama/ollama` (no extension — that's
the macOS upstream convention). Mac sidecar `pull-model` and
`verify-model` arms call the same resolver → returns `null` →
`pull-model` throws.

**Fix:** OS-aware filename in `ResolveOllamaExe`:

```csharp
public string? ResolveOllamaExe(string ollamaDir)
{
    if (!Directory.Exists(ollamaDir)) return null;
    var fileName = OperatingSystem.IsWindows() ? "ollama.exe" : "ollama";
    return Directory.EnumerateFiles(ollamaDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
}
```

While in this file, audit the misleading error message in
`mac-prep-host/HostLifetime.cs:213` — "Run stage-ollama first" is
wrong when stage-ollama already ran; reword to "Mac Ollama binary
missing at the expected path; staging may have failed silently."
or similar.

**Affected files:**
- `prep-core/Services/OllamaPackageService.cs` — pick filename
  by OS.
- `mac-prep-host/HostLifetime.cs` — clearer error message when
  the binary is missing.
- `tests/` — new test pinning Mac filename resolution against a
  synthesized `<dir>/ollama` layout; existing Windows callers
  exercise the .exe path.

**Cross-OS review pass:**
- **Windows surfaces:** Runner / Companion / Windows PrepApp keep
  finding `ollama.exe`. No behavior change.
- **Mac surfaces:** `pull-model` and `verify-model` sidecar arms
  resolve, unblocking the model-pull stage of the Mac field test.
- **Decision:** Bundle (single shared-core helper, both OSes use
  same code path).

**Acceptance criteria:**
- Mac field run completes a starter-model pull (e.g.
  `llama3.2:3b`) and the model lands in `<ssd>/models/`.
- Existing Windows tests still pass; new Mac filename test passes.
- v1.3.6 mac field run reaches readiness checks for the first
  time.

**Status update 2026-05-08 (post-v1.3.6 field test):** MAC25 fix
shipped and the resolver now picks `ollama` correctly on Mac, but
the field test surfaced a deeper architectural issue (MAC26) — the
binary the resolver finds is the wrong one. Acceptance criteria
above are blocked by MAC26.

### MAC26 - Mac Ollama staging picks the wrong binary; pulls cannot land on the SSD

**Status:** **done** — merged 2026-05-08 (PR #212, squash commit
  `1a9c50b`). Released as **v1.3.7**. Implementation matched the
  filed plan: `ArtifactStagingService.StageMacOllamaAsync` validates
  the inner-Resources binary exists, deletes the top-level
  LaunchServices shim, passes the inner path to
  `MacOllamaStagingPipeline.VerifyAndAttest`. New
  `OllamaPackageService.ResolveMacOllamaExe` returns the inner path
  strictly (no recursive walk on Mac so a stray shim can't win).
  `ReadinessService` + `MacOllamaLifecycleService.ResolveBinaryPath`
  (Network Mode) + Swift `mac-runner` local-mode all updated to the
  inner path. Four new resolver tests + updated lifecycle suffix
  pin the new behavior. Kickoff CLI verification on the staged
  v1.3.6 SSD before code landed: `OLLAMA_MODELS=/tmp/test-...
  <inner-path>/ollama serve` → env propagated, runners loaded with
  default CWD (`Dynamic LLM libraries: [metal cpu_avx cpu_avx2]`),
  no `OLLAMA_RUNNERS_DIR` needed. CI green on first run
  (`windows-build` 3m1s, `mac-runner-build` 56s, `mac-prep-build`
  49s; `package-release` 2m41s on dispatch). Manual smoke (real
  model pull writing to `<ssd>/models/blobs/`) deferred to the
  v1.3.7 mac field test.
**Scope:** medium — touched five source files + two test files.
**Risk:** Medium — the fix changes which Mach-O the runtime executes
  and how its env propagates. Backward-compatible to Windows
  (untouched); changes Mac runtime behavior fundamentally.
**Dependencies:** None. User decision recorded: must NOT require
  user-managed Ollama install — the project bundles Ollama and the
  fix has to run a server self-contained, not via the user's own
  daemon.
**Goal:** Mac field run completes a starter-model pull and the
  model bytes land at `<ssd>/models/blobs/...` per the
  `OLLAMA_MODELS` env var the sidecar already sets.

**Driver:** v1.3.6 mac field test (post-MAC25). User attempted
two starter-model pulls. qwen2.5:7b reported exit code 137; the
"other model seemed to pull correctly" per the UI. Direct
inspection of the SSD afterwards (PrepApp still running with
spinner stuck on "pulling starter models"):

- `/Volumes/FREEAI/models/blobs/` — empty.
- `/Volumes/FREEAI/models/manifests/` — does not exist.
- `~/.ollama/models/` — does not exist (no fallback location either).
- No partial blobs anywhere on the system (`/tmp`,
  `/private/var/folders`, FREEAI). No `sha256-*` files period.
- Total used on the 1 TB SSD: 941 MB — that's just stage-runner +
  ollama package + prereqs. Zero model bytes were ever written.
- Host log (`/Volumes/FREEAI/logs/macos-prep-host-*.log`) stops
  cleanly at the prereqs-staged line; no log entries for catalog
  refresh, encryption setup, or any pull attempt.
- `pgrep ollama` → no process. `pgrep -fl PrepApp` → still alive,
  18 minutes idle, sidecar (`mac-prep-host` PID 46443) hung in
  `await` after the first failure with no `ollama` child.
- `lsof -p <sidecar>` → no SSD paths open. Sidecar not flushing
  to its log file; just stdin/stdout pipes back to Swift parent.

**Root cause:** The package the project downloads is
`https://github.com/ollama/ollama/releases/download/v0.5.7/ollama-darwin.zip`.
On macOS Ollama ships as a desktop GUI distribution, not a
self-contained CLI server. The zip contains:

- `ollama` (119 KB) — Electron-style **CLI shim** that talks to a
  running server via localhost:11434 / unix socket. If no server
  is up, the shim launches `Ollama.app` via **LaunchServices**.
- `Ollama.app/Contents/MacOS/Ollama` (119 KB) — Electron launcher
  for the GUI desktop app.
- `Ollama.app/Contents/Resources/ollama` (**53 MB**) — the actual
  Go-based Ollama CLI server. Mach-O universal (x86_64 + arm64),
  Developer ID signed (Identifier=`ollama`, TeamID=3MU9H2V9Y9).
  Supports `serve`, `pull`, `run`, all the same subcommands as
  Linux/Windows.
- `Ollama.app/Contents/Resources/lib/ollama/runners/{cpu_avx,cpu_avx2}/ollama_llama_server`
  — model-runner subprocesses the server invokes when running
  inference. Required at runtime for actual model use.

`ArtifactStagingService.StageMacOllamaAsync`
(`prep-core/Services/ArtifactStagingService.cs:88-158`) extracts
the zip, then at lines 123-127:

```csharp
var cliPath = Directory.EnumerateFiles(ollamaDir, "ollama",
    SearchOption.AllDirectories).FirstOrDefault()
    ?? throw new FileNotFoundException(...);
var finalCliPath = Path.Combine(ollamaDir, "ollama");
File.Copy(cliPath, finalCliPath, overwrite: true);
```

`EnumerateFiles` returns whichever `ollama` (case-insensitive on
exFAT) it walks first. On the user's SSD the result is the
top-level **119 KB shim** copied to
`/Volumes/FREEAI/mac/tools/ollama/ollama`. That's what
`OllamaPackageService.ResolveOllamaExe` (post-MAC25) finds and
hands to `pull-model`.

`MacOllamaStagingPipeline.VerifyAndAttest` then validates the
shim — it has an arm64 slice, the archive SHA matches, and it's
Developer-ID signed — so staging "succeeds." But the binary
that lands on the SSD is the **wrong tool for the job**.

When the sidecar runs `ollama pull <tag>`:
1. The 119 KB shim tries to connect to localhost:11434 → no
   server.
2. macOS Ollama's documented fallback is to launch `Ollama.app`
   via LaunchServices.
3. **LaunchServices launches GUI apps in a clean environment** —
   the `OLLAMA_MODELS=/Volumes/FREEAI/models` and `OLLAMA_HOST`
   env vars set by `ModelOperations.PullModelAsync`
   (`prep-core/ModelOperations.cs:25-32`) **do not propagate** to
   the spawned daemon. So even when a pull does succeed, models
   would land in `Ollama.app`'s default location, not the SSD.
4. Headlessly-launched `Ollama.app` from the unusual
   `/Volumes/FREEAI/mac/tools/ollama/Ollama.app/` path can be
   killed by macOS (TCC, jetsam, signing/quarantine quirks). The
   exit 137 (= SIGKILL = signal 9) on qwen2.5:7b is consistent
   with this.
5. The sidecar's `RunProcessStreamingAsync` then hangs after the
   shim exits, because the shim returned 0 (it successfully
   handed off to LaunchServices) but the daemon it dispatched to
   was killed before any progress streamed back. PrepApp sits
   forever with the spinner.

**Why the user's "first pull seemed correct":** UI illusion. With
no `~/.ollama` directory existing on this Mac and zero blobs on
the SSD, **neither pull succeeded**. The shim probably reported
exit 0 (handed off to LaunchServices cleanly) and the UI flipped
to "done" before the daemon failed.

**Fix architecture (option 1, per user 2026-05-08 product call —
"don't make the user manually install ollama"):** stop using the
shim; run the inner self-contained server directly as a sidecar
child process. Concretely:

1. **Staging** (`ArtifactStagingService.StageMacOllamaAsync`):
   - After extraction, leave `Ollama.app/` intact (do NOT copy
     `ollama` out to `<ollamaDir>/ollama` — that step at
     lines 126-127 is the bug). Removing the copy preserves the
     code-signed `Ollama.app` bundle as Apple distributes it,
     which avoids quarantine / signing fragility.
   - Optionally remove the top-level 119 KB shim from the staged
     dir to make it impossible to invoke by accident.
2. **Resolver** (`OllamaPackageService.ResolveOllamaExe`):
   - On Mac, return
     `<ollamaDir>/Ollama.app/Contents/Resources/ollama`
     (the 53 MB self-contained server). On Windows, keep the
     existing `ollama.exe` walk — no change.
   - Keep the `internal static (dir, fileName)` MAC25 seam; add
     a `(dir)` Mac overload that walks to the bundle path.
3. **Spawn semantics** (`ModelOperations` already correct):
   - The sidecar invokes the resolved binary via
     `Process.Start(...)` with explicit `Environment[OLLAMA_MODELS]`
     and `Environment[OLLAMA_HOST]`. Direct child processes
     **do** inherit env. This bypasses LaunchServices entirely.
   - Working directory: set CWD to
     `<ollamaDir>/Ollama.app/Contents/Resources/` so the server
     finds its `lib/ollama/runners/` adjacents. Or set
     `OLLAMA_RUNNERS_DIR=<ollamaDir>/Ollama.app/Contents/Resources/lib/ollama/runners`
     explicitly.
   - The server is `serve`-capable, so the sidecar can either
     (a) start a long-lived `ollama serve` and run `ollama pull`
     against `OLLAMA_HOST=http://127.0.0.1:11434` like the
     Windows path does via `OllamaServerHandle`, or (b) call
     `ollama pull` directly — recent ollama versions auto-spawn
     a server when pull is invoked without one. **Prefer the
     explicit serve handle** for parity with Windows
     (`StartTemporaryServerAsync` already handles teardown).
4. **Verification pipeline**
   (`MacOllamaStagingPipeline.VerifyAndAttest`):
   - Validate the inner 53 MB binary's arm64 slice and codesign
     state, not the top-level shim. Update the test fixtures
     (`MacOllamaStagingPipelineTests.cs`) to drive synthesized
     `Ollama.app/Contents/Resources/ollama` layouts.
5. **Sidecar protocol** (`mac-prep-host/HostLifetime.cs`
   PullModel arm at line 195): no protocol change needed if the
   resolver returns the right path. Consider adding a timeout
   so a hung pull doesn't park the UI spinner indefinitely (the
   18-minute idle session this field test produced is its own
   small bug).

**Affected files:**
- `prep-core/Services/ArtifactStagingService.cs` — drop the
  `File.Copy(cliPath, finalCliPath, ...)` that lifts the wrong
  binary; let the .app bundle stay as-is.
- `prep-core/Services/OllamaPackageService.cs` — Mac branch of
  `ResolveOllamaExe` returns the inner-Resources path.
- `shared/Services/MacOllamaStagingPipeline.cs` — verify the
  inner server binary, not the shim.
- `tests/MacOllamaStagingPipelineTests.cs` — synthesize the
  `Ollama.app/Contents/Resources/ollama` layout in
  `WriteBinary` helpers; the existing `WriteBinary(root,
  "ollama", ...)` calls need to write under
  `Ollama.app/Contents/Resources/`.
- `tests/OllamaPackageServiceResolveTests.cs` — add a Mac case
  that asserts the resolver picks the inner-Resources binary
  even when a shim exists at the bundle root.
- (Maybe) `prep-core/Services/OllamaServerHandle.cs` (if it
  exists; resolution earlier showed no such file on disk —
  `OllamaServerHandle` may be defined inline in
  `OllamaPackageService.cs`; verify before editing).

**Cross-OS review pass:**
- **Windows surfaces:** completely untouched. Windows still
  resolves `ollama.exe`, still uses `OllamaServerHandle`, still
  passes env to the child process. No behavior change.
- **Mac surfaces:** the daemon is now a direct child process of
  the sidecar (not a LaunchServices-spawned GUI app), so env
  propagates and `OLLAMA_MODELS` actually points pulls at the
  SSD. No more SIGKILL-on-headless-launch. No more env-stripped
  daemon. No reliance on the user having Ollama installed.
- **Decision:** Bundle (single shared-core helper change; both
  OSes use the same code path with OS-aware filename + path
  selection).

**Acceptance criteria:**
- Mac field run completes a starter-model pull and the model
  lands at `<ssd>/models/blobs/sha256-...` with a populated
  `<ssd>/models/manifests/registry.ollama.ai/library/...`.
- `pgrep ollama` shows the server child of the sidecar during
  the pull; gone after pull-model completes.
- Sidecar log `<ssd>/logs/macos-prep-host-*.log` shows pull
  progress lines, not a clean stop at the prereqs step.
- qwen2.5:7b pulls successfully when memory permits; if it
  still 137s, that's a real OOM diagnostic and gets its own
  follow-up — but the smaller starter model (e.g.
  `llama3.2:3b`) must complete.
- Existing Windows tests still pass; new Mac tests pin the
  inner-Resources resolver and the staging-leaves-bundle-intact
  invariant.
- v1.3.7 mac field run reaches readiness checks for the first
  time.

**Open question for kickoff:** confirm
`Ollama.app/Contents/Resources/lib/ollama/runners/` is on the
default search path of the inner server binary when CWD is
`Contents/Resources/`. If not, set `OLLAMA_RUNNERS_DIR`
explicitly. Easy to verify on the staged SSD with a manual
`OLLAMA_MODELS=/tmp/test-ollama \
  /Volumes/FREEAI/mac/tools/ollama/Ollama.app/Contents/Resources/ollama \
  pull llama3.2:1b`
before any code lands.

### MAC27 - Mac sidecar must start a temporary `ollama serve` before pulling

**Status:** **done** — merged 2026-05-08 (PR #213, squash commit
  `4b70758`). Released as **v1.3.8**. Implementation matched the
  filed plan: `_ollamaPackage` and `_modelService` fields switched
  to interface types; new internal test-seam ctor; lazy
  `IOllamaServerHandle? _ollamaServer` field starts on first
  non-test-mode `pull-model` and is reused across the batch;
  `DisposeAsync` shuts the temp server down. Five new lifecycle
  tests in `MacPrepHostPullLifecycleTests.cs`. CI green on first
  run; v1.3.8 dispatched immediately, all four jobs green. v1.3.8
  mac field test confirmed `Starting temporary Ollama server on
  127.0.0.1:<port>...` log line fires correctly — but exposed
  MAC28 (15s health-poll budget too tight for the Mac cold-start
  chain).
**Scope:** small — one production file (`mac-prep-host/HostLifetime.cs`)
  + one new test file. No protocol or UI changes.
**Risk:** Low. The fix mirrors a well-trodden Windows pattern via the
  existing cross-platform `IOllamaPackageService.StartTemporaryServerAsync`
  / `OllamaServerHandle` seam; no new processes spawned in test mode; no
  Windows behavior change.
**Dependencies:** MAC26 must have shipped (it did, v1.3.7) — without
  the inner-Resources binary as the resolver target, starting a temp
  server points at the LaunchServices shim and the field bug returns.
**Goal:** First Mac field model pull lands bytes at
  `<ssd>/models/blobs/sha256-...`.

**Driver:** v1.3.7 mac field test. User reported PrepApp gets to the
"Pulling starter models..." step, the macOS Gatekeeper "Verifying
'Ollama'" modal pops up briefly (first-launch signature check on the
inner binary), then the pull fails with:

```
Pulling Llama3.2:1b
Error: could not connect to ollama app, is it running?
[stderr] Command failed (`pull-model llama3.2:1b`):
  Failed to pull model llama3.2:1b. Exit code: 1
```

**Root cause:** parity gap MAC26 didn't cover. `ollama pull` is a thin
client; it requires a daemon at `OLLAMA_HOST`. Windows
(`shared/ViewModels/PrepViewModel.cs:782`) explicitly starts a
controlled temp server via `_ollamaPackageService.StartTemporaryServerAsync`
before the pull loop, then passes `serverHandle.Host` as the
`OLLAMA_HOST` env to every `PullModelAsync`, then disposes in
`finally`. The Mac sidecar's `pull-model` arm
(`mac-prep-host/HostLifetime.cs:195` pre-MAC27) skipped the entire
server-start block — it just called `_modelService.PullModelAsync(...,
_ollamaHost)` with `_ollamaHost` pointing at the handshake-supplied
default (typically `http://127.0.0.1:11434`) where nothing is
listening, so the CLI emits the "could not connect" error and exits
non-zero. The Gatekeeper popup is unrelated to the failure — it's
macOS first-launch verification of the inner Ollama binary; after it
dismisses, the binary runs `pull` immediately and immediately fails.

**Fix:** lazily start a temp server on the first non-test-mode
`pull-model` and reuse it across all pulls in the same sidecar
lifetime. Symmetric to Windows.

1. `mac-prep-host/HostLifetime.cs`:
   - Switch `_ollamaPackage` and `_modelService` field types to the
     `IOllamaPackageService` / `IModelService` interfaces (already
     used elsewhere in the codebase) so tests can substitute fakes.
   - Add an internal test-seam ctor accepting nullable
     `IOllamaPackageService?` + `IModelService?`; the public ctor
     forwards null to keep production wiring unchanged.
   - Add `private IOllamaServerHandle? _ollamaServer;`
   - In `PullModelAsync` (after the `_testMode` short-circuit and
     after `ResolveOllamaExe` succeeds): if `_ollamaServer is null`,
     `_ollamaServer = await _ollamaPackage.StartTemporaryServerAsync(
       ollamaExe, modelsRoot, EmitLog, ct);`. Pass
     `_ollamaServer.Host` (not `_ollamaHost`) to
     `_modelService.PullModelAsync`.
   - In `DisposeAsync`: best-effort `_ollamaServer?.Dispose();
     _ollamaServer = null;` before the live-catalog dispose, so a
     sidecar exit can never leak an orphan `ollama serve`.

2. `tests/MacPrepHostPullLifecycleTests.cs` (new):
   - `PullModel_FirstCall_StartsTemporaryServerAndPassesItsHost`:
     non-test-mode pull invokes `StartTemporaryServerAsync` once and
     the inner `PullModelAsync` receives the temp host
     (`127.0.0.1:54321`), not `_ollamaHost`. This is the field-bug
     pin.
   - `PullModel_MultipleCallsInSameLifetime_ReuseSingleServerHandle`:
     three sequential `pull-model` commands → one server start,
     three pulls all sharing the same host.
   - `DisposeAsync_DisposesTemporaryServerHandle`: dispose must kill
     the temp server (the no-orphan invariant).
   - `PullModel_TestMode_DoesNotStartServer`: existing test-mode
     short-circuit stays server-free.
   - `PullModel_ResolveOllamaExeReturnsNull_DoesNotStartServer`:
     defense-in-depth — if Ollama staging silently failed, fail
     loudly *before* starting a temp server; otherwise the field
     error shape changes from "binary missing" (clear) to "ollama
     serve crashed" (confusing).

**Affected files:**
- `mac-prep-host/HostLifetime.cs` — lazy temp-server lifecycle.
- `tests/MacPrepHostPullLifecycleTests.cs` — new test file.

**Cross-OS review pass:**
- **Windows surfaces:** untouched. Windows already starts a temp
  server in `PrepViewModel.DownloadModelsAsync` and disposes it in
  `finally`. No code changes outside `mac-prep-host/`.
- **Mac surfaces:** same exact pattern (temp `ollama serve` on a
  random localhost port, OLLAMA_HOST passed to every pull, disposed
  on sidecar exit). The seam (`OllamaServerHandle.StartAsync`) was
  already cross-platform via `ProcessStartInfo` (no LaunchServices),
  so no Mac-specific spawn logic needed.

**Acceptance / smoke:**
- CI green: `windows-build` (runs the new lifecycle tests),
  `mac-runner-build`, `mac-prep-build`.
- v1.3.8 mac field run: starter model pulls, `<ssd>/models/blobs/`
  populates, `<ssd>/models/manifests/registry.ollama.ai/library/...`
  materializes, sidecar log captures pull progress lines.
- After sidecar exit (`shutdown` over stdin or process termination),
  no orphan `ollama` process — `pgrep -fl ollama` empty.

### MAC28 - OllamaServerHandle startup timeout + stream stderr

**Status:** **done** — merged 2026-05-08 (PR #214, squash commit
  `09b5f1b`). Released as **v1.3.9**. Implementation matched the
  filed plan: `WaitForHealthyAsync.maxAttempts` 30 → 120 (15s →
  60s); `DrainAsync` renamed `ConsumeAsync(reader, onLog,
  streamLabel)` and flipped to `internal static`; each non-empty
  line forwarded as `[ollama serve <streamLabel>] <line>`; a
  throwing `onLog` cannot abort the drain. Four new tests in
  `OllamaServerHandleConsumeTests.cs`. CI green on first run;
  v1.3.9 dispatched immediately. **v1.3.9 mac field test pulled
  `llama3.2:1b` successfully — first ever Mac starter-model pull
  to write blobs to `<ssd>/models/blobs/sha256-...`.** The v1.3.6
  → v1.3.9 layer cake (MAC25 → MAC26 → MAC27 → MAC28) clears
  end-to-end Mac model-pull. Surfaced MAC29 (readiness
  false-negatives on a working drive).
**Scope:** small — one production file (`prep-core/OllamaServerHandle.cs`)
  + one new test file. Cross-platform (touches Windows + Mac).
**Risk:** Low. Bumps a constant from 30 to 120 (15s → 60s health-poll
  budget) and routes stdout/stderr through the existing `onLog` callback
  instead of discarding it. No protocol change, no new spawn behavior.
**Dependencies:** MAC27 must have shipped (it did, v1.3.8) — without
  the temp server start path firing in the first place, there's
  nothing to wait on.
**Goal:** Mac field model pull clears the cold-start window where
  Gatekeeper signature verification + Go runner discovery exceed 15s.

**Driver:** v1.3.8 mac field test. The MAC27 fix is partially working —
the new log line `Starting temporary Ollama server on 127.0.0.1:53376...`
proves the temp server start path fires. But the next line is:

```
[stderr] Command failed (`pull-model llama3.2:1b`):
  Ollama server on 127.0.0.1:53376 did not become healthy within 15 seconds.
```

The user's screenshot also shows the macOS Gatekeeper "Verifying
'Ollama'" modal *still on screen* at the moment of failure — Gatekeeper
is holding the binary's first-launch signature verification (Apple OCSP
roundtrip) longer than the 15s health-poll budget. Once Gatekeeper
finishes, the Go-based ollama server still has to run its
`Dynamic LLM libraries: [metal cpu_avx cpu_avx2]` runner discovery
before binding `/api/tags`. The combined cold-start chain on a freshly
staged SSD blows the 15s window we wrote in November when Windows was
the only target.

**Diagnostic gap:** the inner ollama's stdout/stderr is silently
discarded by `OllamaServerHandle.DrainAsync` (literally named "drain").
There's no signal in the PrepApp log explaining *why* the server
didn't come up — the user only sees the timeout. Routing the output
through `onLog` would have made this field test self-diagnosing.

**Fix:**

1. `prep-core/OllamaServerHandle.cs`:
   - `WaitForHealthyAsync.maxAttempts`: `30` → `120`. With the existing
     500ms cadence, this lifts the budget from 15s to 60s. Same code
     path Windows uses; symmetric bump.
   - Rename `DrainAsync(StreamReader)` → `ConsumeAsync(StreamReader,
     Action<string> onLog, string streamLabel)`. Each non-empty line
     gets forwarded as `[ollama serve <streamLabel>] <line>`. Empty /
     whitespace lines filtered. `try { onLog(...) } catch { }` around
     the dispatch so a misbehaving `onLog` cannot leave bytes unread
     on the pipe (the loop's primary purpose remains preventing pipe
     deadlock). Flipped to `internal static` so tests drive it with
     synthetic streams.
   - Update both `Task.Run(() => DrainAsync(...))` call sites to pass
     `onLog` + label.

2. `tests/OllamaServerHandleConsumeTests.cs` (new, 4 cases):
   - Routes each non-empty line through `onLog` with the
     `[ollama serve <label>]` prefix.
   - Skips empty / whitespace lines (no UI clutter).
   - A throwing `onLog` does not abort the drain (pipe-deadlock
     invariant).
   - Empty stream returns immediately.

**Affected files:**
- `prep-core/OllamaServerHandle.cs` — timeout bump + ConsumeAsync.
- `tests/OllamaServerHandleConsumeTests.cs` — new test file.

**Cross-OS review pass:**
- **Windows surfaces:** the temp server start path is shared. Windows
  also gains the longer health-poll window (its 15s was tight too) and
  the streamed `[ollama serve]` log lines. No regression — Windows
  ollama serve typically binds in 1-3s, well within either budget.
- **Mac surfaces:** addresses the field-blocker directly. Cold-start
  chain (Gatekeeper + runner discovery) gets the headroom it needs.
  Streamed output makes future mac-specific spawn issues diagnosable
  without another field test round-trip.

**Acceptance / smoke:**
- CI green on `windows-build` (runs the new ConsumeAsync tests),
  `mac-runner-build`, `mac-prep-build`.
- v1.3.9 mac field run: PrepApp reaches the pull progress lines
  (`Pulling Llama3.2:1b... pulling X.YGB / Z.WGB`) without timing
  out at server startup; `<ssd>/models/blobs/sha256-...` populates;
  sidecar log includes `[ollama serve stderr]` lines confirming the
  Go server bound the port and discovered runners.

### MAC29 - Mac-aware readiness checks (config + installed-model)

**Status:** **DONE — PR #215 merged `4657347` (2026-05-08).**
  Awaiting release dispatch. Field-test smoke deferred until the
  next release ships. CI green run 2 (run 1 needed a fix-forward
  `6f99de1` adding `using FreeAiSsd.PrepApp;` to the test file —
  `ModelOperations` lives in the root prep-core namespace).
**Scope:** small-medium — three changes in two files
  (`shared/PortableConfig.cs`, `prep-core/Services/ReadinessService.cs`)
  + one new test file. Cross-platform.
**Risk:** Low. The disk-truth model check is more rigorous than the
  config-pinned check it replaces (Ollama blobs are content-addressed
  by filename, so the self-consistency test catches the same
  "blob-tampered" failure mode). The path fix corrects a latent
  Windows bug too — Windows tolerates double-backslash mid-path but
  it's still wrong.
**Dependencies:** None. Independent of the encryption-required
  policy decision (MAC29 makes the readiness layer correct
  regardless of which policy lands).
**Goal:** A successfully-prepped Mac SSD (encrypted config +
  starter model on disk) shows all-green readiness on the final
  page. v1.3.9 field test reproduced exactly this scenario as
  two false-negative reds (`Config.json valid` Fail +
  `≥1 installed model` Fail) on an SSD that was actually ready
  to use.

**Driver:** v1.3.9 mac field test screenshot. The drive worked —
model downloaded, blob materialized at `<ssd>/models/blobs/sha256-...`,
manifest at `<ssd>/models/manifests/...` — but the readiness page
flagged two reds:

- `Config.json valid` — Fail. "Config missing or unreadable; defaults loaded."
- `≥1 installed model` — Fail. "No models marked Installed in config."

**Root causes (three layered):**

1. **`ConfigRelativePath` uses Windows-style backslashes**
   (`shared/PortableConfig.cs:196`):
   ```csharp
   public string ConfigRelativePath => @"config\\portable-config.json";
   ```
   The verbatim string `@"\\"` is two literal backslashes. On
   Windows, `Path.Combine` collapses `\\` mid-path so it works by
   accident. On macOS, `Path.Combine` treats `\\` as part of a
   filename — `Path.Combine("/Volumes/FREEAI", "config\\portable-config.json")`
   yields `/Volumes/FREEAI/config\\portable-config.json`, a request
   for a file literally named `config\\portable-config.json` in the
   root. `File.Exists()` returns false → readiness fails before it
   ever opens the real config.

2. **Mac PrepApp writes only an encrypted config (MAC5 invariant);
   readiness checks plaintext path.** When encryption is enabled
   (mandatory on Mac per MAC17a), the Swift writer creates
   `portable-config.encrypted.json`, NOT `portable-config.json`
   (`shared/SsdEncryption.cs:41` defines the encrypted filename
   constant). `ReadinessService.RunReadinessChecksAsync` line 31-36
   loads the plaintext path via `PortableConfig.LoadWithValidationAsync`,
   which has no decryption path. Even with bug #1 fixed, the
   plaintext file by design never exists on a Mac-prepped SSD.

3. **Mac sidecar's `pull-model` arm doesn't update the encrypted
   config.** `mac-prep-app/Sources/PrepViewModel.swift:388` discards
   the sidecar's pull-model result (which carries `sha256` + `sizeBytes`
   per `mac-prep-host/HostLifetime.cs:218-224`). Windows
   (`shared/ViewModels/PrepViewModel.cs:794-797`) calls
   `_modelService.UpdateModelStatusAsync(... ModelInstallStatus.Installed,
   result.Sha256, ...)` after each pull; Swift skipped that step
   because the encryption flow zeroizes the passphrase before pulls
   run (`PrepViewModel.swift:359`), and re-deriving the key for each
   pull would either need to keep the passphrase live or re-prompt.
   So even when a Mac model pull succeeds, no
   `ModelInstallStatus.Installed` entry exists in any config.

**Fix architecture (three changes):**

1. **`shared/PortableConfig.cs:196`** — flip to forward slash:
   ```csharp
   public string ConfigRelativePath => "config/portable-config.json";
   ```
   `Path.Combine` normalizes `/` correctly on both OSes. One line,
   zero behavior change on Windows (the pre-fix double-backslash
   was tolerated by the kernel, not relied on).

2. **`prep-core/Services/ReadinessService.cs` — "Config.json valid"
   accepts encrypted OR plaintext.** Check both paths:
   `<root>/config/portable-config.encrypted.json` (preferred when
   encryption enabled) OR `<root>/config/portable-config.json`
   (plaintext fallback). For the encrypted file, validate via
   `JsonDocument.Parse` that it's well-formed JSON with the
   expected `scheme` field matching `SsdEncryption.SchemeName`
   — full integrity without needing the passphrase. For the
   plaintext file, keep the existing `LoadWithValidationAsync`
   roundtrip. Pass if either is valid; fail only if neither
   exists or both are corrupt.

3. **`prep-core/Services/ReadinessService.cs` — "≥1 installed
   model" switches to disk-truth.** Replace the
   `config.Models.Where(m => m.Status == ModelInstallStatus.Installed)`
   loop with disk-state inspection via the existing
   `ModelOperations.DiscoverModelsOnDisk(modelsRoot)` (already used
   by the `discover-models` sidecar arm). For each discovered model:
   - Locate its blob via `ModelOperations.FindModelBlobForModel`.
   - **If the loaded config contains a pinned SHA for that model**,
     verify against it (existing strict check). This preserves the
     Windows path's full integrity guarantee.
   - **If no config pin exists** (the Mac case post-MAC29 + the
     plaintext-prep case if the encryption-required policy lands
     option-b), verify the blob's content SHA-256 matches its
     filename digest (`sha256-<hex>` → SHA-256(file) == hex). Ollama
     blobs are content-addressed by filename, so this is a real
     integrity check — it catches blob-tampering even without a
     pinned config hash.
   - Pass if at least one model verifies; Fail with the list of
     bad models otherwise.

**Affected files:**
- `shared/PortableConfig.cs` — one-line `ConfigRelativePath` flip.
- `prep-core/Services/ReadinessService.cs` — config-presence check
  + disk-truth model check.
- `tests/ReadinessServiceTests.cs` (new) — four states pinned:
  encrypted-config + ≥1 installed model on disk → all-green
  (the v1.3.9 field-test scenario, the regression pin); encrypted
  config + zero installed models → only the model check fails;
  plaintext config + models on disk → matches Windows behavior
  unchanged; no config at all → both fail clearly.

**Cross-OS review pass:**
- **Windows surfaces:** path fix is silently correct (Windows kernel
  tolerated the prior double-backslash, no behavior change). Config
  check still passes against plaintext `portable-config.json`. Model
  check now uses disk-state but if the config pin exists, it's still
  applied — Windows users who pulled via the existing flow keep their
  full-strength integrity verification. New users (or
  encryption-required-mode-on-Windows once that ships) get the
  self-consistency fallback — strictly better than the pre-fix Fail.
- **Mac surfaces:** Both readiness checks pass on a successfully
  prepped + pulled SSD, which is the field-test goal. Mac PrepApp
  remains encrypted-config-only per MAC5; bug #3 (Swift not
  recording installed models) becomes a UX-only issue (config doesn't
  list the model) rather than a readiness blocker. A future MAC30
  could close bug #3 by either keeping the derived key live across
  the pull batch or moving to a `installed-models.manifest.json`
  plaintext sidecar.

**Acceptance / smoke:**
- CI green (`windows-build` runs the new ReadinessServiceTests;
  `mac-prep-build`, `mac-runner-build` unchanged).
- v1.4.0 (or v1.3.10) mac field run on a freshly-prepped SSD shows
  all-green readiness with at least one starter model pulled.
- Same SSD plugged into a Windows PC also shows all-green
  readiness against the encrypted config.

### MAC30 - Encryption optional (default OFF, opt-in passphrase) cross-OS

**Status:** filed 2026-05-08. Resolves the long-standing
  "Encryption-required policy" open question. **Product call
  (2026-05-08):** encryption becomes opt-in with the toggle
  defaulting OFF on both PrepApps. The passphrase prompt is
  framed as an optional security upgrade, not a gate.
**Scope:** medium — cross-OS, multi-layer:
  - Restore the `!enableEncryption` codepath that MAC17a-#6
    deleted (it threw `failed` because the writer was missing).
  - New `NoOpEncryptedConfigWriter` (Swift) that writes the
    plaintext `portable-config.json` instead of
    `portable-config.encrypted.json`.
  - Confirm `RunnerLocalApi` + Companion still authenticate when
    the encrypted-config secret bag is absent (read API key from
    plaintext config when no encrypted blob exists).
  - UX rework of the passphrase step on both Windows and Mac
    PrepApp: encryption toggle visible, default OFF, with a
    clear inline explainer ("Encrypts your portable-config.json
    so the API key and library metadata are unreadable on a
    lost SSD. Recommended if you enable Network Mode.").
  - Cross-OS per 2026-05-07 dual-OS rule.
**Risk:** Medium. Touches the MAC5 plaintext invariant explicitly:
  pre-MAC30, "no plaintext config containing secrets ever
  written" was a hard rule; post-MAC30, plaintext is allowed
  *unless Network Mode + Require API Key are both on*, which
  the existing `NetworkModeEncryptionRequiredMessage` guard at
  `shared/PortableConfig.cs:275` already enforces. So the
  invariant tightens to "API key is never written in plaintext"
  rather than "no plaintext config at all" — a narrower and
  more defensible posture.
**Dependencies:** MAC29 should ship first so the readiness
  layer correctly handles both encrypted and plaintext configs;
  MAC30 then exercises the plaintext path it added.
**Goal:** A user who taps "Skip encryption" on either PrepApp
  reaches Drive ready with no passphrase friction; the resulting
  SSD's `portable-config.json` is readable plaintext (no API key
  written; Network Mode toggle is off by default); a Windows
  Runner / Mac Runner unlocks the SSD without a passphrase
  prompt.

**Driver:** v1.3.5 mac field test pushback ("you cant pull the
models unless you set an encryption password. that shouldnt be
forced.") + v1.3.9 confirming the user still wants this. Per
the 2026-05-08 product call, encryption becomes a security
*upgrade*, not a gate.

**Fix architecture (rough sketch — refine at execution time):**

1. **Swift PrepApp — restore the toggle:**
   - `mac-prep-app/Sources/main.swift` `EncryptionSetupStepView`:
     re-add the `Toggle("Encrypt SSD config", isOn: $enableEncryption)`,
     default OFF. When OFF, the passphrase fields hide and the
     primary button reads "Continue without encryption". When ON,
     the existing passphrase + confirm flow shows.
   - `mac-prep-app/Sources/PrepViewModel.swift` `applyEncryption()`:
     branch on `enableEncryption`. ON branch unchanged (uses
     `EncryptedConfigWriter`). OFF branch invokes a new
     `PlaintextConfigWriter` (or just calls `SsdEncryption`'s
     plaintext save path) that writes
     `portable-config.json` directly.

2. **C# Windows PrepApp — flip the default:**
   - `prep-app/Views/EncryptionSetupView.xaml(.cs)` (or wherever
     the toggle lives — verify at execution time): default the
     toggle to `IsChecked = false`. Keep the explainer text.
   - Verify `PrepViewModel.SaveEncryptedConfigAsync` and the
     non-encrypted save path both still work — both probably do
     since Windows never lost the toggle, but pin via test.

3. **Plaintext-config write path (cross-OS):**
   - C#: `PortableConfig.SaveAsync` already handles plaintext;
     the `NetworkModeEncryptionRequiredMessage` guard prevents
     writing an API key in plaintext, which is the narrower
     invariant.
   - Swift: new `PlaintextConfigWriter` mirroring
     `EncryptedConfigWriter` shape. Writes
     `<ssdRoot>/config/portable-config.json` via JSONEncoder
     with the same camelCase contract as the C# JsonNamingPolicy.
     Same `Sendable` posture for the `Task.detached` hop.

4. **Runner unlock flow:**
   - Windows: `EncryptionService.TryLoadEncryptedConfigAsync`
     returns null when the encrypted file is absent → caller
     falls back to plaintext load. Verify this path still works
     end-to-end.
   - Mac runner (`mac-runner-host` + Swift `mac-runner`): same
     fallback. The current Mac Runner refuses encrypted SSDs
     with "mac unlock not supported yet" anyway, so plaintext
     should be the primary supported path on Mac for now.

5. **Companion auth:**
   - Companion authenticates against Runner via Bearer token
     stored in the *encrypted* config currently. With plaintext
     opt-in, the existing
     `NetworkModeEncryptionRequiredMessage` guard already
     blocks Network Mode + Require API Key + plaintext config.
     So Companion-on-LAN remains an encrypted-config feature.
     Document this in the UX explainer.

**Affected files (preliminary):**
- `mac-prep-app/Sources/main.swift` — restore encryption toggle
  in `EncryptionSetupStepView`.
- `mac-prep-app/Sources/PrepViewModel.swift` — branch on
  `enableEncryption`.
- `mac-prep-app/Sources/PlaintextConfigWriter.swift` (new) —
  Swift sibling of `EncryptedConfigWriter`.
- `prep-app/Views/EncryptionSetupView.xaml(.cs)` — flip default,
  refresh explainer text.
- `shared/ViewModels/PrepViewModel.cs` — confirm both save
  paths route correctly when toggle off.
- `tests/PortableConfigSaveAsyncTests.cs` (or new) — pin the
  `NetworkModeEncryptionRequiredMessage` guard still fires on
  the plaintext path.
- `tests/PlaintextConfigWriter…` (Swift) — round-trip the
  default payload through the plaintext writer; matches what a
  C# `PortableConfig.LoadAsync` would parse.
- `README.md` + `docs/QUICKSTART.txt` — update encryption
  framing from "required" to "optional, recommended for
  Network Mode".

**Cross-OS review pass:**
- **Windows surfaces:** toggle default flips OFF. Encrypted
  path unchanged for users who keep it on. Plaintext save path
  was always there; no behavior change for users who were
  already opting out on Windows.
- **Mac surfaces:** toggle restored after MAC17a-#6 removed it.
  Plaintext path is new. Encrypted path unchanged.
- **Encryption-required-mode triggers:** Network Mode + Require
  API Key still demands encryption per the
  `NetworkModeEncryptionRequiredMessage` guard. Document this
  path explicitly.

**Acceptance / smoke:**
- CI green: new tests pin both save branches + the API-key
  guard.
- Windows field run: PrepApp encryption toggle defaults OFF,
  user clicks through → plaintext config on SSD → Runner reads
  it without passphrase.
- Mac field run: PrepApp encryption toggle defaults OFF, user
  clicks through → plaintext config on SSD → Mac Runner reads
  it without passphrase.
- Cross-OS roundtrip: Mac-prepped plaintext SSD reads on
  Windows Runner; Windows-prepped plaintext SSD reads on Mac
  Runner.
- Network Mode regression: enabling Network Mode + Require
  API Key on a plaintext config triggers
  `NetworkModeEncryptionRequiredMessage` at save time (existing
  test should still pass; add an integration test if needed).

### MAC31 - Pull UX: cancel button, single progress line, preserve partial-download progress

**Status:** **done** — PR #221 merged on `c7646f2` (2026-05-08); v1.3.12 release dispatch in flight. All three sub-bugs landed in one cross-OS PR per the 2026-05-07 dual-OS rule. Mac PrepApp Models step gains a Cancel button gated on `vm.canCancelPull`; sidecar's new `cancel-pull` arm signals `_activePullCts` (linked CTS) under a lock; `Program.cs` detaches `pull-model` at the loop layer so the loop can read `cancel-pull` while a pull is in flight (HandleCommandAsync stays sequential — tests untouched); shutdown drains the in-flight pull task via cancel-pull + 5s WaitAsync. New static `OllamaPullProgressFilter` strips ANSI cursor-rewrite escapes (`\x1b[?25l`, `\x1b[2K`, `\x1b[1G`, etc.), collapses `\r`-separated rewrites to the latest segment, and detects Ollama's `pulling <hash>... NN%` shape conservatively (drift falls through to verbose log). `ModelOperations.Consume` gains optional `onProgress` (default null = back-compat); cleaned progress lines route there. Mac sidecar emits `progress: ...` on a dedicated stdout channel; PrepHostController's new `onPullProgress` callback routes to `pullProgressLine`; new `ModelPullStepView` in main.swift renders it as a single monospaced Text with `.lineLimit(1)` + truncation. Windows `PrepViewModel` exposes `PullProgressLine` (cross-thread safe via `SetPullProgressLineSafe`); MainWindow.xaml renders it under the Log card header via an extended `EmptyToVisibilityConverter` (now string-aware + supports `ConverterParameter=Inverted`). New `ModelOperations.EstimatePartialProgress` sums `<modelsRoot>/blobs/sha256-<digest>-partial-*` against manifest layer totals; pre-pull seed emits *"Resuming `<tag>` from NN%…"* so retry doesn't visually reset to 0%. Tag allowlist-validated to prevent path traversal. **Project-graph constraint:** shared can't reference prep-core (circular), so the Windows path threads via new `IModelService.EstimatePartialPullProgress`; mac-prep-host calls `ModelOperations.EstimatePartialProgress` directly. 18 new tests + 2 signature updates in existing test files. CI green on first run: `windows-build` 2m45s, `mac-runner-build` 44s, `mac-prep-build` 55s. v1.3.12 mac field run is the manual smoke pin.
**Scope:** medium — three discrete sub-bugs across both PrepApps
  + the prep-core ConsumeAsync log filter. Cross-OS.
**Risk:** Low for sub-bugs (b) and (c); medium for (a) — wiring
  Cancel into the actual ollama process kill path needs care
  around partial-blob cleanup so a cancelled pull leaves disk
  in a state Ollama's resume logic can pick up.
**Dependencies:** None. Independent of MAC29/MAC30/MAC33.
**Goal:** A 5GB pull on a slow connection presents as a single
  progress line that climbs monotonically, the user can cancel
  it without trashing the partial blobs, and clicking Retry
  picks up from where it left off rather than redownloading
  from 0%.

**Driver:** v1.3.10 mac field test, 8B model on a slow
connection. Three observed problems (in priority order):

**(a) No abort/cancel button during pull.** Both PrepApps go
modal during `pull-model` with a 30-min timeout
(`mac-prep-app/Sources/PrepViewModel.swift:388` —
`hostController.send("pull-model \(tag)", timeout: 1800)`).
The user has no way to bail out short of force-quitting the
app, which orphans Ollama partial blobs in an undefined state.

**(b) Log pane spams `pulling <hash>... NN%` lines.** Ollama's
TUI uses ANSI cursor-rewrite escapes (`[?25h[?25l[2K[1G[A`)
to overwrite the progress line in place in a real terminal.
MAC28's `OllamaServerHandle.ConsumeAsync` captures stdout
line-by-line and forwards each tick to `onLog`, but doesn't
strip or coalesce the rewrite escapes. Result: the log pane
gets a fresh `pulling <hash>... 43%` line every progress tick
(roughly 1Hz × 16 chunks = a flood) and the literal escape
codes appear as garbage at end-of-line. Looks like a restart
loop, isn't.

**(c) Visual progress resets to 0% on retry.** Ollama IS
resumable — partial blobs persist as
`<ssd>/models/blobs/sha256-<hex>-partial-N` and get
re-validated on the next `pull`. But our progress display
reads only the live `ollama pull` stdout, so on a fresh
invocation the bar starts at 0% and climbs through Ollama's
re-validation phase before resuming downloads. Confusing
because users expect "Retry" to mean "pick up from where I
was."

**Fix architecture:**

1. **Cancel button (sub-bug a):**
   - Mac PrepApp: add a Cancel button to the Models step's
     pulling UI. Hold a `Task` handle to the active pull
     operation; on Cancel, call `task.cancel()` and send
     `cancel-pull` to the sidecar.
   - `mac-prep-host/HostLifetime.cs`: add `cancel-pull` arm
     that signals the active pull's CancellationToken. The
     existing `ModelOperations.PullModelAsync` already
     registers a kill-process-tree on `ct.Register` (line 340),
     so the pipe-back wire-up is the missing piece.
   - Windows PrepApp: same Cancel button on the pulling step.
     `shared/ViewModels/PrepViewModel.cs` already passes ct
     into `_modelService.PullModelAsync`; UI just needs a
     Cancel command bound to a CancellationTokenSource.
   - Partial-blob cleanup: leave them on disk. Ollama's resume
     logic re-validates on next pull, so abandoned partials
     are not corruption — they're cached progress.

2. **Single progress line (sub-bug b):** in
   `prep-core/OllamaServerHandle.ConsumeAsync` (or a sibling
   helper), pre-process each line:
   - Strip ANSI cursor-rewrite escape sequences (a small
     regex catches `[?25h`, `[?25l`, `[2K`, `[1G`, `[A`, etc.).
   - Coalesce: if the cleaned line matches `pulling <hash>...
     NN%` (Ollama's progress format), suppress it from the
     `[ollama serve stdout]` stream and emit it on a separate
     dedicated channel that PrepApp UIs render as a single
     line that overwrites in place.
   - Keep `[ollama serve stderr]` lines (errors, stalls)
     forwarded as-is — those are exactly the diagnostics
     MAC28 added.
   - Tests in `OllamaServerHandleConsumeTests.cs`: pin the
     ANSI strip + the progress-line coalesce against
     captured Ollama TUI output.

3. **Preserve partial progress on retry (sub-bug c):** before
   spawning `ollama pull`, scan `<ssd>/models/blobs/` for
   `*-partial-*` files matching the model's expected blob
   digest. Sum their sizes against the manifest's total layer
   size and seed the progress display with that fraction.
   First progress tick from `ollama pull` then takes over.
   Cosmetic but materially improves "is this stuck or just
   resuming?" perception.

**Affected files:**
- Mac PrepApp Cancel UI: `mac-prep-app/Sources/main.swift`
  (Models step view), `mac-prep-app/Sources/PrepViewModel.swift`
  (cancel task wire-up).
- Windows PrepApp Cancel UI: `shared/ViewModels/PrepViewModel.cs`
  + `prep-app/MainWindow.xaml`.
- Sidecar cancel arm: `mac-prep-host/HostLifetime.cs`.
- Log filter: `prep-core/OllamaServerHandle.cs` ConsumeAsync.
- Resume seeding: helper in `prep-core/ModelOperations.cs`,
  called by both Windows and Mac pull paths.
- Tests: extend `OllamaServerHandleConsumeTests.cs`.

**Cross-OS review pass:**
- Both PrepApps need the Cancel UI per the 2026-05-07 dual-OS
  rule; both already have Cancel-style buttons elsewhere
  (drive-erase confirm) for visual reference.
- ConsumeAsync filter is shared in prep-core, so it benefits
  both OSes' pull paths automatically.
- Resume seeding is also a shared prep-core helper.

**Acceptance / smoke:**
- 5GB pull on a slow connection shows one progress line that
  monotonically climbs.
- Mid-pull Cancel button stops the process, leaves partial
  blobs on disk, returns user to a state where Retry resumes
  from the partial position rather than 0%.
- Cross-OS roundtrip: Mac-cancelled pull resumes correctly
  when the SSD is plugged into Windows and pull is re-run
  from Windows Runner (validates the partial-blob format is
  shared, not a Mac-only artifact).

### MAC31a - Cancel mid-pull falls through to readiness instead of offering Retry

**Status:** **done** — PR #225 merged on `179dfc0` (2026-05-09); v1.3.13 release dispatched in same session. Bundled cross-OS with MAC32 per the 2026-05-07 dual-OS rule.
**Scope:** small — Mac-only code change. Windows verified already correct.
**Risk:** Low. Pure UI plumbing on the Mac side.
**Dependencies:** Built on MAC31 (resume seed + Cancel button). Independent of MAC30/MAC32.
**Goal:** A user who cancels a multi-GB pull mid-flight lands on a clear paused state with Retry / Skip / Start over, rather than silently advancing past the pull to readiness with no surface to resume the partially-downloaded model.

**Driver:** v1.3.12 mac field test, 8B model on a slow connection. Per the v1.3.12 field-test screenshot, clicking Cancel during a stalled larger-model pull jumped straight to Finalize with no Retry button — even though MAC31's resume seed preserves partial blobs on disk under `<ssd>/models/blobs/sha256-<hex>-partial-N`. The on-disk preservation worked; the UX surface didn't expose it.

**Root cause:** `pullStarterModels` in `mac-prep-app/Sources/PrepViewModel.swift:391` unconditionally set `currentStep = .readiness` after the pull `Task` ended — including the `catch is CancellationError` branch that just `break`ed and fell through. The step machine had no "paused" state to land on.

**Fix architecture (Mac):**

1. **New flow step:** `PrepFlowStep.swift` gains `case modelPullPaused(tag: String, progressSnapshot: String?)` between `.modelPull` and `.readiness`. Snapshot is a `String?` (the last in-memory `pullProgressLine` value) rather than a Double fraction — design call 2026-05-09 to avoid a sidecar roundtrip on Cancel; the snapshot is the same string the user just saw on screen.
2. **Refactored pull loop:** `pullStarterModels` split into a public entry + a private `pullPendingTags`. Entry seeds `pendingPullTags` from `selectedStarterModels` only on first call; resume re-enters with `pendingPullTags` already populated (cancelled tag at index 0). Inside the loop, on `CancellationError` the cancelled tag + remaining queue are captured + transition routes to `.modelPullPaused`. On clean completion, queue clears and routes to `.readiness`.
3. **New VM methods:** `resumePull()` (re-enters the loop with `pendingPullTags` intact), `skipRemainingPulls()` (clears queue, advances to readiness), reuse existing `restart()` for Start over. `restart()` updated to clear `pendingPullTags` + `pullProgressLine` so a post-failure restart doesn't carry stale state.
4. **New step view:** `ModelPullPausedStepView` in `main.swift` shows headline + body explaining partial download is preserved, optional snapshot text in monospaced overlay (only if non-empty), three buttons: Retry (`.keyboardShortcut(.defaultAction)`) / Skip / Start over. Added to the `switch currentStep` in ContentView and the title-bar mapping ("5 / 6 — Pull paused").

**Windows verified already correct:** `shared/ViewModels/PrepViewModel.cs` `CancelOperation` just calls `_modelOperationCts.Cancel()`; `PullModelsAsync` catches `OperationCanceledException`, logs "Download cancelled", clears `PullProgressLine`, and returns. The user stays on the Models tab and clicking Download again triggers MAC31's resume seed at line 818 (`PullProgressLine = seed > 0 ? $"Resuming {model} from {seed:P0}…" : ...`). The Windows tabbed UI without a step machine is a natural fit for "implicit retry" — explicit Retry surface would be visual noise. Per the 2026-05-09 cross-OS parity-rule decision, this asymmetry is acceptable because the user-visible *behavior* matches: cancel a pull, click Download again, see "Resuming…".

**Affected files:**
- `mac-prep-app/Sources/PrepFlowStep.swift` — new `.modelPullPaused` case.
- `mac-prep-app/Sources/PrepViewModel.swift` — `pendingPullTags` state, `pullPendingTags` private, `resumePull` + `skipRemainingPulls` public, `restart` clears.
- `mac-prep-app/Sources/main.swift` — `ModelPullPausedStepView`, ContentView switch arm, step-title mapping.
- `mac-prep-app/Tests/PrepAppTests.swift` — 4 new MAC31a Equatable cases.

**Acceptance / smoke (deferred to v1.3.13 mac field run):**
- Multi-GB pull on a slow connection → Cancel mid-pull → land on paused step with snapshot text → Retry → "Resuming `<tag>` from NN%…" log line and pull continues from approximately where it stopped → eventual completion advances to readiness.
- Skip from paused → readiness without finishing the cancelled tag.
- Start over from paused → welcome with all state cleared.

### MAC32 - PrepApp Finish button is a no-op

**Status:** **done** — PR #225 merged on `179dfc0` (2026-05-09); v1.3.13 release dispatched in same session. Bundled cross-OS with MAC31a per the 2026-05-07 dual-OS rule. **(Mac)** `DoneStepView`'s Finish button silently called `vm.finalize()` (sidecar shutdown + log line) and left the window visibly frozen. Renamed to Quit; new `vm.quit()` calls `finalize()` then dispatches `NSApplication.shared.terminate(nil)`. Body copy now mirrors the Windows modal: "Your SSD is ready. Open `mac/Runner.app` on the SSD to start chatting. Quit when ready." **(Windows)** `FinalizeAsync` ended silently — added `_dialogService.ShowInfo("Your SSD is ready. Open Runner.exe on this SSD to start chatting.", "Setup complete")` on the full-success path only. `IDialogService.ShowInfo` already existed (used by `CheckReadinessAsync`). Asymmetric implementation per the 2026-05-09 decision: Mac is a step-machine flow with a natural terminal step; Windows is a tabbed XAML UI with no step machine — modal matches the user-visible message without re-architecting Windows. 3 new Windows tests pin modal-on-success / modal-NOT-on-no-profile / modal-NOT-on-readiness-failure. CI green on first run.
**Scope:** small — one Swift handler + one C# command. Cross-OS.
**Risk:** Trivial. Pure UI plumbing.
**Dependencies:** None.
**Goal:** Clicking Finish on the readiness page does *something*
  that signals "you're done with prep, here's what to do next."

**Driver:** v1.3.10 mac field test: user prepped a Mac SSD,
readiness all-green, clicked Finish, nothing happened. Window
stayed on the readiness page with no indication of what to do
next.

**Product call needed.** Three options:

**Option (a) — close the app.** Simplest. Mirrors what most
"setup wizard" apps do at completion. User then double-clicks
Runner.app on the SSD to start using it. Risk: feels abrupt;
no confirmation; the user may not realize the SSD is ready
because the app vanished.

**Option (b) — show a "you're done" page with a Quit
button.** A final-step view explaining: "Your SSD is ready.
Open Runner.app on the SSD to start chatting. Quit when ready."
Clearer; one extra click. Probably the right answer.

**Option (c) — auto-launch Runner.** Most user-friendly but
risky: the Mac Runner unlock flow is broken (MAC33), so
auto-launching would make MAC33 immediately visible without
giving the user a chance to read the readiness summary.
Skip until MAC33 is fixed.

**Recommendation:** option (b). Add a final `.done` step
view that congratulates and instructs. Window stays open so
the user can review readiness; Quit is explicit.

**Affected files:**
- Mac PrepApp: `mac-prep-app/Sources/main.swift` (new
  `DoneStepView`), `mac-prep-app/Sources/PrepViewModel.swift`
  (`finish()` action transitions to `.done` step).
- Windows PrepApp: `shared/ViewModels/PrepViewModel.cs`
  (Finish command), `prep-app/MainWindow.xaml` (final step).

**Cross-OS review pass:** simultaneous on both PrepApps per
the 2026-05-07 dual-OS rule.

**Acceptance / smoke:**
- v1.4.x field test: complete prep on either OS, click Finish,
  see a clear "you're done" view with a Quit button. Quit
  closes the app cleanly.

### MAC33 - Mac Runner shows zero selectable models on a Mac-prepped SSD

**Status:** **done** — PR #218 merged on `bf3a923` (2026-05-08); v1.3.11 release dispatch pending. Three runner-core consumers (`ModelManagementService.GetInstalledModelNames`, `GetModelSizingWarnings`, `RunnerLocalApiService.cs:160` `/models` endpoint) plus the Mac SwiftUI picker (`mac-runner/Sources/main.swift:415` `applyConfigToUi`, which reads `config["models"]` directly from the in-memory dict — *not* the LAN endpoint as the execution prompt initially assumed) all swapped to disk-truth via `ModelOperations.DiscoverModelsOnDisk`. `ModelManagementService` captures `ssdRoot` on ctor; `RunnerLocalApiService` injects `IModelManagementService` (back-compat fallback retained); both DI graphs wire the new ctor; Swift mirrors the manifest walk so all four consumers agree. Persistent writeback at unlock time was dropped — disk-truth reads at every consumer make it unnecessary and avoiding it keeps the MAC5 plaintext-config invariant simple. Five new unit tests pin the disk-truth scenarios. v1.3.11 mac field run is the manual smoke pin: prep + pull `llama3.2:1b` → unlock Runner → picker shows the model → chat works.
**Scope:** small-medium — same shape as MAC29 but for the
  Runner's model selector instead of ReadinessService.
  Cross-platform-aware (the Windows Runner reading a Mac-prepped
  SSD has the same problem).
**Risk:** Low. Same fix architecture as MAC29 sub-bug (3): swap
  config-pinned model enumeration for disk-truth via
  `ModelOperations.DiscoverModelsOnDisk`.
**Dependencies:** None. **Should ship before MAC30 + MAC31 +
  MAC32** because nothing else matters until Mac users can
  actually load and chat with a model from their prepped SSD.
**Goal:** A user who unlocks a Mac-prepped SSD in Mac Runner
  (or Windows Runner) sees the starter models they pulled in
  the model picker and can select one for chat.

**Driver:** v1.3.10 mac field test: user prepped Mac SSD,
pulled `llama3.2:1b` successfully, readiness all-green
(MAC29 win), opened Runner.app off the SSD, entered passphrase,
unlock appeared to succeed, but the model selector showed zero
options. Re-selecting the SSD and re-entering the passphrase
did nothing different. Zero post-prep usability.

**Root cause (almost certainly the same as MAC29 bug 3 in a
different consumer).** The Mac sidecar's `pull-model` arm at
`mac-prep-host/HostLifetime.cs:237` doesn't write back to the
encrypted config — the Swift caller at
`mac-prep-app/Sources/PrepViewModel.swift:388` discards the
result's `sha256`/`sizeBytes`, and the encryption flow zeroizes
the passphrase before pulls run, so re-deriving the key for
each pull is too expensive to bolt on. As a result,
`config.Models` is empty in the encrypted blob even after a
successful pull.

MAC29 fixed `prep-core/Services/ReadinessService.cs` by reading
disk truth via `ModelOperations.DiscoverModelsOnDisk` +
`FindModelBlobForModel`. **The Mac Runner's "Available
Models" picker is a different consumer of installed-model
state** that almost certainly still reads
`config.Models.Where(m => m.Status == ModelInstallStatus.Installed)`,
which is empty on a Mac-prepped SSD → empty picker.

**Verification step before code (kickoff):**
1. Grep the runner-core / mac-runner-host / runner WPF for
   the model-selector data source. Likely candidates:
   `runner-core/Services/`, `runner/ViewModels/`,
   `mac-runner-host/`.
2. Confirm it reads `config.Models` rather than enumerating
   disk. If so, the fix is clear.
3. If multiple consumers read `config.Models` for installed
   state, decide whether to (a) factor a shared
   `IInstalledModelDiscovery` interface backed by
   `ModelOperations.DiscoverModelsOnDisk`, or (b) keep the
   fix local to the model picker and leave other consumers
   on config-pinned (e.g. settings-page model management
   probably does want config-pinned because it lets users
   un-install / re-install per-model).

**Fix architecture (preliminary — refine after kickoff
verification):**

1. **Disk-truth model enumeration in the Runner picker.** Same
   pattern as MAC29: `ModelOperations.DiscoverModelsOnDisk(modelsRoot)`
   returns the set of `name:tag` strings, and
   `FindModelBlobForModel` resolves each to its primary blob
   for size/availability metadata. The picker shows what's
   on disk, regardless of whether config.Models has a pinned
   entry.
2. **Unification opportunity.** If Windows Runner's picker
   uses the same data source, the disk-truth swap fixes both
   automatically. If they diverged (likely — Windows Runner
   was written against the Windows path where pulls *do* write
   back to config), unify around the disk-truth path. MAC29's
   plaintext-config branch already exists; this just adds it
   to the Runner's model-selector code path.
3. **Optional: opportunistic config rebuild on unlock.** When
   the Runner unlocks an encrypted config and notices
   `config.Models` is empty but disk has models, write back to
   the encrypted config now (we have the unlocked
   `UnlockMaterial` in scope at unlock time). Then config and
   disk agree from that point forward. This is the Runner-side
   solution to the "Mac sidecar can't write back" problem from
   MAC29.

**Affected files (best guesses, confirm at kickoff):**
- Runner model picker source — TBD by grep.
- `prep-core/ModelOperations.cs` — already has
  `DiscoverModelsOnDisk` and `FindModelBlobForModel` from
  MAC29; no new API likely needed.
- New tests covering: encrypted config + model on disk →
  picker shows the model; multiple models on disk → all
  appear; unlock-time rebuild persists the disk-truth
  enumeration into config.

**Cross-OS review pass:**
- **Mac surfaces:** picker populates correctly on a
  Mac-prepped SSD. The intended end-to-end Mac flow finally
  works.
- **Windows surfaces:** Mac-prepped SSD plugged into a
  Windows machine — same fix, same story. Already-prepped
  Windows SSDs continue to work because their config.Models
  was being written back correctly; the disk-truth fallback
  just becomes redundant for them, not wrong.
- Bundle both OSes in one PR.

**Acceptance / smoke:**
- v1.4.x mac field run: prep SSD → pull `llama3.2:1b` → eject
  → unmount/remount → open Runner.app → unlock → model
  picker shows `llama3.2:1b` → select → send a chat → response
  arrives.
- Cross-OS roundtrip: same SSD plugged into Windows machine
  → Windows Runner shows the same model in its picker → chat
  works.
- Tests pin the disk-truth read on both encrypted and
  plaintext config paths.

### MAC34 - Mac Runner local chat works without Network Mode (auto-spawn sidecar + API key generation)

**Status:** **done** — PR #223 merged `e9c9a65` (2026-05-08), shipped in v1.3.13 cumulative bundle (2026-05-09). Two follow-ups filed from the v1.3.13 mac field test: **MAC34a** (Swift handshake regression — the `networkModeEnabled` toggle wasn't wired correctly into the C# sidecar startup gate) and **MAC34b** (port 11434 reclaim before staged ollama spawn). Closed the v1.3.12 mac field-test post-prep blocker: a small starter-model pull works, unlock works, but clicking Send returned `Chat failed: API key is required by configuration but not set on host.` and toggling Network Mode to "fix it" reproduced the same error. Two root causes stacked: (1) Mac chat is architecturally routed through the `mac-runner-host` sidecar (which is where the C# RAG pipeline lives) but the sidecar only ran when the user toggled "Network Mode" on — so local-only chat looked like it required LAN exposure even though that toggle is supposed to be optional. (2) The PrepApp shipped `networkApiKey: ""` with `networkRequireApiKey: true`, so the moment the sidecar came up, every non-loopback-ish request 503'd via the `RunnerLocalApiService` fail-closed guard; there was no UI to set a key.

**Scope:** medium — Mac Runner UI + lifecycle refactor + cross-OS PrepApp API key generation. Cross-OS: PrepViewModel.cs gets a parallel API-key generation pass; `RunnerLocalApiService` is untouched (no security policy change). Windows Runner runs runner-core in-process, so the auto-spawn architecture is Mac-only.
**Risk:** Low for the API key generation; medium for the Mac auto-spawn refactor (lifecycle gets coupled to unlock so any unlock-time crash now leaves both ollama and the sidecar in unknown states — Lock path mitigates by tearing both down).
**Dependencies:** None. Independent of MAC30/MAC31/MAC32.
**Goal:** A user who unlocks a Mac-prepped SSD can immediately send a chat without flipping any toggles. The Network Mode toggle survives but its semantics shift to "Expose API on LAN" — runtime control over the bind address only, with the sidecar always running on 127.0.0.1 by default.

**Driver:** v1.3.12 mac field test. Repro: prep SSD with `llama3.2:1b` → unlock Runner.app off the SSD → click Send → see "Chat failed: API key is required by configuration but not set on host" → toggle Network Mode on (chase the error) → same error. Pre-MAC34 the only way to make Mac chat work was to manually edit `portable-config.json` to set `networkApiKey` to some non-empty value, which was undocumented.

**Architecture:**

1. **Auto-spawn ollama + sidecar at unlock.** New private helper `ensureLocalChatStackRunning()` in `mac-runner/Sources/main.swift` is idempotent: starts ollama via `startOllamaIfNotRunning()` (a wrapper around the existing `startOllama()` that no-ops when `process != nil`), then calls `restartHostSidecar()` which stops any existing host and starts a fresh one bound to 127.0.0.1 (or `networkBindAddress` from config when the toggle is ON). Wired into `attemptUnlock` success and `loadConfig` non-encrypted path; `lockSession` tears both down before zeroizing the unlock material so the sidecar can never serve traffic with a wiped key.
2. **Toggle becomes "Expose API on LAN".** `setNetworkMode(enabled:)` / `startNetworkMode` / `stopNetworkMode` deleted; replaced with `setExposeApiOnLan(_:)` that just flips `networkModeEnabled` and calls `restartHostSidecar()`. Bind address in `restartHostSidecar` uses `configuredBind` only when the toggle is ON; otherwise hardcoded to `127.0.0.1`. The persisted PortableConfig field name stays `networkModeEnabled` for cross-OS schema compat. UI label changes from "Network Mode (LAN API)" to "Expose API on LAN".
3. **Start/Stop buttons removed.** With ollama auto-starting at unlock and stopping at lock, the manual buttons are dead UI. Removed entirely; the Lock button stays.
4. **API key generation at PrepApp first-write (cross-OS).** Mac PrepApp `EncryptedConfigWriter.swift` `InitialPortableConfigPayload.networkApiKey` defaults to a fresh 32-byte random hex via `SecRandomCopyBytes` (with a UUID-derived fallback). Windows PrepApp `PrepViewModel.FinalizeAsync` checks `string.IsNullOrWhiteSpace(config.NetworkApiKey)` after `LoadConfigAsync` and sets `Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()`. Idempotent — re-finalizing preserves an existing key so paired companions/clients don't break.
5. **Runtime API key backfill in Mac Runner.** Legacy field-test SSDs prepped pre-MAC34 have `networkApiKey: ""` baked into their encrypted config. `restartHostSidecar` checks the key and generates one inline (same `SecRandomCopyBytes` shape as the PrepApp helper) if empty, both updating the in-memory `portableConfig` so `apiKeyForLocalApiRequest()` agrees AND queueing a `saveConfig` so the key persists across Lock/Unlock. Runtime backfill obviates a "loopback-bypass-in-runner-core" approach that would have changed `RunnerLocalApiService` security posture and broken the existing `ApiKeyEnforcement_BlocksChatWithoutKey` test (which exercises a real loopback connection — exactly the scenario a bypass would silently pass).

**Affected files:**
- `runner-core/Services/RunnerLocalApiService.cs` — unchanged. (Considered + reverted a loopback bypass; runtime backfill in the Runner is cleaner and doesn't perturb auth tests.)
- Mac Runner: `mac-runner/Sources/main.swift` — `setNetworkMode`/`startNetworkMode`/`stopNetworkMode` deleted; `ensureLocalChatStackRunning` / `startOllamaIfNotRunning` / `restartHostSidecar` / `setExposeApiOnLan` / `generateRandomApiKeyHex` added; `lockSession` always tears down both ollama + sidecar; UI loses Start/Stop and renames the toggle.
- Mac PrepApp: `mac-prep-app/Sources/EncryptedConfigWriter.swift` — `InitialPortableConfigPayload.networkApiKey` default flips from `""` to `Self.generateRandomApiKey()`. Static helper added.
- Windows PrepApp: `shared/ViewModels/PrepViewModel.cs` — `FinalizeAsync` populates `config.NetworkApiKey` if empty. `using System.Security.Cryptography;` import added.
- Tests: `mac-prep-app/Tests/PrepAppTests.swift` — three new MAC34 cases (default 64-hex; differs between instances; explicit override wins). `tests/PrepViewModelTests.cs` — two new MAC34 cases (`FinalizeCommand_GeneratesNetworkApiKey_WhenEmpty`, `FinalizeCommand_PreservesExistingNetworkApiKey`). All Mac swift tests pass locally (19/19); C# tests deferred to Windows CI.

**Cross-OS review pass:**
- **Mac:** Runner picker auto-spawns chat stack on unlock; chat works with no toggles; Network Mode toggle becomes pure LAN-exposure UI. PrepApp future SSDs have a generated key from the first encrypted write.
- **Windows:** Runner is unchanged (runs runner-core in-process — the architectural mismatch is Mac-specific). PrepApp Finalize generates a key cross-OS so a Windows-prepped SSD has a ready-to-go API key for any future Mac use or Companion pairing. Latent Windows-side bug closed: per the existing TODO at `runner/MainWindow.xaml.cs:2176`, the Windows Runner has no UI to set Network Mode or the API key — users edit JSON. Pre-MAC34 a Windows user toggling Network Mode in JSON would 503 every request the same way the Mac field test did. PrepApp generation closes that latent trap too.

**Acceptance / smoke:**
- v1.3.13 mac field test: prep + pull `llama3.2:1b` → unlock Runner.app off the SSD → immediately type a prompt and click Send (no toggles) → response arrives. Click "Expose API on LAN" toggle ON → sidecar restarts, log shows the new bind address. Click Lock → ollama + sidecar both visibly stop in Activity Monitor.
- Cross-OS roundtrip: same SSD plugged into Windows machine → Windows Runner unlocks → existing chat works (Windows Runner is unchanged). Open `portable-config.json` (decrypted) → confirm `networkApiKey` is a 64-char lowercase hex string.
- Legacy SSD self-heal: an SSD prepped on v1.3.12 (empty key) opens correctly in v1.3.13 Runner; first unlock generates and persists a key; second unlock reuses the same key.

### MAC34a - Mac sidecar handshake hardcodes `networkModeEnabled = true` so chat survives toggle OFF

**Status:** **done** — PR #226 merged `95b62b5` (2026-05-09); slated for v1.3.14 hotfix bundle.
**Scope:** trivial — one-line Swift change + comment-only test update.
**Risk:** Low. Hardcoded value matches the documented MAC34 contract; persisted user intent unchanged.
**Dependencies:** Built on MAC34. Independent of MAC30/MAC34b.
**Goal:** Mac chat survives Lock/Unlock cycles regardless of the "Expose API on LAN" toggle state. Restore MAC34's "sidecar always runs after unlock" contract.

**Driver:** v1.3.13 mac field test screenshot — chat dead with "Chat host not running. Lock and unlock to restart.", lock/unlock not recovering. `<ssdRoot>/logs/macos-runner.log` showed every unlock crashed the sidecar with `Mac runner host crashed: RunnerLocalApiService did not start. Ensure networkModeEnabled is true before spawning mac-runner-host.`

**Root cause:** MAC34's documented contract said the toggle controls bind address only. But `restartHostSidecar` in `mac-runner/Sources/main.swift` still passed the toggle's runtime value as the `networkModeEnabled` field of the C# sidecar handshake. With the toggle OFF (the default after Lock), the C# `RunnerLocalApiService.StartAsync` early-returned at `if (!config.NetworkModeEnabled) return;` (`runner-core/Services/RunnerLocalApiService.cs:67`), `HostLifetime.StartAsync` threw on empty `CurrentBaseUrl` (`mac-runner-host/HostLifetime.cs:70`), host crashed.

**Fix:** Hardcode `config["networkModeEnabled"] = true` in the handshake. LAN exposure is now governed purely by `networkBindAddress` (loopback when toggle OFF, configured address when ON). C# inner gate stays as defense-in-depth — Windows pre-gates externally in `MainWindow.xaml.cs:470` so it's never reached there with false either.

**Why not the alternative — change C# to ignore `networkModeEnabled` on the Mac sidecar:** `RunnerLocalApiService` is shared between Mac and Windows; introducing a Mac-specific branch splits the contract for one downstream consumer. Hardcoding true at the Swift→C# boundary is one line of code, preserves the C# contract, and keeps the existing `HostRunner_WithNetworkModeDisabled_FailsWithoutReadyLine` smoke valid as defense-in-depth.

**Affected files:**
- `mac-runner/Sources/main.swift` — `restartHostSidecar` always sets `networkModeEnabled = true`.
- `tests/MacRunnerHostSmokeTests.cs` — comment-only update on `HostRunner_WithNetworkModeDisabled_FailsWithoutReadyLine` clarifying its post-MAC34a defense-in-depth role.

**Workaround offered live during diagnosis:** the user could toggle "Expose API on LAN" ON to satisfy the broken gate. PrepApp default `networkBindAddress = 127.0.0.1` meant ON didn't actually expose anything to LAN — but the workaround didn't survive Lock/Unlock so the fix landed same-session.

**Acceptance / smoke (deferred to v1.3.14 mac field run):** prep SSD → unlock Runner.app → click Send (no toggles) → response arrives. Lock → unlock again without touching the toggle → Send still works. Toggle Expose API on LAN ON → log shows bind address swap; toggle OFF → bind back to loopback; chat works through both transitions.

### MAC34b - Mac runner reclaims port 11434 by killing PIDs holding it before launching staged ollama

**Status:** **in flight** — PR #227 open + CI green (2026-05-09); pending merge before v1.3.14 dispatch.
**Scope:** small — one new Swift static helper + one call site.
**Risk:** Low. Kill-by-PID via `lsof` is precise; SIGTERM → grace → SIGKILL is the standard escalation.
**Dependencies:** Independent of MAC34/MAC34a (different code path; same field-test driver).
**Goal:** Mac Runner auto-cleans up any preexisting ollama (Ollama.app or stray CLI server) that's holding port 11434 before launching its staged binary, with visibility for the user.

**Driver:** v1.3.13 mac field test log surfaced silent `Ollama exited with code 1` immediately after `Started ollama`, blocking the C# sidecar from reaching ollama. Root cause: user had Ollama.app + a stray CLI ollama already bound to 127.0.0.1:11434. User's quote: "I had two running and had no idea." Windows side-steps the same scenario via `OllamaLifecycleService.ResolvePort` (scans preferred+20). Mac can't port-shift because the C# sidecar handshake takes a fixed host URL — kill-and-reclaim fits Mac's lifecycle better.

**Architecture:**

1. **New static helper `terminateProcessesListening(onPort:log:)`** in `RunnerViewModel`. Runs `/usr/sbin/lsof -nP -t -iTCP:<port> -sTCP:LISTEN` to enumerate PIDs, then `Darwin.kill(pid, SIGTERM)` for each, sleeps in 50ms increments up to 600ms total grace, then `SIGKILL` anything still alive. Logs `Found N existing process(es) on port 11434 (PIDs ...)` so the user has visibility. No-op when nothing is bound.
2. **Call site:** invoked from `startOllama` after the trust-gate check + before `p.run()`. The kill scope is "what is holding *our* port," not "anything named ollama" — a sibling ollama serving a different model on a different port stays untouched.

**Cross-OS parity note:** Windows is unaffected — `OllamaLifecycleService.ResolvePort` scans preferred+20 already, side-stepping the conflict instead of resolving it. Implementation diverges because the underlying mitigation differs by platform; user-visible outcome converges (chat works after launch).

**Affected files:**
- `mac-runner/Sources/main.swift` — new static `terminateProcessesListening`, `pidsListening`; one call from `startOllama`.

**Acceptance / smoke (deferred to v1.3.14 mac field run):**
- Launch Ollama.app, then launch Runner.app off the SSD → log shows "Found N existing process(es) on port 11434 (PIDs ...)" → bundled ollama starts cleanly → chat works.
- Sibling-ollama edge: start a second ollama on port 11500, then launch Runner.app → log only mentions the 11434 PID; the 11500 process is untouched.
- No-op smoke: launch Runner.app with no preexisting ollama → `lsof` returns empty stdout → `Found N existing` message does NOT appear → bundled ollama spawns directly.

