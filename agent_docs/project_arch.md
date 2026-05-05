# Project Architecture

## Purpose
Cross-platform offline AI assistant that runs entirely from an
encrypted external SSD. A Windows prep tool stages Ollama, models,
and prerequisites onto the drive; a Windows runner (with a beta
macOS port) boots from the drive and provides a RAG-augmented chat
UI with voice I/O, HOTAS PTT, DCS binding import, and a LAN API
for a lightweight companion app on a second PC.

## Stack and versions
- **.NET 8** (`global.json` pinned to `8.0.204`, `rollForward: latestFeature`)
- **WPF** (`net8.0-windows`) for prep-app, runner, companion
- **Shared library** (`net8.0`, cross-platform, no WPF) — portable config,
  document, prerequisite, and helper logic
- **Runner core** (`net8.0`, cross-platform, no WPF) — platform-neutral Runner
  chat, RAG orchestration, document operations, model management, and LAN API
  endpoint logic
- **Swift/SwiftUI** — `mac-runner/` (beta, unsigned)
- **xUnit + Moq** — tests target `net10.0` via `tests/` (326 tests as of 2026-04-17)
- **PdfPig** — PDF ingestion
- **SharpDX** — DirectInput for HOTAS
- **ASP.NET Core** (in-proc) — Runner LAN HTTP host
- **Microsoft.Extensions.DependencyInjection** — DI in WPF hosts
- **SQLite** — RAG vector index (binary BLOB embeddings)

## Layout

```
FreeAiSsd.sln
├── shared/          — net8.0 library; core logic shared by all hosts
├── runner-core/     — net8.0 library; platform-neutral Runner services
├── prep-app/        — net8.0-windows WPF; drive staging & model download
├── runner/          — net8.0-windows WPF; main UI and Windows adapters
├── runner-cli/      — net8.0 CLI; headless REPL client for Runner's LAN API
├── companion/       — net8.0-windows WPF; tray/overlay LAN client
├── tools/FreeAiSsd.PrereqFetch/  — net8.0 CLI; CI prereq-bundle builder
├── mac-runner/      — Swift/SwiftUI macOS beta (unsigned)
├── tests/           — xUnit suite
├── docs/            — user-facing docs (Theme.md, QUICKSTART, images)
└── .github/         — workflows
```

## Key subsystems

### Shared library (`shared/`)
Namespaces:
- **Config** — `PortableConfig` (atomic JSON writes, AES-256-GCM), `SsdLayout` (canonical path constants), `SsdEncryption`
- **Documents** — `DcsBindingParser`, `DcsAircraftScanner`, `DcsBatchProcessor`; PDF ingestion via PdfPig; RAG vector index (SQLite, binary BLOBs, SIMD cosine similarity)
- **Io** — `FileOps` (`ReplaceWithRetry` — shared atomic-replace helper used by Config and Documents save paths; see decisions 2026-04-20)
- **Helpers** — `PathGuards` (traversal prevention), `ProcessRunner` (shell-injection-safe via `ArgumentList`), `DownloadManager` (resumable), `DriveInspector`
- **Prereqs** — `PrereqResolver` (upstream version discovery + SHA-256 verification)
- **UI/Theme** — Colors.xaml, Controls.xaml, Shadows.xaml, Typography.xaml (neumorphic dark theme, injected into both WPF hosts)
- **Mvvm** — `BaseViewModel`, `RelayCommand`, `AsyncRelayCommand`, `IDialogService`

### Runner core (`runner-core/`)
Platform-neutral Runner services and contracts:
- `ChatService` — RAG-augmented streaming via Ollama `/api/generate`; returns `ChatResult` discriminated union (Success / RagRetrievalFailed / Failure)
- `DocumentOperationsService` — document library CRUD, ingestion, active-library selection, and index rebuild orchestration
- `ModelManagementService` — installed model list, embedding model pull, first-run sizing warning state, and hardware sizing checks via `ISystemResourceProbe`
- `RunnerLocalApiService` — ASP.NET Core LAN HTTP endpoint logic for Companion / RunnerCli compatibility
- Contracts for platform adapters: `IOllamaLifecycleService`, `ISpeechToTextService`, `ITextToSpeechService`, `ITtsProvider`, and `ISystemResourceProbe`

