# Free-AI-SSD

**Plug in a drive. Ask your AI anything. No internet required.**

Imagine you're in VR, flying the F-18, and you can't remember the sequence to uncage an AIM-9. You reach for your HOTAS, key the mic, and ask your AI copilot. It answers with the exact buttons on *your* stick — not some generic keybind chart, but your actual X-56 layout — sourced from the aircraft manual sitting on the drive. No internet. No cloud. No subscription. Just a portable SSD with a local AI that actually knows your stuff.

That's what this project does. You prepare a drive once on a machine with internet access — download the AI models, load in your documents — then take that drive anywhere. Plug it into any Windows or macOS machine and you have a fully self-contained AI assistant that runs 100% offline. It can reference your own PDFs, manuals, and notes when it answers, so you get grounded responses instead of hallucinated guesses.

---

## Who This Is For

### The DCS pilot who just bought a new module
You dropped $80 on the F-14 and the manual is 900 pages. You don't need to read all of it. Load the PDF onto the drive, plug it in, and just ask: *"How do I do a case III recovery?"* or *"What's the TWS auto scan pattern?"* The AI pulls the relevant section from the actual manual and gives you an answer with citations. Study on your couch, quiz yourself before a sortie, or have it running on a second monitor while you fly.

### The VR flight sim player who can't see their keyboard
You're in VR in DCS or IL-2, mid-engagement, and need to deploy countermeasures but you've blanked on the keybind. You can't take your headset off to Google it. With the voice pipeline now built in, you speak your question and the AI answers back by voice — no headset off, no hands off the stick. Import your HOTAS bindings first and it'll answer with your actual button layout, not a generic chart.

### The ham radio operator in the field
You're camping, or deployed for emergency comms, and you need to reference a section of your radio manual or the band plan for a frequency allocation. No cell signal, no internet. Load your manuals and reference docs onto the drive beforehand, and you have an offline AI assistant that can pull answers from your own library.

### The person who just wants a private, portable AI
Maybe you don't trust cloud AI with your data. Maybe your workplace restricts internet access. Maybe you travel and want the same AI setup on every machine you sit down at. Prepare the drive once, and it works the same everywhere — your models, your documents, your config, no account needed.

### The field researcher or off-grid prepper
Load in first aid guides, plant identification references, survival manuals, equipment specs — whatever you need access to when there's no connectivity. The AI indexes it all and can answer questions against your library, offline, from a drive that fits in your pocket.

---

## Roadmap — What's Still Coming

### IL-2 and War Thunder Bindings Parser (Phase 2)
Bindings import currently supports DCS World only. IL-2 Sturmovik and War Thunder parsers are planned for Phase 2, pending example binding files.

### HOTAS Push-to-Talk Trigger (Phase 4)
A keybind on a HOTAS button that starts and stops voice recording without touching the keyboard. Intended for VR use where hands-free activation matters.

### Network Mode (Phase 5)
Run the AI on one machine, query it from another on the same local network. Runner exposes a local API endpoint — no cloud, no internet.

### Setup Profiles (Phase 6)
Mode selection at install time: **general use** or **flight sim mode**. Flight sim mode pulls Whisper, TTS, and the bindings importer. Base install stays lightweight for users who do not need sim tooling.

---

## Getting Started

### What You'll Need

- A portable SSD (or any external drive with enough space for your AI models — most need at least 4–8 GB)
- A Windows machine with internet access for the one-time setup
- That's it

### Download and Install

#### Stable release (recommended)
- Download **`Free-AI-SSD-win.zip`** from GitHub Releases.
- Extract anywhere on Windows.
- Run `FreeAiSsd.PrepApp.exe`.

#### Optional beta cross-platform bundle
- **`Free-AI-SSD-beta-crossplatform.zip`** includes macOS artifacts and enables macOS target prep options.
- macOS build is currently unsigned/not notarized — expect Gatekeeper prompts.

#### CI artifacts
- GitHub Actions artifacts are available for validation and testing.
- Prefer Releases for normal end-user use.

### Quick Start (Windows)

