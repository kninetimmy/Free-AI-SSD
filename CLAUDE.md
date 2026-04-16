# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Session continuity

Before starting work, read `agent_docs/project_state.md` to understand
where things stand. Pay special attention to "Stable decisions (don't
revisit)" — those are locked in and re-opening them wastes time.

If this session produces meaningful progress, new decisions, or a
natural stopping point, prompt me to run `/wrap-up` before context
gets heavy. Don't run it yourself — suggest it, and I'll decide.

If `agent_docs/project_state.md` doesn't exist yet, suggest I run
`/init-project-state` to bootstrap it.

## What This Project Is

Free-AI-SSD is a cross-platform offline AI assistant that runs entirely from an encrypted external SSD. A Windows WPF prep tool stages Ollama, models, and prerequisites onto the drive; a Windows WPF runner (or beta macOS Swift app) boots from the drive and provides a RAG-augmented chat UI with voice I/O (STT via Whisper.cpp, TTS via Windows SAPI or Piper), HOTAS PTT, DCS binding import, and a LAN API for a lightweight companion app on a second PC.

## Solution Layout

```
FreeAiSsd.sln
├── shared/          — net8.0 library; core logic shared by all hosts
├── prep-app/        — net8.0-windows WPF; drive staging & model download
├── runner/          — net8.0-windows WPF; main chat UI, voice, LAN API
├── companion/       — net8.0-windows WPF; tray/overlay LAN client
├── tools/FreeAiSsd.PrereqFetch/  — net8.0 CLI; CI prereq-bundle builder
├── mac-runner/      — Swift/SwiftUI macOS beta (unsigned)
└── tests/           — net8.0 xUnit suite (212 tests)
```

## Commands

```powershell
# Build entire solution
dotnet build FreeAiSsd.sln -c Release

# Run tests
dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal

# Run a single test class
dotnet test tests/FreeAiSsd.Tests.csproj --filter "FullyQualifiedName~DcsBindingParserTests"

# Full release build + publish (Runner, PrepApp, Companion, prereq bundle)
./build.ps1 -Configuration Release -Runtime win-x64

# Build shared only (cross-platform, no WPF)
dotnet build shared/FreeAiSsd.Shared.csproj
```

SDK version is pinned in `global.json` (8.0.204, `rollForward: latestFeature`).

## Architecture

### Shared Library (`shared/`)

All business logic lives here so it can be tested without WPF. Key namespaces:

- **Config** — `PortableConfig` (atomic JSON writes, AES-256-GCM encryption), `SsdLayout` (canonical path constants), `SsdEncryption`
- **Documents** — `DcsBindingParser`, `DcsAircraftScanner`, `DcsBatchProcessor`; PDF ingestion via PdfPig; RAG vector index (SQLite, binary BLOBs, SIMD cosine similarity)
- **Helpers** — `PathGuards` (traversal prevention), `ProcessRunner` (shell-injection-safe via `ArgumentList`), `DownloadManager` (resumable), `DriveInspector`
- **Prereqs** — `PrereqResolver` (upstream version discovery + SHA-256 verification)
- **UI/Theme** — Colors.xaml, Controls.xaml, Shadows.xaml, Typography.xaml (neumorphic dark theme injected into both WPF hosts)
- **Mvvm** — `BaseViewModel`, `RelayCommand`, `AsyncRelayCommand`, `IDialogService`

### Runner Services (`runner/Services/`)

Services are DI-registered and cover:
- `OllamaLifecycleService` — process start/stop
- `ChatService` — RAG-augmented streaming via Ollama `/api/generate`
- `DocumentOperationsService` — CRUD, ingestion, index rebuild
- `WhisperSpeechToTextService` — STT (Tiny/Base/Small/Medium ggml models)
- `SystemTextToSpeechService` / `PiperTextToSpeechService` — Windows SAPI or neural TTS
- `HotasInputService` — DirectInput joystick polling (SharpDX)
- `PttVoicePipelineService` — full PTT→record→transcribe→send→TTS loop
- `RunnerLocalApiService` — ASP.NET Core LAN HTTP host for Companion

### RAG Pipeline

Embeddings stored as binary BLOBs in SQLite (~60% smaller than JSON). Vector search uses `System.Numerics.Vector<float>` SIMD with cosine similarity threshold (default 0.3). Ingestion is parallel with bounded concurrency and per-file failure isolation (failures are logged and skipped, not fatal).

### MVVM Pattern

ViewModels inherit `BaseViewModel` from shared. Main UI logic is in ViewModels, not code-behind. `IDialogService` abstracts `MessageBox` for testability. DI via `Microsoft.Extensions.DependencyInjection`.

### Security Invariants

- **AES-256-GCM** config encryption (PBKDF2-SHA256, 210k iterations)
- **Ollama packages** verified by URL allowlist + SHA-256 digest before extraction
- **Path traversal** blocked by `PathGuards` (throw, don't swallow)
- **Shell injection** prevented via `ProcessRunner.ArgumentList` (never string concat)
- **Fail-closed** write guard on encrypted drives (`PrepDriveWriteGuard`)

Do not weaken these controls. If adding a new process launch or file operation, use `ProcessRunner` and `PathGuards` respectively.

## Test Coverage

Tests in `tests/` use xUnit + Moq. Heavily covered: `DcsBindingParser`, `DcsAircraftScanner`, vector retrieval, encryption, document ingestion, RAG pipeline. Currently no integration tests for download or dependency-install workflows — unit test those paths with mocks.

## SSD Directory Layout (runtime)

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

`SsdLayout` in shared is the single source of truth for all these paths — always use it rather than constructing paths manually.

## GitHub Push & Pull Request Workflow

When pushing changes to this repository, always follow this workflow:

1. **Create a pull request** rather than pushing directly to main. Use a clear title and summary describing what changed and why.

2. **Monitor CI checks** — after the PR is created, watch for check results:
   - If all checks pass: notify the user and ask whether to merge to main.
   - If any check fails: notify the user immediately with which check failed and why (if determinable), then ask whether they'd like to debug the issue and push an updated branch.

3. **Merging** — only merge to main after explicit user confirmation.

4. **On check failure and debug request** — investigate the failure, propose or apply a fix, push the updated branch, and re-enter the monitor step above.

## Known Gaps / Out of Scope

- DOCX format not supported (PDF, TXT, Markdown only)
- IL-2 / War Thunder bindings parsers not implemented (DCS only)
- Ollama is never exposed directly on LAN; `RunnerLocalApiService` is the only network surface
- macOS Runner is unsigned and beta
