# Linux Support Strategy

## Executive Summary

The recommended path is an incremental Linux rollout, not a rewrite. Start by getting the existing portable core and `runner-cli/` working reliably on Linux, then add Linux Runner runtime services, then add Linux GUI, and only after runtime maturity add Linux drive-prep workflows.

Recommended direction:

- Keep `shared/` as the portable core and continue moving platform-independent logic there.
- Isolate platform-specific concerns (GUI, input devices, audio backends, drive formatting, OS prereq checks) behind explicit interfaces.
- Deliver Linux Runner runtime and CLI support before Linux Prep.
- Keep Windows WPF apps stable and unchanged while Linux support is developed in parallel.
- Treat Flatpak as a likely cross-distro GUI package, while also offering native Debian-family and Arch-family packaging for users who need deeper host integration.

## Current Repository Baseline

The current repository is Windows-first with a portable shared core.

- Solution composition is defined in `FreeAiSsd.sln`:
  - `shared/` (`FreeAiSsd.Shared`) — core logic
  - `runner/` (`FreeAiSsd.Runner`) — WPF GUI runner
  - `prep-app/` (`FreeAiSsd.PrepApp`) — WPF prep/staging tool
  - `companion/` (`FreeAiSsd.Companion`) — WPF companion client
  - `runner-cli/` (`FreeAiSsd.RunnerCli`) — .NET CLI client
  - `tools/FreeAiSsd.PrereqFetch/` — prereq fetch tooling used in CI
  - `tests/` — xUnit tests
- Build and staging flow is Windows-centric:
  - `build.ps1` publishes `runner/` with `net8.0-windows` and stages output into `prep-app/bin/.../net8.0-windows/runner-publish`.
  - `.github/workflows/build.yml` uses Windows build jobs as primary release path.
- Windows-specific projects and assumptions:
  - `runner/FreeAiSsd.Runner.csproj`, `prep-app/FreeAiSsd.PrepApp.csproj`, `companion/FreeAiSsd.Companion.csproj` all use `Microsoft.NET.Sdk.WindowsDesktop`, `TargetFramework=net8.0-windows`, and WPF settings.
  - `runner/` and `shared/` include Windows-centric packages/APIs (`System.Speech`, `NAudio`, `SharpDX.DirectInput`, registry checks, WPF namespaces).
  - `prep-app/Services/DriveService.cs` and related code use Windows-specific drive/format behavior and PowerShell formatting flows.
- Portable parts that are already good Linux candidates:
  - `runner-cli/` is plain `net8.0` and HTTP-based.
  - Core config, document parsing, vector indexing, path guards, and many service contracts are in `shared/`.
  - RAG/document pipeline (`shared/Documents/*`) is mostly OS-neutral.
  - Local API service behavior and security model (in `runner/Services/RunnerLocalApiService.cs`) can be retained with minimal platform changes.
- SSD layout is centrally defined in `shared/SsdLayout.cs`, but currently includes Windows-specific paths and executable assumptions that must be generalized for Linux.

## Linux Support Goals

- Run the assistant directly from the SSD on Linux.
- Reuse existing model storage and document libraries on the SSD where feasible.
- Preserve offline-first behavior (no new mandatory online runtime dependencies).
- Support Debian-family and Arch-family systems first.
- Keep Ollama loopback-only by default.
- Preserve Runner LAN API security controls (opt-in LAN bind, API-key behavior, no default exposure).
- Keep Windows behavior and release flow stable while Linux support ships incrementally.

## Non-Goals

- No full rewrite of the project for Linux.
- No internet exposure of Ollama or Runner local services by default.
- No promise to support every Linux distro in phase one.
- No assumption that Windows-only APIs can be cheaply shimmed.
- No platform scope beyond Windows and Linux in this document.

## Recommended Linux Product Shape

Target Linux deliverables:

1. **Linux Runner GUI**
   - Full chat/runtime UX with models, RAG, voice, and local API controls.
2. **Linux Runner CLI/headless**
   - `runner-cli/` remains a first-class headless client.
3. **Linux Prep / Drive Setup tool**
   - Added after Linux Runner runtime is proven in real usage.
4. **Linux Companion/client role (optional)**
   - If retained, should consume Runner LAN API similarly to existing companion behavior.
5. **Shared portable core**
   - `shared/` continues to own core config, security, RAG, and storage logic.
