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

**Status:** planned
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

### MAC18 - Cross-platform prep compatibility docs

**Status:** planned
**Scope:** docs / release matrix
**Risk:** Low
**Dependencies:** MAC17 ships first so the matrix isn't aspirational.
**Goal:** Document the source/target compatibility matrix so users know
  which OS to run prep from for which target. Make NTFS-only-from-Windows
  and APFS-only-from-Mac explicit OS limits, not project gaps.

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

MAC0-MAC9 are merged. Runner-parity track is complete (Mac runs the same
RunnerCore business logic via `mac-runner-host` for chat, RAG, and library
management). MAC9 closed as a docs-only architecture lock-in for the
Swift thin-UI + .NET sidecar approach. The next Mac items split into
two tracks:

**Packaging / distribution** (sequence before broader public release):
- **MAC10a** - Windows PrepApp OS compatibility selector.
- **MAC10b** - Mac app icon + Info.plist polish.
- **MAC11** - Signing + notarization.

**Cross-platform PrepApp parity** (so a Mac-only user can prep + run
without owning a Windows machine):
- **MAC16** - Extract PrepApp core to `prep-core/` (parallels MAC3).
- **MAC17** - macOS PrepApp MVP (exFAT). Now unblocked from the
  encrypted-config side (MAC5 done) and from the prep-core side once
  MAC16 ships.
- **MAC18** - Cross-platform prep compatibility docs (after MAC17).

Beyond those, MAC12 (voice/TTS), MAC13 (HOTAS/PTT decision), and MAC14
(DCS import on Mac) remain as feature-parity items but aren't on the
critical path for a supported public Mac release.
