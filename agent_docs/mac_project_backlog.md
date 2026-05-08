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

**Status:** planned 2026-05-08 (field-reported via v1.3.6 mac
  screenshot; replaces MAC25 as the actual blocker for first
  successful model pull on Mac).
**Scope:** medium — staging path + resolver + verification pipeline,
  with new tests pinning that the inner-Resources binary is what
  ships.
**Risk:** Medium — the fix changes which Mach-O the runtime executes
  and how its env propagates. Backward-compatible to Windows
  (untouched); changes Mac runtime behavior fundamentally.
**Dependencies:** None. Top of queue. User decision recorded:
  must NOT require user-managed Ollama install — the project
  bundles Ollama and the fix has to run a server self-contained,
  not via the user's own daemon.
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