6. **Platform service adapters**
   - Distinct Windows and Linux adapters for OS-level behaviors.

## Architecture Strategy

### Core direction

- Keep `FreeAiSsd.Shared` portable and avoid embedding Linux-only behavior directly in core logic.
- Introduce interfaces in `shared/Services/` (or an equivalent platform-abstraction layer) for all OS-dependent capabilities.
- Keep UI projects thin by pushing behavior into reusable services/ViewModels.

### Required platform abstractions

Add/expand abstractions for:

- Process management (including process tree kill and stdout/stderr handling).
- Filesystem and drive inspection.
- Removable drive detection.
- Formatting/prep operations.
- Audio capture/playback device enumeration.
- TTS engine resolution and invocation.
- HOTAS/joystick input.
- GPU/system compatibility detection.
- Prerequisite detection and advisory messaging.
- Ollama binary resolution and executable naming.

### Boundary rules

- Shared code can branch on platform only inside focused adapter implementations.
- Avoid scattered `OperatingSystem.IsLinux()` branches across business logic.
- Keep all path and executable resolution through central helpers (extend `SsdLayout` and related resolvers).

## UI Strategy

### Option comparison

- **Avalonia**
  - Pros: mature .NET cross-platform desktop UI; strongest reuse potential for C# ViewModels/services.
  - Cons: requires UI rewrite from WPF XAML patterns; some control/theme migration work.
- **Web UI wrapper (local web app + embedded or default browser)**
  - Pros: highly portable, easier long-term UI parity across platforms.
  - Cons: large UI architecture shift, adds web front-end complexity.
- **.NET MAUI**
  - Pros: single framework ambition.
  - Cons: desktop Linux maturity and ecosystem fit remain less attractive for this repo’s current structure; high migration risk.
- **GTK/Qt native UI**
  - Pros: native Linux feel.
  - Cons: lower code reuse with current C# WPF codebase unless adding substantial interop or a full new app.

### Recommended path

- Build a **new Linux Runner GUI in Avalonia** while leaving existing WPF apps untouched.
- Reuse non-UI services and ViewModels where practical.
- Port Runner UX first; do not start with Prep UI.
- Treat the Linux GUI as a new host over shared/runtime services, not a direct WPF port.

## Packaging Strategy

Linux packaging should support three delivery modes:

1. **Flatpak (cross-distro GUI path)**
   - Good for broad install simplicity.
   - Must be validated against removable SSD and device-access constraints.
2. **Debian-family native package (`.deb`)**
   - Useful for users needing predictable host integration and simpler support docs.
3. **Arch-family native package (`PKGBUILD`/AUR-style)**
   - Useful for ecosystem-native installs and community packaging workflows.

Secondary/optional:

- **AppImage**: can be useful for no-install portable tests, but weaker for deep integration and permission modeling.
- **Tarball/manual bundle**: useful as a fallback for debugging or unsupported variants.

Packaging constraints:

- Packages should install launcher/runtime host components, not duplicate or relocate SSD data model unexpectedly.
- Preserve “runs from SSD + offline” behavior by default.
- Any bundled tool archives must keep existing trust rules (HTTPS + hash/manifest checks as applicable).

## Linux SSD Layout Strategy

Proposed Linux-aware layout (coexisting with current structure):

```text
<ssd-root>/
  linux/
    runner/
    tools/
      ollama/
      whisper/
      piper/
  windows/
    ...
  models/
  config/
  docs/
    libraries/
  logs/
  cache/
```

Key strategy points:

- Keep shared data folders (`models/`, `config/`, `docs/libraries/`, `logs/`, `cache/`) platform-neutral.
- Keep platform runtime payloads under platform-specific subtrees (`windows/`, `linux/`).
- Replace hardcoded Windows path assumptions with path resolvers based on current host OS.
- Add migration logic that is non-destructive and idempotent (create missing Linux dirs, do not rewrite unrelated trees).

## Debian-Family Support Plan

### Baseline target

- Initial targets: Debian stable, Ubuntu LTS, Linux Mint releases based on current Ubuntu LTS.
- Assumption: systemd-based userland and `apt` package manager available.

### Dependency assumptions

- Runtime dependencies should be split between:
  - bundled-on-SSD tools (project-managed)
  - host packages required for audio/device integration.
- Host package assumptions should be explicit (for example: ALSA/Pulse/PipeWire user-space tooling as needed by chosen audio stack).