1. Open `FreeAiSsd.PrepApp.exe`.
2. Select your target external SSD.
3. Add or select models in **Model Manager**.
4. Pull models.
5. Run **Check SSD Readiness** until checks are acceptable.
6. Click **Finalize SSD**.
7. Move the SSD to the destination machine.
8. Run Runner from the SSD:
   - Windows: `<SSD>\windows\runner\FreeAiSsd.Runner.exe`
   - macOS (beta): `<SSD>/mac/Runner.app`

### How It Works (The Short Version)

There are two phases:

**Phase 1 — Prepare (online, once)**
- Run **PrepApp** on a machine with internet access
- Select your target SSD
- Download the AI engine, pick your models, and stage everything for offline use
- Finalize the drive

**Phase 2 — Run (offline, anywhere)**
- Plug the SSD into any target machine
- Run **Runner** directly from the SSD
- Chat locally — optionally load in your own documents (PDFs, text files, etc.) so the AI can reference them when answering

### Offline Usage

| Operation | Internet Required? |
|---|---|
| PrepApp download / pull / staging | Yes |
| Runner start / stop | No |
| Chat (local AI) | No |
| Reference Documents indexing and retrieval | No |
| Pull embedding model (if not already on SSD) | Temporarily yes |
| DCS Bindings Import | No |
| Voice input (Whisper transcription) | No (model download is once, online) |
| Text-to-speech (System / Piper) | No |

### Using Reference Documents

Runner includes a **Reference Documents** panel for document-grounded chat without internet access. This is what lets the AI actually reference your manuals and files instead of making things up.

**Supported file types:** `.pdf`, `.txt`, `.md`, `.json`, `.csv`

**Typical workflow:**
1. Start the AI engine in Runner.
2. In **Reference Documents**, create or select a library.
3. Add files directly (**Add files**) or attach watched folders (**Add folder**).
4. Run **Sweep folders now** to ingest new or changed files.
5. Run **Rebuild index** for a full re-index.
6. Ask a question in chat with the library selected.

**How citations work:**
- Retrieved chunks are injected into the prompt with inline citations such as `[manual.pdf p.12]` or `[notes.txt]`.
- The **Sources** list shows the distinct citations used in context.
- If no chunks meet the similarity threshold, the prompt notes "No relevant documents found" so the model responds honestly rather than guessing.

**Current limitations:**
- PDF extraction depends on embedded text layer quality. Scanned/image-only PDFs may extract poorly without prior OCR.
- DOCX is not currently supported.
- Retrieval is optimized for personal and small-to-medium libraries (up to ~10,000 chunks; a warning is logged if exceeded).

### DCS Bindings Import

Runner can read your DCS World controller bindings and load them into the document library so the AI knows your exact HOTAS layout. When you ask "how do I uncage my AIM-9?" it can answer with the button on *your* stick, not a generic keybind table.

**How to import:**
1. Open **Bindings Import** in Runner (requires an active document library).
2. Runner auto-detects your `Saved Games\DCS` folder. If detection fails, browse to it manually.
3. Click **Scan** — Runner lists every aircraft that has binding files.
4. Select the aircraft you want and click **Import**.
5. Runner reads each device's `diff.lua` file (stick, throttle, rudder pedals), merges them into one per-aircraft reference document, and writes it directly into your library.
6. Run **Rebuild index** in Reference Documents or wait for the next sweep to ingest the new files.

After import, you can ask things like: *"What button fires the gun on my F/A-18C?"* and the AI will pull from your own binding file.

**Supported:**
- DCS World stable and Open Beta (auto-detected)
- Any aircraft that has binding files in `Config/Input`
- Multi-device merging — stick, throttle, and rudder pedals all merged into one file per aircraft

**Not yet supported:** IL-2 Sturmovik and War Thunder (Phase 2 — see roadmap).

### Voice Input and Response

Runner includes a voice pipeline so you can speak your questions and hear the answers — entirely offline, no cloud.

**Speaking to the AI:**
1. Click the microphone button (or configure a keybind) to start recording.
2. Speak your question.
3. Click the button again to stop — Whisper transcribes the audio locally and either sends it automatically or places the text in the prompt field for review, depending on your `autoSendVoiceInput` setting.