### Windows Runner host (`runner/Services/`)
WPF-hosted Windows adapters and UI-facing services:
- `ConfigStore` (`IConfigStore`) — config save chokepoint; serializes saves via `SemaphoreSlim(1,1)`, caches `UnlockMaterial`, zeroes key on `LockSession()`
- `OllamaLifecycleService` — Windows Ollama process start/stop
- `WhisperSpeechToTextService` — STT (Tiny/Base/Small/Medium ggml models); returns `TranscriptionResult` discriminated union (Success / Failure)
- `SystemTextToSpeechService` / `PiperTextToSpeechService` — Windows SAPI or neural TTS
- `HotasInputService` — DirectInput joystick polling (SharpDX)
- `PttVoicePipelineService` — full PTT → record → transcribe → send → TTS loop
- `DcsBindingsImportService` — DCS binding import UI workflow support
- `WindowsSystemResourceProbe` — Windows RAM/VRAM probe behind the RunnerCore `ISystemResourceProbe` contract

### Runner CLI (`runner-cli/`)
Thin .NET 8 HTTP client against `RunnerLocalApiService`. No WPF/GUI deps —
runs on any `net8.0` host (SSH / Tailscale use case). REPL with slash
commands, NDJSON streaming. Not an in-process console host for Runner;
talks to a running Runner instance over HTTP. Binary ships alongside
Runner via `build.ps1`.

### RAG pipeline
Embeddings stored as binary BLOBs in SQLite (~60% smaller than JSON). Vector search uses `System.Numerics.Vector<float>` SIMD with cosine similarity threshold (default 0.3). Ingestion is parallel with bounded concurrency and per-file failure isolation (failures logged and skipped, not fatal).

### MVVM pattern
ViewModels inherit `BaseViewModel` from shared. UI logic lives in ViewModels, not code-behind. `IDialogService` abstracts `MessageBox` for testability.

## Security invariants

- **AES-256-GCM** config encryption (PBKDF2-SHA256, 210k iterations)
- **Ollama packages** verified by URL allowlist + SHA-256 digest before extraction
- **Path traversal** blocked by `PathGuards` (throw, don't swallow)
- **Shell injection** prevented via `ProcessRunner.ArgumentList` (never string concat)
- **Fail-closed** write guard on encrypted drives (`PrepDriveWriteGuard`)
- **Config writes** route exclusively through `ConfigStore`; plaintext write on an encrypted drive throws `InvalidOperationException` (fail-closed)

Do not weaken these controls. New process launches go through `ProcessRunner`; new file operations go through `PathGuards`.

## Runtime layout (SSD)

```
config/                   — portable-config.json (encrypted at rest)
models/                   — Ollama model store
models/whisper/           — ggml-*.bin STT models
docs/libraries/           — RAG library files, manifests, SQLite index
windows/runner/           — Runner executable
windows/tools/ollama/     — staged Ollama runtime
windows/tools/piper/      — optional Piper TTS + voice models
windows/tools/prereqs/    — offline .NET 8 + VC++ redist + manifest
mac/                      — beta macOS payloads
cache/                    — prep-time download cache
```

`SsdLayout` in shared is the single source of truth for these paths — always use it rather than constructing paths manually.

Network surface: Ollama is never exposed on LAN. `RunnerLocalApiService` is the only network endpoint.

## Known gaps / out of scope
- DOCX format not supported (PDF, TXT, Markdown only)
- IL-2 / War Thunder bindings parsers not implemented (DCS only)
- macOS Runner is unsigned and beta
- No integration tests for download or dependency-install workflows (unit-tested with mocks only)