### `.deb` strategy

- Provide a `.deb` that installs a launcher and host-side integration files.
- Keep SSD as source of truth for models/config/docs unless user explicitly chooses host-local mode.
- If first-run checks detect missing host dependencies, show actionable instructions rather than failing silently.

### Device/audio/GPU/filesystem considerations

- Add guidance for udev group membership or rules if joystick/microphone access is denied.
- Validate audio on common Debian-family defaults (PipeWire and PulseAudio compatibility layer).
- Support CPU-first fallback; treat GPU acceleration as optional and capability-detected.
- Handle mount options and execute permissions for external SSDs (especially non-native Linux filesystems).

### Testing matrix (Debian-family)

- Debian stable (current).
- Ubuntu LTS (current).
- Linux Mint (current LTS base).
- For each: CLI publish smoke test, GUI launch, SSD read/write sanity, Ollama lifecycle, RAG ingest/query, audio smoke, HOTAS probe.

## Arch-Family Support Plan

### Baseline target

- Initial targets: Arch Linux (current), and one widely used Arch-derived distribution.
- Assumption: `pacman` available; AUR workflows are acceptable for community-native distribution.

### PKGBUILD/AUR strategy

- Maintain PKGBUILD definitions with clear split between runtime package and optional tooling.
- Prefer deterministic source URLs/checksums for package build inputs.
- Publish an AUR-style flow after CLI + GUI runtime are stable.

### Rolling-release risk

- Expect more frequent dependency/API drift.
- Add quick compatibility checks in app startup diagnostics for known break points (audio backend, libc version mismatches, permission issues).

### Device/audio/GPU/filesystem considerations

- Same capability model as Debian-family, but with explicit rolling-release caveats.
- Validate joystick permissions and group/rule requirements.
- Keep GPU optional with robust CPU fallback and visible diagnostics.

### Testing matrix (Arch-family)

- Arch Linux (fresh install, current packages).
- Arch-derived distro (latest stable release).
- For each: package install, CLI/GUI launch, Ollama lifecycle, RAG flow, audio capture/playback, HOTAS detect + PTT bind test.

## Flatpak Plan

### Scope and placement

- Flatpak should primarily contain Linux Runner GUI host binaries and minimal runtime integration.
- SSD remains source of truth for models, docs, config, logs, and tool payloads where possible.

### Permissions and portals

Flatpak would likely need:

- Filesystem access to removable SSD mount paths (or portal-mediated access with persisted permission).
- Microphone access for STT.
- Audio output access for TTS/playback.
- Network access for loopback and optional LAN API mode.

### Risks and constraints

- Flatpak sandboxing can complicate direct removable-drive access patterns.
- HOTAS/joystick access may be constrained compared with native package installs.
- Bundled runtime/tool invocation from external media may require careful permission and path handling.

### Recommendation for sequence

- Do **not** make Flatpak the first Linux milestone.
- First stabilize native Linux runtime behavior (CLI + GUI + SSD access).
- Add Flatpak as a packaging layer once filesystem/device access expectations are validated.

## Feature-by-Feature Linux Impact Assessment

| Feature | Current likely Windows dependency | Linux replacement/approach | Difficulty | Recommended phase |
|---|---|---|---|---|
| Runner GUI | WPF (`net8.0-windows`) in `runner/` | New Linux GUI host (recommended Avalonia) over shared services | High | 4 |
| Runner CLI | Already `net8.0` in `runner-cli/` | Publish `linux-x64`, validate endpoint parity | Low | 2 |
| Ollama lifecycle | Windows exe path + trust gating assumptions | Linux binary resolution + same trust policy model | Medium | 3 |
| Model storage | Shared paths in `models/` | Keep shared model store semantics | Low | 3 |
| RAG document library | Mostly shared code | Reuse shared library manager/index flow | Low | 5 |
| PDF/TXT/MD/JSON/CSV ingest | Shared parser with filesystem assumptions | Reuse parser, validate path and encoding behavior on Linux | Low | 5 |
| SQLite vector/index storage | Shared SQLite usage | Reuse, test file lock behavior on Linux FS types | Medium | 5 |
| Whisper STT | Current service wiring in Runner | Provide Linux-compatible Whisper runtime loading path | Medium | 6 |
| Piper TTS | Current process/runtime wiring in Runner | Linux piper binary packaging + adapter | Medium | 6 |
| System TTS | `System.Speech` Windows-only | Linux TTS adapter (engine abstraction) | High | 6 |
| Microphone capture | NAudio-based assumptions | Linux audio capture backend abstraction | High | 6 |
| Audio output routing | Windows audio device APIs | Linux audio output selection abstraction | High | 6 |
| HOTAS/PTT | SharpDX DirectInput usage | Linux joystick backend (`/dev/input`/evdev or SDL-based wrapper) | High | 7 |
| DCS bindings import | Path conventions and file parsing | Keep parser, add Linux path discovery strategy | Medium | 7 |
| Drive detection | Windows drive inspector patterns | Linux mount/removable detection adapter | High | 8 |
| Drive formatting/prep | Windows format command path | Linux formatting workflow with strong safeguards | High | 8 |
| Prerequisite checks | Windows registry + redist checks | Linux dependency probe model (package/process capabilities) | Medium | 9 |
| LAN API | Already ASP.NET Core in-process | Reuse same service + security defaults | Low | 3 |
| Companion/client app | Current WPF app | Consider Linux client only after Runner API stability | Medium | 9+ |
| Build/release packaging | Windows-centric CI/workflow | Add Linux build/package jobs and artifact validation | Medium | 11 |