**AI voice response:**
- Enable TTS in settings. When enabled, each AI response is spoken aloud after it finishes generating.
- Two engines available: **System** (Windows SAPI, built-in, no download required) and **Piper** (optional neural TTS — download `piper.exe` and a voice model into `windows/tools/piper/`).
- You can target a specific audio output device (e.g., route AI voice to your headset while system audio goes elsewhere).

**Whisper model sizes** — stored at `models/whisper/` on the SSD:

| Size | File | Approx. disk | Notes |
|---|---|---|---|
| Tiny | `ggml-tiny.bin` | ~75 MB | Fastest; lower accuracy |
| Base | `ggml-base.bin` | ~142 MB | Default; good for most use |
| Small | `ggml-small.bin` | ~466 MB | Better accuracy |
| Medium | `ggml-medium.bin` | ~1.5 GB | Best accuracy; requires more RAM |

The first time voice is used, Runner downloads the selected model automatically (internet required for that one step). After that it runs fully offline.

**Voice configuration** (in `config/portable-config.json`):

| Property | Default | Description |
|---|---|---|
| `whisperModelSize` | `Base` | Whisper model: `Tiny`, `Base`, `Small`, or `Medium` |
| `selectedMicrophoneDevice` | `null` | Microphone device name; `null` = system default |
| `autoSendVoiceInput` | `true` | When true, transcribed text is sent immediately; when false, it is placed in the prompt field for review |
| `ttsEnabled` | `false` | Enable text-to-speech for AI responses |
| `ttsEngine` | `system` | `system` (Windows SAPI) or `piper` (neural TTS) |
| `ttsVoiceName` | `null` | Voice name for the selected engine; `null` = engine default |
| `ttsRate` | `0` | Speech rate: `-10` (slowest) to `10` (fastest) |
| `ttsVolume` | `100` | Volume: `0` (silent) to `100` (max) |
| `ttsOutputDevice` | `null` | Audio output device name for TTS; `null` = system default |

### Troubleshooting

**Runner won't start / dependency warnings**
- Use Runner's **Re-run dependency check**.
- Ensure SSD includes `windows/tools/prereqs` and its manifest.
- If the prereq bundle is missing or invalid, reconnect to an online machine and run **Update Prereqs** in PrepApp.

**Missing embedding model while offline**
- Document indexing can fail if the embedding model is not on the SSD.
- Start the AI engine and click **Pull embedding model**.
- If fully offline, connect temporarily to download the model, then return offline.

**PDF citations seem wrong or sparse**
- Confirm the source PDF has a machine-readable text layer.
- For scans or image PDFs, run OCR externally before importing.

**.NET / runtime prerequisites on target machine**
- Runner can install staged prerequisites offline (Windows) when the bundle is valid.
- If install is blocked, refresh prerequisites from PrepApp while online and retry.

---

## Recent Improvements

- **2026-02-21**: **DCS Bindings Import** — reads DCS `diff.lua` binding files, auto-detects saved games folder, merges multi-device HOTAS inputs, and writes per-aircraft reference documents directly into the document library for RAG
- **2026-02-21**: **Voice pipeline** — offline speech-to-text via Whisper.cpp (Tiny/Base/Small/Medium); text-to-speech via Windows SAPI (built-in) or optional Piper neural TTS; configurable mic, voice, rate, volume, and output device
- **2026-02-19**: Initial Replit setup with .NET 8; build and test workflow configured
- **2026-02-19**: Comprehensive code review completed (architecture, security, code quality)
- **2026-02-19**: XML documentation comments added to all source files (shared, prep-app, runner, tests)
- **2026-02-19**: MVVM refactoring Phase 1 complete — base classes, 9 service interfaces, shared DTOs, `PrepViewModel`, 20 unit tests
- **2026-02-19**: MVVM refactoring Phase 2 complete — 9 service implementations, `MainWindow.xaml` data binding, `MainWindow.xaml.cs` simplified to ~95 lines

