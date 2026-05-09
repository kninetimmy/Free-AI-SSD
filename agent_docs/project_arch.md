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
├── mac-runner-host/ — net8.0 console; Mac LAN API sidecar spawned by mac-runner (MAC6)
├── tools/FreeAiSsd.PrereqFetch/  — net8.0 CLI; CI prereq-bundle builder
├── mac-runner/      — Swift/SwiftUI macOS beta over local sidecar API (unsigned)
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

### Mac Runner sidecar host (`mac-runner-host/`) [MAC6, MAC34]
Net8.0 console exe spawned by the Swift `mac-runner` automatically when
the SSD is unlocked. Links `runner-core/` directly and reuses
`RunnerLocalApiService` byte-for-byte; the Swift parent owns Ollama
lifecycle and encrypted-config IO, the sidecar consumes both via a stdin
handshake. Self-contained single-file publish for `osx-arm64`; lives at
`<ssdRoot>/mac/runner-host/FreeAiSsd.MacRunnerHost` on a staged SSD.

**Lifecycle (MAC34: coupled to unlocked session, not the LAN toggle).**
- Auto-spawn — at unlock, `ensureLocalChatStackRunning()` in
  `mac-runner/Sources/main.swift` starts ollama (idempotently) and the
  sidecar bound to 127.0.0.1 by default. Wired into both `attemptUnlock`
  success and the plaintext `loadConfig` path. Pre-MAC34 the sidecar
  only ran when the user toggled Network Mode on; that toggle now
  controls bind address only.
- Spawn handshake — Swift writes one JSON line to stdin:
  `{ "ssdRoot": "...", "ollamaHost": "...", "config": { ...PortableConfig dictionary... } }`.
  The host parses, builds its DI container, calls
  `RunnerLocalApiService.StartAsync`, and emits `ready: <baseUrl>` on
  stdout once Kestrel is listening.
- Steady state — every log message from `RunnerLocalApiService` is mirrored
  to stdout as `log: <line>` so the Swift status pane can surface API
  events; stderr carries fatal failures.
- Bind-address change — flipping the "Expose API on LAN" toggle calls
  `restartHostSidecar()`: shutdown, mutate `networkBindAddress` (OFF
  forces 127.0.0.1, ON uses configured value), respawn with the new
  config dictionary.
- Config update — `config-update <json>` on stdin restarts the API with
  the new config (e.g., the user toggled `NetworkRequireApiKey`).
- Shutdown — `shutdown\n` on stdin, or stdin EOF, triggers a graceful
  `RunnerLocalApiService.StopAsync` followed by exit 0.
- Lock semantics — when the user locks the encrypted SSD, backgrounds
  the app, or terminates it, the Swift app shuts the sidecar AND ollama
  down BEFORE zeroing the in-memory `UnlockMaterial`, so neither process
  can outlive an unlocked session.

**API key backfill (MAC34).** If the unlocked config's `networkApiKey`
is empty (legacy v1.3.12-and-earlier SSDs), `restartHostSidecar`
generates a fresh 32-byte hex key inline, mirrors it into the in-memory
`portableConfig`, and persists it via `saveConfig`. Future SSDs are
prepped with a key set at first encrypted-config write (Mac
`EncryptedConfigWriter` + Windows `PrepViewModel.FinalizeAsync`), so the
runtime backfill is a self-heal path for legacy drives only.

**Plaintext-config invariant (preserved from MAC5).** The Swift app
must never write the in-memory PortableConfig dictionary to disk to pass
it to the sidecar. The handshake travels over stdin only. The host holds
the parsed config in memory for the lifetime of the process and never
persists it.

**X4 plumbing.** `RunnerLocalApiService` wires `UseDefaultFiles` +
`UseStaticFiles` in front of the `/api/*` group when a `wwwroot/`
directory exists. The runner-core project ships `wwwroot/.gitkeep` so
the directory flows to the Windows runner publish AND the Mac sidecar
publish on every build. When X4 ships SPA assets, both Kestrels serve
`/chat/index.html` from the same code path.

### RAG pipeline
Embeddings stored as binary BLOBs in SQLite (~60% smaller than JSON). Vector search uses `System.Numerics.Vector<float>` SIMD with cosine similarity threshold (default 0.3). Ingestion is parallel with bounded concurrency and per-file failure isolation (failures logged and skipped, not fatal).

### MVVM pattern
ViewModels inherit `BaseViewModel` from shared. UI logic lives in ViewModels, not code-behind. `IDialogService` abstracts `MessageBox` for testability.

## Security invariants

- **AES-256-GCM** config encryption (PBKDF2-SHA256, 210k iterations)
- **Ollama packages** verified by URL allowlist + SHA-256 digest before extraction; macOS payloads additionally require an arm64 Mach-O slice (Apple Silicon baseline). Per-platform on-SSD trust attestations under `windows/tools/ollama/` and `mac/tools/ollama/` gate runtime launch.
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

## Runtime layout (host-side, Mac)

Mac model pulls stage outside the SSD before merging in. The bundled
`ollama` CLI hardcodes 16 parallel chunk writers, which exFAT FSKit
on macOS 15+ cannot sustain — pulls collapse to ~5 % of network
bandwidth on a 1 Gb line. The fix (MAC35) routes the pull's
`OLLAMA_MODELS` to a host-side cache, then `OllamaModelStager`
merges sequentially to the SSD:

```
~/Library/Caches/FreeAiSsd/
  ollama-staging/         — Mac PrepApp + (future) Mac Runner pull staging
    manifests/...         — Ollama-shaped manifest tree (host APFS)
    blobs/sha256-<hex>    — full blobs (and `…-partial-N` mid-pull)
```

The merge is content-addressed and idempotent — re-running a pull
after cancellation reuses staged blobs and writes the SSD manifest
last so a torn merge is invisible to `DiscoverModelsOnDisk`. Windows
is unaffected (NTFS sustains the 16-chunk pattern fine) and pulls
direct to the SSD with no staging detour. Same shape as MAC34b's
`lsof`-vs-port-shift split: per-platform implementation, converged
user-visible outcome.

The staging cache is not auto-pruned today; it's safe to delete by
hand between sessions if disk pressure becomes an issue.

Network surface: Ollama is never exposed on LAN. `RunnerLocalApiService` is the
only network endpoint. Post-MAC6 it serves identical wire shapes on Windows
(in-process inside the WPF Runner) and macOS (the `mac-runner-host` sidecar
spawned by the Swift Runner). RunnerCli and Windows Companion connect to
either platform with the same Bearer / X-API-Key handshake; static-file
middleware in front of `/api/*` is wired the same way on both so X4's web
chat UI lands cross-platform when it ships.

## Known gaps / out of scope
- DOCX format not supported (PDF, TXT, Markdown only)
- IL-2 / War Thunder bindings parsers not implemented (DCS only)
- macOS Runner is unsigned and beta
- No integration tests for download or dependency-install workflows (unit-tested with mocks only)