## Staged Implementation Plan

### Phase 0: Repo audit and platform boundary map

- **Purpose:** Establish exact Linux-impact map and avoid accidental rewrites.
- **Major tasks:**
  - Inventory Windows-specific APIs/usages by project.
  - Define service-abstraction backlog and ownership.
  - Define Linux SSD layout extension proposal.
- **Files/areas likely touched:** `agent_docs/`, `docs/`, architecture notes.
- **Acceptance criteria:**
  - Documented map of Windows-only dependencies.
  - Approved abstraction plan with clear interface boundaries.
- **Risks:** Missing hidden Windows assumptions in transitive dependencies.

### Phase 1: Make shared core Linux-clean

- **Purpose:** Ensure `shared/` builds and runs Linux-safe for non-UI logic.
- **Major tasks:**
  - Move/guard Windows-specific code paths out of shared core hot paths.
  - Add OS-aware path/executable resolution helpers.
  - Add tests for Linux path behavior and case sensitivity assumptions.
- **Files/areas likely touched:** `shared/`, `tests/`.
- **Acceptance criteria:**
  - `shared/` passes unit tests on Linux runners.
  - No unguarded Windows-only API calls in shared execution paths.
- **Risks:** Interface churn causing regressions in Windows hosts.

### Phase 2: Publish and validate RunnerCli on linux-x64

- **Purpose:** First usable Linux runtime client milestone.
- **Major tasks:**
  - Add `linux-x64` publish job for `runner-cli/`.
  - Validate API calls against existing Runner endpoints.
  - Add smoke tests for CLI usage on Linux.
- **Files/areas likely touched:** `runner-cli/`, CI workflow definitions, docs.
- **Acceptance criteria:**
  - Linux CLI artifact produced in CI.
  - Basic connect/chat/health operations verified.
- **Risks:** Network/API assumptions tied to Windows-only host behavior.

### Phase 3: Linux Ollama lifecycle and SSD layout support

- **Purpose:** Establish Linux runtime core without GUI rewrite yet.
- **Major tasks:**
  - Add Linux tool path constants and resolver logic.
  - Implement Linux Ollama process adapter and trust checks.
  - Validate loopback-only defaults and LAN API security behavior.
- **Files/areas likely touched:** `shared/SsdLayout.cs`, runtime services, config path resolution.
- **Acceptance criteria:**
  - Linux host can start/stop Ollama from SSD layout.
  - Trust gating still enforced before launch.
  - Loopback default preserved.
- **Risks:** Binary-distribution trust differences for Linux Ollama artifacts.

### Phase 4: Linux Runner GUI proof of concept

- **Purpose:** Validate practical Linux desktop UX path.
- **Major tasks:**
  - Build minimal Linux GUI shell (chat + status + start/stop Ollama).
  - Reuse existing service layer contracts where possible.
  - Define theme parity baseline (not full visual parity).
- **Files/areas likely touched:** new Linux GUI host project, shared ViewModel/service layer.
- **Acceptance criteria:**
  - POC launches on target distros.
  - Can run local prompt flow through Ollama.
- **Risks:** UI framework migration complexity and unexpected platform behavior.