**RAG / performance improvements:**
- **Cosine similarity threshold** — RAG retrieval discards chunks below a configurable minimum cosine similarity score (default 0.3). Configurable via `minimumSimilarityThreshold` in `config/portable-config.json`.
- **Binary BLOB embedding storage** — Embeddings stored as raw binary BLOBs in SQLite instead of JSON text, reducing index size by ~60% and eliminating serialization overhead on every query. Existing indexes are migrated automatically on first open.
- **Parallel embedding ingestion** — Document ingestion embeds chunks concurrently under a bounded concurrency cap (default 4). Configurable via `maxEmbeddingConcurrency` in `config/portable-config.json`.
- **SIMD-optimized vector search** — Embeddings pre-normalized at write time so search reduces to a dot product. Dot product is SIMD-accelerated via `System.Numerics.Vector<float>` (built into .NET 8, no additional native dependencies). Top-K selection uses an O(N log K) priority queue instead of a full sort.
- **Runner service layer** — `MainWindow.xaml.cs` refactored from a 983-line monolith into a thin UI shell. Business logic now lives in four injectable, interface-backed services: `OllamaLifecycleService`, `ModelManagementService`, `DocumentOperationsService`, and `ChatService`.

---

## Technical Details

<details>
<summary>High-Level Architecture</summary>

Free-AI-SSD ships two desktop applications backed by a shared cross-platform library:

- **PrepApp** (Windows, WPF) — the preparation tool. Runs on an online machine to configure the SSD: selects drive, downloads and stages Ollama, pulls models, bundles prerequisites, and finalizes the layout.
- **Runner** (Windows WPF / macOS Swift) — the end-user tool. Runs directly from the SSD on the target machine. Starts Ollama, provides a chat interface, and optionally loads Reference Document libraries for grounded local retrieval.
- **Shared library** (`FreeAiSsd.Shared`, `net8.0`) — portable core logic used by both apps: encryption, trust policy, path guards, configuration, dependency checking, download management, and MVVM infrastructure.

The Runner's business logic is split into injectable services (no UI dependencies) to allow unit testing without a WPF host:

- `OllamaLifecycleService` — process start/stop, port resolution, trust validation
- `ModelManagementService` — installed model listing, sizing warnings, embedding model pull
- `DocumentOperationsService` — library CRUD, file ingestion, folder sweep, index rebuild
- `ChatService` — RAG-augmented prompt construction and Ollama `/api/generate` calls
- `DcsBindingsImportService` — DCS installation detection, aircraft scanning, batch binding import
- `WhisperSpeechToTextService` — Whisper.cpp transcription via `Whisper.net`; model download management via `WhisperModelManager`
- `SystemTextToSpeechService` — Windows SAPI TTS with optional NAudio device targeting
- `PiperTextToSpeechService` — Piper neural TTS (optional); spawns `piper.exe`, streams raw PCM through NAudio
- `AudioCaptureService` — microphone capture at 16 kHz/16-bit mono (Whisper's required format)

</details>

<details>
<summary>Key Differentiators</summary>

| Feature | Detail |
|---|---|
| **Hardened package trust** | Ollama downloads are validated against an allowlist of known-good URLs and verified with SHA-256 digest checks before execution |
| **Encrypted configuration** | Portable config is protected with AES-256-GCM (PBKDF2-SHA256, 210,000 iterations) |
| **Fail-closed write guard** | PrepApp blocks all writes to encrypted drives if encryption state is ambiguous — no partial writes |
| **Cross-platform shared core** | Shared library (`FreeAiSsd.Shared`) targets `net8.0` and builds on all platforms; GUI apps target Windows or macOS separately |
| **Staged offline dependencies** | Prerequisites (VC++, .NET runtimes) are bundled and SHA-256 validated so the target machine can be set up without internet |
| **Path traversal prevention** | `PathGuards` enforces sibling boundary checks with platform-aware case sensitivity |

</details>

<details>
<summary>SSD Directory Layout</summary>

Free-AI-SSD prepares a layout similar to:

```
config/              — portable config + runtime state
models/              — Ollama model store
models/whisper/      — Whisper speech-to-text model files (ggml-*.bin)
logs/                — app logs
docs/libraries/      — Reference Documents library files, manifests, index DB
windows/runner/      — Runner app payload
windows/tools/ollama/    — staged Ollama runtime
windows/tools/piper/     — optional Piper TTS binary and voice models (user-installed)
windows/tools/prereqs/   — offline prerequisite installers + manifest
mac/                 — beta macOS payloads and tools (when included)
cache/               — prep-time download cache
```

</details>

<details>
<summary>Project Structure and Shared Library Components</summary>

### Project Structure

| Directory | Target | Purpose |
|---|---|---|
| `shared/` | `net8.0` | Cross-platform shared library (`FreeAiSsd.Shared`) |
| `prep-app/` | `net8.0-windows` | WPF PrepApp |
| `runner/` | `net8.0-windows` | WPF Runner |
| `mac-runner/` | macOS | Swift-based macOS Runner |
| `tests/` | `net8.0` | xUnit test project (`FreeAiSsd.Tests`) |
| `docs/` | — | Documentation |

### Shared Library Components (`shared/`)

| File | Purpose |
|---|---|
| `DependencyChecker.cs` | Detects missing VC++ / .NET runtimes via registry + process checks |
| `DownloadManager.cs` | Resumable HTTP file downloads with progress callbacks |
| `DriveInspector.cs` | Enumerates candidate drives (removable + optionally fixed) |
| `ModelSizing.cs` | Maps model tags to RAM/VRAM/disk requirements for sizing warnings |
| `NetUtils.cs` | Port availability checking for Ollama port selection |
| `OllamaPackageTrustPolicy.cs` | URL allowlisting, SHA-256 digest verification, trust attestation |
| `PathGuards.cs` | Path traversal prevention (sibling boundary, case sensitivity) |
| `PortableConfig.cs` | JSON serialization for `portable-config.json` (models, port, timestamps) |
| `PrepDriveWriteGuard.cs` | Blocks PrepApp writes to encrypted drives (fail-closed model) |
| `PrereqInstallValidator.cs` | Validates bundled installer integrity (SHA-256) before execution |
| `PrereqManifest.cs` | Manifest of bundled prerequisite installers with hashes |
| `PrereqCatalog.cs` | Catalog of required Windows prerequisites (VC++, .NET) |
| `MacToolCatalog.cs` | macOS-specific tool URLs and paths |
| `ProcessRunner.cs` | Safe process spawning with argument lists (not string concatenation) |
| `RunnerFirstRunState.cs` | Persists first-run state (sizing warning dismissed, dependency prompt shown) |
| `SsdEncryption.cs` | AES-256-GCM config encryption with PBKDF2-SHA256 (210,000 iterations) |
| `SsdLayout.cs` | Canonical directory structure constants and creation |
| `SsdLogger.cs` | File-based logger writing to the SSD's logs directory |
| `SystemCompatibility.cs` | Detects GPU, CPU architecture, OS version for compatibility display |
| `SystemResources.cs` | WMI-based RAM and VRAM detection |
| `Documents/DcsBindingParser.cs` | Parses DCS `diff.lua` binding files into structured data and formats them as plain text for RAG |
| `Documents/DcsAircraftScanner.cs` | Scans `Config/Input` for aircraft folders and device `diff.lua` files |
| `Documents/DcsBatchProcessor.cs` | Batch-processes selected aircraft: merges devices, formats output, writes `.txt` files to the library |
| `Documents/DcsSavedGamesLocator.cs` | Auto-detects `Saved Games\DCS` and `Saved Games\DCS.openbeta`; supports manual path override |
| `Documents/DcsBindingModels.cs` | Data models: `DcsDeviceBindings`, `DcsAircraftBindings`, `DcsAxisBinding`, `DcsButtonBinding`, `DcsAxisFilter` |
| `Documents/DcsScannerModels.cs` | Data models: `DcsInstallation`, `DcsAircraftInfo`, `DcsDeviceInfo`, `DcsBatchSummary`, `DcsBatchItemResult` |

### MVVM Infrastructure (`shared/Mvvm/`, `shared/Services/`, `shared/ViewModels/`)

| Directory | Files | Purpose |
|---|---|---|
| `shared/Mvvm/` | `BaseViewModel.cs`, `RelayCommand.cs`, `AsyncRelayCommand.cs` | MVVM infrastructure (`INotifyPropertyChanged`, `ICommand` implementations) |
| `shared/Services/` | `IDriveService`, `IModelService`, `IOllamaPackageService`, `IPrereqService`, `IArtifactStagingService`, `IReadinessService`, `IEncryptionService`, `IDialogService`, `ILogService` | Service interfaces for dependency injection |
| `shared/Models/` | `PrepModels.cs` | DTOs: `ReadinessItem`, `ModelGridRow`, `StarterModelRow`, `PrepTargets`, `ModelRemoveChoice` |
| `shared/ViewModels/` | `PrepViewModel.cs` | PrepApp ViewModel with commands, properties, and service orchestration |

### Service Implementations (`prep-app/Services/`)

| File | Purpose |
|---|---|
| `DriveService.cs` | Wraps `DriveInspector`, `SystemResources`, `PrepDriveWriteGuard` for drive operations |
| `ModelService.cs` | Wraps `ModelOperations`, `PortableConfig`, `ModelSizingCatalog` for model lifecycle |
| `OllamaPackageService.cs` | Handles Ollama download, trust validation, extraction via `DownloadManager` + `OllamaPackageTrustPolicy` |
| `PrereqService.cs` | Manages Windows prerequisite staging, online updates, and bundle validation |
| `ArtifactStagingService.cs` | Handles Runner and macOS artifact deployment with availability checks |
| `ReadinessService.cs` | Runs comprehensive SSD validation checks with model integrity verification |
| `EncryptionService.cs` | Wraps `SsdEncryption` for config encryption operations |
| `DialogService.cs` | Centralizes MessageBox and custom dialog interactions with Window owner support |
| `LogService.cs` | Provides thread-safe logging to `ObservableCollection` and `SsdLogger` via Dispatcher |

### MVVM Design Decisions

- Service interfaces are in `shared/` (`net8.0`) for cross-platform testability.
- `PrepViewModel` is in `shared/` so it can be unit tested on Linux without WPF.
- `IDialogService` abstracts all `MessageBox`/dialog interactions.
- Service implementations in `prep-app/Services/` (`net8.0-windows`) delegate to existing utility classes.
- Moq 4.20.72 used for mocking in tests.
- `MainWindow.xaml.cs` reduced from ~1,800 lines to ~95 lines; all business logic moved to `PrepViewModel` and services.

</details>

<details>
<summary>Build System and Development Workflow</summary>

### Prerequisites
- Windows (for WPF projects)
- .NET 8 SDK

### Build and test (all platforms — shared + tests only)
```powershell
dotnet build shared/FreeAiSsd.Shared.csproj
dotnet build tests/FreeAiSsd.Tests.csproj
dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal
```

> Note: 1 test (`IsPathUnderRoot_WindowsBoundaryIsRespected`) is expected to fail on Linux — it tests Windows-specific path behavior.

### Full build (Windows only)
```powershell
dotnet restore FreeAiSsd.sln
dotnet build FreeAiSsd.sln -c Release
dotnet test FreeAiSsd.sln -c Release
```

### Stage Runner payload into PrepApp output
```powershell
./build.ps1 -Configuration Release -Runtime win-x64
```

### Run PrepApp from source
```powershell
dotnet run --project prep-app
```

### Key Dependencies
- xUnit 2.9.2 (testing)
- System.Management 8.0.0 (Windows system info)
- Moq 4.20.72 (mocking in tests)

</details>

<details>
<summary>Security Assessment</summary>

**AES-256-GCM encryption** — Portable configuration is encrypted with AES-256-GCM using PBKDF2-SHA256 at 210,000 iterations. This is consistent with current industry standards.

**Ollama package trust policy** — Downloads are validated against an explicit URL allowlist and verified with SHA-256 digest checks before execution. This prevents substitution or supply-chain attacks against the Ollama binary.

**Fail-closed write guard** — `PrepDriveWriteGuard` blocks all writes to encrypted drives if encryption state cannot be confirmed. Any ambiguity results in a write block, not a fallback.

**Path traversal prevention** — `PathGuards` enforces sibling boundary checks with platform-aware case sensitivity (case-insensitive on Windows, case-sensitive on macOS/Linux).

**Shell injection prevention** — `ProcessRunner` uses `ArgumentList` (not string concatenation) for all process spawning.

**No critical vulnerabilities identified** in the security review (2026-02-19).

**Known quality issues (non-security):**
- Silent exception swallowing in `SystemResources.cs`, `PrereqManifest.Load()`, and `RunnerFirstRunState.Load()` masks failures and complicates diagnostics. Caught exceptions should be logged before returning defaults.

</details>

<details>
<summary>Test Coverage</summary>

212 tests total across 17 test files. 1 Windows-specific path test (`IsPathUnderRoot_WindowsBoundaryIsRespected`) is expected to fail on Linux.

| Area | Tests | Status |
|---|---|---|
| `DcsBindingParser` | 44 | Covered |
| `DcsAircraftScanner` | 33 | Covered |
| `VectorIndexRetrieval` | 17 | Covered |
| `SsdEncryption` | 15 | Covered |
| `DocumentParser` | 14 | Covered |
| `DocumentIngestionSecurity` | 13 | Covered |
| `DocumentChunker` | 10 | Covered |
| `OllamaPackageTrustPolicy` | 9 | Covered |
| `RagPromptBuilder` | 7 | Covered |
| `PrepDriveWriteGuard` | 7 | Covered |
| `PrepViewModel` | 20 | Covered |
| `CitationBuilder` | 8 | Covered |
| `RagPipelineIntegration` | 5 | Covered |
| `ModelOperations` | 5 | Covered |
| `PathGuards` | 3 | Covered |
| `PrereqInstallValidator` | 1 | Covered |
| `DocumentHashDedup` | 1 | Covered |
| `DownloadManager` | 0 | Not covered |
| `DriveInspector` | 0 | Not covered |
| `SsdLayout` | 0 | Not covered |
| `SystemCompatibility` | 0 | Not covered |
| `PortableConfig` | 0 | Not covered |

High-risk workflows (downloads, dependency installation, Runner start/stop) are not yet covered. The service layer refactoring enables these to be tested without a WPF host.

</details>

<details>
<summary>Code Review Findings (2026-02-19)</summary>

### Architecture
- **Resolved**: PrepApp `MainWindow.xaml.cs` was a ~1,800-line monolith mixing UI state, I/O, downloads, encryption, and business logic. MVVM refactoring Phase 1 and Phase 2 are complete — the file is now ~95 lines; all logic lives in `PrepViewModel` and injectable services.
- **Good**: Clean separation of shared library from GUI concerns. Individual shared components are focused and well-bounded.

### Code Quality
- **Open**: Silent exception swallowing in `SystemResources.cs`, `PrereqManifest.Load()`, and `RunnerFirstRunState.Load()` masks failures. Recommend logging caught exceptions before returning defaults.
- **Good**: Consistent use of records and immutable data structures throughout the shared library.
- **Good**: Async/await used correctly throughout with proper `CancellationToken` propagation.

### Improvement Recommendations (Priority Order)
1. **Error handling**: Replace silent catch blocks with logged exceptions
2. **Test expansion**: Add integration tests for download, encryption, and model workflows
3. **Configuration validation**: Add schema validation for `portable-config.json`
4. **Service abstractions (Runner)**: Runner services have been extracted; further interface coverage can be added as needed

</details>

<details>
<summary>macOS Signing and Notarization (CI)</summary>

The workflow supports optional signing and notarization via repository secrets:

- `MACOS_CERT_P12_BASE64`
- `MACOS_CERT_PASSWORD`
- `APPLE_TEAM_ID`
- `APPLE_ID`
- `APPLE_APP_SPECIFIC_PASSWORD`
- `MACOS_SIGN_IDENTITY` (optional if derived from certificate)

Signing is currently disabled by default in CI (`MAC_SIGNING_ENABLED=false`).

</details>
