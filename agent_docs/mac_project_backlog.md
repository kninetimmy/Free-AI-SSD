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

**Status:** planned
**Scope:** planning / decision record
**Risk:** Low
**Goal:** Lock the minimum viable supported Mac release target before code
churn.

**Decisions to capture:**
- Minimum macOS version.
- Apple Silicon / Intel / universal build strategy.
- Supported filesystem expectations for shared Windows+macOS SSD use.
- Which features are required for "supported Mac" versus explicitly deferred.
- Whether the current Swift app remains a thin UI, or is replaced later.

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

**Status:** planned
**Scope:** codebase audit + tests/build guardrails
**Risk:** Medium
**Goal:** Make the portable-vs-Windows-only boundary explicit before moving
  Runner services.

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

**Tests:** Build/test on Windows; non-Windows shared/core build if introduced.

---

### MAC3 - Introduce platform-neutral Runner core

**Status:** planned
**Scope:** service extraction
**Risk:** Medium
**Goal:** Create a reusable home for Runner business logic currently tied to
  the WPF `runner/` project.

**Candidate services to extract or wrap:**
- `ChatService`
- `DocumentOperationsService`
- `RunnerLocalApiService` or its endpoint logic
- `ModelManagementService`
- platform-neutral Ollama lifecycle contract

**Likely files:**
- New project such as `runner-core/FreeAiSsd.RunnerCore.csproj`
- `runner/Services/*`
- `tests/FreeAiSsd.Tests.csproj`
- `FreeAiSsd.sln`

**Do not change:**
- WPF UI behavior.
- Existing Windows Runner public workflows.

**Acceptance criteria:**
- Windows Runner still works through the extracted service boundary.
- Core can build without WPF.
- Existing `ChatService` and API tests still pass.

**Tests:**
- Existing chat/RAG/API tests.
- New construction tests proving core services do not need WPF.

---

### MAC4 - macOS Ollama lifecycle + runtime trust gate

**Status:** planned
**Scope:** platform adapter
**Risk:** Medium
**Goal:** Start and stop `mac/tools/ollama/ollama` through shared/core logic
  with the same security posture as Windows.

**Likely files:**
- Runner core Ollama lifecycle abstractions.
- macOS implementation for `mac/tools/ollama/ollama`.
- `shared/Prereqs/MacToolCatalog.cs`
- `prep-app/Services/ArtifactStagingService.cs`

**Acceptance criteria:**
- Uses `OLLAMA_MODELS=<SSD>/models`.
- Binds to loopback.
- Refuses missing or unverified macOS Ollama payloads.
- Logs stdout/stderr and process exit.

**Tests:**
- Path resolution tests.
- Environment variable tests.
- Trust/manifest failure tests.

---

### MAC5 - macOS encrypted config unlock/save

**Status:** planned
**Scope:** Mac runtime integration
**Risk:** Medium
**Goal:** Bring `SsdEncryption` and `ConfigStore` into the Mac runtime path so
  encrypted SSDs are usable on macOS.

**Likely files:**
- `shared/SsdEncryption.cs`
- `shared/Services/ConfigStore.cs`
- Mac host/UI code
- tests for encrypted config roundtrip

**Do not change:**
- Encryption format.
- Windows unlock behavior.

**Acceptance criteria:**
- Mac can unlock an SSD encrypted by Windows PrepApp/Runner.
- Mac saves config changes back to encrypted config without plaintext leaks.
- Wrong password and corrupt metadata fail closed.

**Tests:**
- Cross-platform encrypted roundtrip.
- Wrong-password test.
- Save-after-unlock test.

---

### MAC6 - Mac local API host and RunnerCli compatibility

**Status:** planned
**Scope:** Mac host service
**Risk:** Medium
**Goal:** Run the Runner API on macOS for health, models, non-streaming chat,
  and streaming chat.

**Likely files:**
- Extracted `RunnerLocalApiService` / endpoint host.
- `runner-cli/*` tests against Mac-compatible host.
- Mac launcher/host wiring.

**Acceptance criteria:**
- `FreeAiSsd.RunnerCli` can connect to a Mac-hosted Runner API.
- `/api/health`, `/api/models`, `/api/chat`, `/api/chat/stream` work.
- API key behavior matches Windows.

**Tests:**
- API endpoint tests.
- RunnerCli streaming/non-streaming tests.

---

### MAC7 - RAG parity

**Status:** planned
**Scope:** document-grounded chat
**Risk:** High
**Goal:** Mac chat uses the same RAG pipeline as Windows: embeddings, vector
  search, prompt packing, and citations.

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

---

### MAC8 - Mac document management

**Status:** planned
**Scope:** library CRUD + ingestion surface
**Risk:** High
**Goal:** Mac users can create/select libraries, add files/folders, sweep, and
  rebuild.

**Likely files:**
- `shared/Documents/*`
- Mac UI/host endpoints
- possibly Runner API document endpoints from `R1 Stage 2`

**Acceptance criteria:**
- Create/select library.
- Ingest PDF/TXT/MD/JSON/CSV.
- Rebuild/sweep works from stored SSD files.
- Oversized/unsupported files produce user-facing errors.

**Tests:**
- Library CRUD tests.
- Document ingest tests.
- SQLite WAL / external-drive smoke where feasible.

---

### MAC9 - Mac UI strategy decision

**Status:** planned
**Scope:** architecture decision
**Risk:** High
**Goal:** Decide the long-term UI path after the Mac host/core has proven the
  service boundary.

**Options:**
- Keep Swift as a thin native UI over local .NET host.
- Replace with Avalonia cross-platform Runner UI.
- Keep Mac support CLI-first longer.

**Acceptance criteria:**
- Decision recorded in `project_decisions.md`.
- The chosen path does not duplicate RAG/encryption/network logic.

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
- Build strategy for arm64/x64/universal is explicit.
- App bundle contains or can locate required host pieces.
- External SSD launch path is tested.
- Logs and failure messages are useful.

**Tests:**
- CI artifact validation.
- Manual clean-Mac launch smoke.

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

**Acceptance criteria:**
- Signed and notarized `Runner.app`.
- Clean Mac launch works without right-click workaround.
- Quarantine/Gatekeeper behavior documented.

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

### MAC15 - Supported Mac release docs

**Status:** planned
**Scope:** docs/release
**Risk:** Low
**Goal:** Replace beta caveats with a real supported Mac feature matrix once
  the minimum release criteria are met.

**Acceptance criteria:**
- README and QUICKSTART match actual Mac behavior.
- Unsupported/deferred features are named.
- Troubleshooting covers Gatekeeper, permissions, Ollama, encrypted drives,
  and external SSD filesystem guidance.

**Tests:** Doc review.

## Recommended First Step

Start with **MAC0 - Truth-in-docs + roadmap anchor**.

This is the safest first PR because the repo currently implies macOS parity
that the code does not provide. MAC0 should update README/QUICKSTART and, if
useful, link this backlog from `project_state.md` without changing runtime
behavior.