### Phase 5: RAG/document library validation on Linux

- **Purpose:** Bring document workflows to parity.
- **Major tasks:**
  - Validate ingest/index/query for supported formats.
  - Verify library storage and SQLite behavior on removable SSD.
  - Add integration tests for rebuild and dedupe flows.
- **Files/areas likely touched:** `shared/Documents/`, runtime integration, tests.
- **Acceptance criteria:**
  - End-to-end RAG works on Linux with existing library model.
- **Risks:** Filesystem lock/permission differences across mount types.

### Phase 6: Audio/STT/TTS support

- **Purpose:** Restore voice workflows on Linux.
- **Major tasks:**
  - Implement Linux microphone capture adapter.
  - Implement Linux playback/output device selection adapter.
  - Integrate Whisper and Piper runtime handling.
  - Add system-TTS fallback abstraction if feasible.
- **Files/areas likely touched:** audio services, TTS/STT services, config handling, tests.
- **Acceptance criteria:**
  - Voice input transcription works.
  - Spoken output works with selectable engine/device behavior.
- **Risks:** Linux audio stack variability across distros/desktops.

### Phase 7: HOTAS/PTT support

- **Purpose:** Restore flight-sim control workflows.
- **Major tasks:**
  - Implement Linux joystick input backend.
  - Support button detection, bind persistence, and polling.
  - Validate PTT workflow with voice pipeline.
- **Files/areas likely touched:** input services, config, UI binding screens, tests.
- **Acceptance criteria:**
  - HOTAS detection and PTT record/send loop work on Linux test matrix.
- **Risks:** Device permission and input API fragmentation.

### Phase 8: Linux drive prep workflow

- **Purpose:** Add Linux-side staging and drive setup safely.
- **Major tasks:**
  - Implement Linux drive detection and safe target selection.
  - Implement guarded formatting/prep path with explicit confirmations.
  - Support SSD structure provisioning and artifact staging.
- **Files/areas likely touched:** prep tooling/services, shared drive abstractions, docs/tests.
- **Acceptance criteria:**
  - Linux prep can stage a drive that Linux Runner can boot from.
  - Safety guards prevent accidental host-disk targeting.
- **Risks:** Formatting safety and mount behavior differences.

### Phase 9: Debian/Arch packaging

- **Purpose:** Native distro packaging for operational support.
- **Major tasks:**
  - Create `.deb` package definitions and install scripts.
  - Create PKGBUILD/AUR packaging assets.
  - Document host dependency assumptions and troubleshooting.
- **Files/areas likely touched:** packaging directories/scripts, docs, CI.
- **Acceptance criteria:**
  - Native packages install and launch on baseline targets.
  - Uninstall path leaves SSD data intact unless explicitly requested.
- **Risks:** Dependency drift and maintainer burden.

### Phase 10: Flatpak packaging

- **Purpose:** Cross-distro GUI distribution layer.
- **Major tasks:**
  - Create Flatpak manifest and runtime config.
  - Validate permissions for SSD access, audio, optional network mode.
  - Document sandbox limitations and known caveats.
- **Files/areas likely touched:** Flatpak manifest/build scripts, docs, CI.
- **Acceptance criteria:**
  - Flatpak install works on target desktops.
  - Core Runner workflows function with documented permission setup.
- **Risks:** Sandbox restrictions for removable media and input devices.

### Phase 11: CI/release hardening

- **Purpose:** Make Linux support maintainable and regression-resistant.
- **Major tasks:**
  - Add Linux build/test/package validation jobs.
  - Add checksum and artifact manifest generation for Linux outputs.
  - Gate release creation on both Windows and Linux quality bars.
- **Files/areas likely touched:** CI workflows, release scripts, docs.
- **Acceptance criteria:**
  - Linux artifacts are reproducibly generated and validated in CI.
  - Windows release flow remains green and unchanged in behavior.
- **Risks:** CI runtime cost, flaky integration tests, matrix explosion.

## CI/CD Strategy

- Add Linux CI jobs in parallel with existing Windows jobs; do not replace Windows gates.
- Publish `runner-cli` for `linux-x64` early (Phase 2).
- Run unit tests on Ubuntu runners for shared and CLI-centric coverage.
- Add package validation jobs:
  - `.deb` lint/install smoke
  - PKGBUILD/AUR build smoke
  - Flatpak manifest build/sandbox smoke (later phase)
- Adopt deterministic artifact naming, for example:
  - `Free-AI-SSD-runnercli-linux-x64.tar.gz`
  - `Free-AI-SSD-runner-linux-x64-flatpak.bundle`
  - `free-ai-ssd_<version>_amd64.deb`
- Add checksums + signed manifest publication for release artifacts.
- Release gating:
  - Windows gates must stay required.
  - Linux gates become required only after reaching declared support milestone.

## Testing Strategy

### Automated

- Unit tests for shared abstractions and Linux adapters.
- Integration tests for:
  - SSD layout provisioning
  - offline mode startup (no network)
  - Ollama lifecycle start/stop/health
  - RAG ingest/query/rebuild flow
  - audio capture/playback contracts (where feasible in CI)
  - HOTAS detection contracts (mock + hardware-lab supplement)
- VM-based distro tests:
  - Debian-family matrix
  - Arch-family matrix
- Flatpak sandbox tests:
  - removable SSD access
  - microphone/audio access
  - optional LAN mode behavior

### Manual smoke checklist

- Launch Runner from installed package.
- Select SSD root and verify config/model/doc detection.
- Start Ollama; confirm loopback binding.
- Run chat prompt and RAG query.
- Perform voice input and spoken output test.
- Validate HOTAS button bind and PTT cycle.
- Toggle LAN mode and verify warning + auth behavior.
- Reboot/replug SSD and retest startup.

## Security and Trust Strategy

- Keep HTTPS-only download rules for fetched prerequisites/tools.
- Keep hash verification against vendor-published hashes when available.
- Record observed hashes and provenance in manifests/attestations.
- Maintain loopback-only default for Ollama and Runner API binding.
- Preserve Runner API auth behavior (API-key-required defaults where configured).
- Require explicit user opt-in for non-loopback LAN binds.
- Do not expose services publicly; no automatic internet-facing mode.
- Review Flatpak sandbox impacts on trust boundaries and storage paths.
- Treat device-access permissions (audio/input/removable media) as explicit, least-privilege requests.

## Risks and Open Questions

- GUI framework decision risk (Avalonia fit vs migration cost).
- Flatpak filesystem/device constraints vs SSD-centered workflow expectations.
- Audio stack fragmentation (PipeWire/Pulse/ALSA interactions).
- HOTAS device access and permission model consistency.
- GPU acceleration variability (drivers, CUDA/ROCm availability, fallback semantics).
- External drive mount options and execution permissions.
- Distro-specific dependency drift and long-term support burden.
- Packaging support overhead across native + sandboxed formats.

## Recommended First PRs

1. **PR 1: Platform boundary inventory and docs update**
   - Scope: add Linux boundary map and service-interface backlog docs.
   - Likely files: `docs/`, `agent_docs/` architecture notes.
2. **PR 2: Shared path/runtime resolver groundwork**
   - Scope: add OS-aware executable/path resolver abstraction without behavior change on Windows.
   - Likely files: `shared/SsdLayout.cs`, `shared/PortableConfig.cs`, new resolver interfaces/classes, tests.
3. **PR 3: Linux CLI publish in CI**
   - Scope: add `runner-cli` linux-x64 publish artifact and basic test job.
   - Likely files: `.github/workflows/build.yml`, `runner-cli/` docs.
4. **PR 4: Linux Ollama lifecycle adapter skeleton**
   - Scope: introduce Linux-capable lifecycle implementation behind interface; keep Windows default intact.
   - Likely files: runtime service layer, shared abstractions, tests.
5. **PR 5: Linux SSD layout extension (non-breaking)**
   - Scope: extend layout constants and migration helpers for `linux/` paths.
   - Likely files: `shared/SsdLayout.cs`, config/path tests.
6. **PR 6: Linux GUI host POC scaffold**
   - Scope: add new Linux GUI project with minimal startup and status panel only.
   - Likely files: new GUI project directory, solution updates, minimal shared service wiring.

Each PR should be small, independently reviewable, and keep Windows behavior unchanged.

## Final Recommendation

Adopt a staged Linux migration anchored on the existing shared core: first make the core Linux-clean, then ship Linux CLI/runtime support, then introduce a dedicated Linux GUI, and only after runtime confidence add Linux drive-prep and packaging layers. Treat Flatpak as an important distribution target, but not as the first implementation milestone. Keep Windows release stability as a hard guardrail throughout.
