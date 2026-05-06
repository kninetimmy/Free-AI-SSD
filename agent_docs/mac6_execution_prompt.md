# MAC6 Execution Prompt

- Item: `MAC6 - Mac LAN API host + Companion compatibility + X4 web UI plumbing`
- Status: `approved`
- Saved: `2026-05-06`
- Recommended execution model: `claude-opus-4-7` or `gpt-5.4`

Architectural decision (locked in this prompt): MAC6 ships a **net8.0
sidecar host process** spawned by the Swift `mac-runner`. The host links
runner-core directly and reuses `RunnerLocalApiService` byte-for-byte; no
Swift port of the API surface. This is the explicit exit ramp MAC5 left
open. Encrypted-config IO stays Swift-authoritative — the sidecar receives
unlock material via stdin only, plaintext never touches disk on Mac. X4
SPA assets are NOT shipped in MAC6; only the static-file middleware
plumbing so X4 lands cross-platform when it ships.

Use the prompt below to resume in a fresh session.

```text
Implement MAC6 only in /Users/stephenelswick/Free-AI-SSD.

Start by reading:
- agent_docs/project_state.md
- agent_docs/project_arch.md (Security invariants, SSD runtime layout,
  Network API section)
- agent_docs/project_decisions.md (MAC1 baseline, 2026-05-05 cross-platform
  prep parity, 2026-05-05 MAC4 trust gate, 2026-05-05 MAC5 native Swift
  encryption — MAC6 is the explicit exit ramp that MAC5 left open)
- agent_docs/mac_project_backlog.md (MAC6 entry plus MAC7/MAC8 for context)
- agent_docs/mac_platform_dependency_audit.md (the Mac side dependency
  budget — MAC6 is allowed to introduce a net8.0 process on Mac; record
  the waiver explicitly)
- runner-core/Services/RunnerLocalApiService.cs (the full host — every
  endpoint, middleware, validation, auth path is already here and
  platform-neutral; do not fork it)
- runner-core/Services/IRunnerLocalApiService.cs
- runner-core/Services/IChatService.cs and ChatService.cs
- runner-core/Services/IModelManagementService.cs and
  ModelManagementService.cs
- runner-core/Services/ITtsProvider.cs, ITextToSpeechService.cs,
  ISpeechToTextService.cs (the Mac host will pass null/no-op providers
  for STT/TTS in MAC6 and gate the routes via existing config flags)
- runner-core/Services/MacOllamaLifecycleService.cs (the Mac trust-gated
  Ollama supervisor MAC6 will call into)
- runner/App.xaml.cs lines 29-79 (the Windows DI wiring MAC6 mirrors)
- runner/MainWindow.xaml.cs around the API-start path (search for
  IRunnerLocalApiService.StartAsync to find it; MAC6 needs the same
  startup contract on Mac)
- runner-cli/Program.cs and runner-cli/RunnerApiClient.cs (the
  cross-platform RunnerCli that MAC6 must connect to a Mac-hosted Runner
  unchanged)
- companion/CompanionRuntime.cs around /api/health and /api/voice/query
  call sites (the Windows Companion that MAC6 must connect to a
  Mac-hosted Runner unchanged)
- mac-runner/Sources/main.swift (the SwiftUI app that holds the unlocked
  PortableConfig today; MAC6 adds a Network Mode toggle + sidecar
  spawn/teardown here)
- mac-runner/Sources/SsdEncryption.swift (do not modify; unlock stays
  Swift-authoritative)
- shared/PortableConfig.cs (the Network* fields the host reads:
  NetworkModeEnabled, NetworkBindAddress, NetworkPort, NetworkApiKey,
  NetworkRequireApiKey, NetworkAllowTts, NetworkAllowRemoteStt,
  NetworkAllowRemoteVoiceQuery)
- .github/workflows/build.yml (the windows-build and mac-runner-build
  jobs MAC6 extends)
- agent_docs/project_backlog.md X4 entry (MAC6 ensures X4 plumbing is
  ready on Mac without itself implementing X4)

Goal:
A Mac-hosted Runner exposes the same Runner LAN API surface as Windows
(/api/health, /api/models, /api/chat, /api/chat/stream, plus 403/503-
gated /tts/*, /stt/transcribe, /voice/query). FreeAiSsd.RunnerCli and
the Windows Companion connect to a Mac-hosted Runner over LAN with the
same Bearer / X-API-Key handshake and identical request/response shapes.
Static-file middleware is wired in RunnerCore so X4 (when shipped) is
served from the same Kestrel on both platforms with no Mac-specific
code path.

Architectural decision (lock this in this PR's project_decisions.md
entry — this is the exit ramp MAC5 explicitly left open):
- The Mac host runs as a net8.0 sidecar process spawned by the Swift
  mac-runner. We accept a .NET runtime on Mac for the API surface
  because the alternative — porting RunnerLocalApiService + ChatService
  + multipart + streaming + RAG glue to Swift — is large, ongoing
  duplication for an active surface and would meaningfully slow MAC7
  and MAC8. Encrypted-config IO stays Swift-authoritative (MAC5 is not
  reverted; Swift hands unlock material to the sidecar over stdin).
- The Mac host links against runner-core directly. There is no fork of
  RunnerLocalApiService. Any platform behavioral difference (e.g. STT
  provider) is expressed by injecting a different DI implementation.

Repo context:
- runner-core is plain net8.0 and references shared/. It has no WPF
  dependency. The host can be a plain net8.0 console exe published
  self-contained for osx-arm64 (single-file, trimmed off — RunnerCore
  uses reflection-heavy ASP.NET Core minimal APIs, do not enable
  trimming without verification).
- The Companion already speaks the API (companion/CompanionRuntime.cs
  uses the same routes Windows-Runner serves). MAC6 does not change the
  Companion. The integration smoke is "Windows Companion -> Mac Runner
  works the same as Windows Companion -> Windows Runner."
- API key handshake: header is "Authorization: Bearer <key>" or
  "X-API-Key: <key>". RunnerLocalApiService.TryReadApiKey already
  handles both. /api/health is the only unauthenticated route. Do not
  change auth semantics on Mac.
- Bind address: NetworkBindAddress defaults to 127.0.0.1; LAN exposure
  requires the user to set a non-loopback address and accepts the
  warning. Preserve the existing WARNING log when the bind is non-
  loopback. Do not bind 0.0.0.0 by default.

Implement:

1. Mac host project (new).
   - Add `mac-runner-host/FreeAiSsd.MacRunnerHost.csproj`, plain net8.0
     console exe, references runner-core/FreeAiSsd.RunnerCore.csproj
     and shared/FreeAiSsd.Shared.csproj. Target framework net8.0; do
     not target net8.0-windows.
   - Add to FreeAiSsd.sln. Update agent_docs/project_arch.md project
     layout list.
   - The host's Main:
     - Reads ssdRoot + an unlocked PortableConfig + the resolved Ollama
       host URL from stdin as a single JSON line ("init handshake").
       After this point stdin is closed; the host continues reading
       stdout-side commands ("config-update", "shutdown") via a
       newline-delimited protocol described below.
     - Wires DI mirroring runner/App.xaml.cs but with Mac-appropriate
       services: SsdLogger, HttpClient, DocumentLibraryManager,
       EmbeddingClient, DocumentIngestor, ConfigStore (read-only on
       host side — the host never saves config; Swift owns saves),
       MacOllamaLifecycleService (already in runner-core),
       ModelManagementService, DocumentOperationsService, ChatService,
       a NoOpSpeechToTextService (new, in runner-core), a
       NoOpTtsProvider (new, in runner-core), and
       RunnerLocalApiService.
     - Calls RunnerLocalApiService.StartAsync with the handshake'd
       PortableConfig + Ollama host. On a "config-update" message,
       call StopAsync then StartAsync with the new config (the user
       toggled NetworkApiKey or similar). On "shutdown" or stdin
       closure, call StopAsync, dispose the service provider, exit 0.
     - Logs to logs/macos-runner-host.log via SsdLogger and also to
       stderr so the Swift parent can surface failures to the UI.
   - Critical: the host does NOT start its own Ollama. MacOllamaLife-
     cycleService is shared, but starting Ollama is the Swift app's
     responsibility (it already does this for direct chat). The host
     just reads ollamaHost from the handshake.

2. No-op providers (new, in runner-core).
   - `runner-core/Services/NoOpSpeechToTextService.cs` implementing
     ISpeechToTextService: IsModelLoaded returns false, InitializeAsync
     throws InvalidOperationException("STT is not available on this
     platform."), TranscribeAudioAsync returns
     TranscriptionResult.Failure("STT is not available on this
     platform."). The /stt/transcribe and /voice/query routes are
     already gated by NetworkAllowRemoteStt / NetworkAllowRemoteVoice-
     Query and return 403 when off; on Mac, the user simply leaves
     them off. If they enable them and call the route, the 503/500
     errors from the no-op are the surfaced behavior.
   - `runner-core/Services/NoOpTtsProvider.cs` implementing ITtsProvider
     with Current returning null. /tts/speak and /tts/stop already
     return 503 in that case; behavior matches.
   - Both classes ship in runner-core (not platform-gated) — they're
     useful in tests and in any future cross-platform host.

3. Swift sidecar lifecycle (mac-runner/Sources/main.swift).
   - Add a "Network Mode" toggle to RunnerViewModel (mirrors the
     Windows Network Mode UX in spirit; a checkbox plus a small status
     line that shows "API: http://127.0.0.1:41555" when running).
   - Add `MacRunnerHostController` (new file:
     mac-runner/Sources/MacRunnerHostController.swift):
     - Spawns `<ssdRoot>/mac/runner-host/FreeAiSsd.MacRunnerHost` as a
       Process (Foundation Process, Pipe for stdin/stdout/stderr).
     - Writes the init handshake JSON line to stdin
       ({ "ssdRoot": "...", "ollamaHost": "...", "config": {...full
       PortableConfig dictionary as decrypted in memory...} }).
     - Listens on stdout for "ready: <baseUrl>" and "log: <line>"
       events; relays log lines to the Swift status pane.
     - On Network Mode toggle off, app background, app terminate,
       Lock button, or VM deinit: write "shutdown\n" to stdin, give
       the child up to 2s to exit, then SIGTERM, then SIGKILL.
     - On unexpected child exit: surface "API host crashed: <stderr>"
       to the UI and keep the toggle off.
   - Path resolution: when running from a development build (not in
     mac/Runner.app), find the host binary at
     <repoRoot>/mac-runner-host/bin/Release/net8.0/osx-arm64/publish/
     FreeAiSsd.MacRunnerHost. When running from a packaged app, find
     it at Bundle.main.resourceURL.appendingPathComponent("Contents/
     Resources/runner-host/FreeAiSsd.MacRunnerHost"). Fail clearly if
     neither path resolves.
   - Plaintext invariant from MAC5 still holds: do not write the
     PortableConfig JSON to disk to pass it to the host. Stdin only.
     If the user has Network Mode enabled and locks the drive, the
     host shuts down (the unlock material is gone).

4. Static-file plumbing for X4 (RunnerLocalApiService).
   - Add static-file middleware before the api group:
     `app.UseDefaultFiles(); app.UseStaticFiles(new StaticFileOptions {
        FileProvider = new PhysicalFileProvider(wwwroot),
        RequestPath = "" });`
     where wwwroot resolves to <RunnerCore content root>/wwwroot if
     the directory exists, else skipped silently.
   - Add a stub `runner-core/wwwroot/.gitkeep` so the folder exists.
     Do NOT ship X4 SPA assets — that's the X4 backlog item.
   - Update FreeAiSsd.RunnerCore.csproj to copy wwwroot to output (so
     both Windows publish and Mac publish carry it).
   - Acceptance for this slice: GET /chat/ on a host with no SPA
     assets returns 404 (UseStaticFiles falls through cleanly); GET
     /chat/index.html on a host where the file exists serves it.
     Cover with a runner-core integration test that creates a temp
     wwwroot/chat/ directory + a placeholder index.html.

5. Build + packaging.
   - Update build.ps1 (Windows side): publish mac-runner-host for
     osx-arm64 self-contained, single-file, and stage into
     out/mac/runner-host/. The cross-platform ZIP should include
     this directory under mac/runner-host/.
   - Update .github/workflows/build.yml mac-runner-build job: build
     mac-runner-host on the macOS runner via `dotnet publish
     mac-runner-host/FreeAiSsd.MacRunnerHost.csproj -c Release -r
     osx-arm64 --self-contained -p:PublishSingleFile=true`, then
     bundle the published binary into mac/Runner.app/Contents/
     Resources/runner-host/. The Swift build step stays as-is.
   - The trust-gate from MAC4 covers Ollama; the host binary itself
     is part of the Free-AI-SSD app payload, so it inherits the app's
     code-signing posture and does not need a separate attestation
     in this PR.

6. Tests.
   - runner-core integration tests:
     - StartAsync on 127.0.0.1 with NetworkRequireApiKey=false serves
       /api/health, /api/models, /api/chat with a fake IChatService.
       Already substantially covered for Windows; add cross-platform
       test (no [WindowsOnly] gate) and confirm it runs on the
       mac-runner-build job too via `dotnet test runner-core/...`.
     - StartAsync with NetworkRequireApiKey=true: /api/chat without
       a Bearer/X-API-Key returns 401; with the right key returns
       200; constant-time-equals path covered.
     - /chat/ static-file serving: returns 200 with placeholder
       index.html present, 404 with no SPA assets.
   - mac-runner-host smoke (Mac CI only):
     - Spawn the published binary, write a known handshake JSON to
       stdin, hit /api/health, hit /api/chat with a stub ChatService
       (use a test-only DI override via a `--test-mode` arg that
       installs a fake IChatService returning canned responses).
       Assert exit 0 on shutdown.
   - RunnerCli integration test: point RunnerApiClient at the Mac-
     spawned host (osx-arm64 only on the Mac CI job) and verify
     /api/health, /api/models, /api/chat, /api/chat/stream all work
     with the same wire shapes as Windows.
   - Companion handshake smoke: this is hard to automate cross-
     platform. Add a TODO and call it out as a manual gap (mirrors
     the MAC5 manual smoke pattern).

Constraints:
- Do not fork RunnerLocalApiService. The Mac host must reuse the
  runner-core implementation byte-for-byte. Any divergence is a bug.
- Do not start MAC7 (RAG parity) or MAC8 (doc management). The
  DocumentLibraryManager is constructed for ChatService dependency
  reasons, but the Mac host serves chat without a populated library
  (RAG-off path) in this PR.
- Do not implement X4. Only the static-file plumbing.
- Do not weaken the loopback default. The Mac host must default to
  127.0.0.1 when NetworkBindAddress is unset, with the same
  non-loopback warning the Windows host emits.
- Do not weaken the constant-time API key compare or the Bearer/
  X-API-Key handshake.
- Do not write plaintext PortableConfig to disk on Mac. Stdin
  handshake only. The host process holds it in memory and never
  persists it.
- Do not modify mac-runner/Sources/SsdEncryption.swift or the C#
  SsdEncryption format. Unlock stays Swift-authoritative.
- Do not bundle a third-party Swift HTTP package (no Vapor, no
  Hummingbird). The whole point of the sidecar is to reuse the C#
  implementation.

Acceptance criteria:
- mac-runner-host publishes for osx-arm64 self-contained and lands
  in mac/Runner.app/Contents/Resources/runner-host/ in the packaged
  artifact.
- Swift mac-runner can spawn the host on Network Mode toggle, hand
  off the unlocked config via stdin, and tear it down on Lock / app
  exit / app background.
- /api/health, /api/models, /api/chat, /api/chat/stream all serve
  the same wire shapes as the Windows Runner.
- Bearer / X-API-Key handshake works identically on Mac.
- FreeAiSsd.RunnerCli connects to a Mac-hosted Runner without code
  changes (verified in CI on osx-arm64).
- Windows Companion connects to a Mac-hosted Runner without code
  changes (manual smoke gap noted in the PR if not automatable).
- /chat/ static-file middleware is wired in RunnerCore. With a
  placeholder index.html present, both Windows and Mac Kestrel
  serve it; with no assets, both return 404 cleanly.
- No-op STT/TTS providers cleanly produce 403/503 when the user
  enables those flags on Mac without a real provider.
- All existing tests pass. MacPlatformBoundaryTests still asserts
  shared/runner-core dependency budgets (the new mac-runner-host
  project is allowed net8.0 + ASP.NET Core minimal APIs; record
  this in the audit doc).

Validation:
- dotnet build FreeAiSsd.sln -c Release
- dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal
- dotnet test runner-core/... (cross-platform integration tests)
- mac-runner-build CI job: publish mac-runner-host, run the Swift
  test binary, run the Mac-side host smoke test, build Runner.app
  with the host bundled.
- Manual-smoke gaps to call out in the PR description: Windows
  Companion -> real-Mac Runner over LAN; Mac Network Mode toggle
  with a real Ollama serving a real model.

GitHub workflow:
- Never push directly to main.
- Branch: `mac6-mac-lan-api-host`.
- Open a PR titled "[codex] MAC6 Mac LAN API host + Companion
  compatibility + X4 plumbing".
- Watch CI; on failure, push fixes to the same branch.
- Wait for explicit confirmation before merging.

After merge:
- Update agent_docs/mac_project_backlog.md MAC6 status to
  "done <date>" with an outcome paragraph mirroring MAC1-MAC5.
- Append a dated decision to agent_docs/project_decisions.md titled
  "MAC6 Mac net8.0 sidecar host: approved exit ramp from MAC5".
  Capture: the architectural choice (sidecar over Swift port), the
  rationale (reuse RunnerLocalApiService + ChatService + RAG glue;
  alternative Swift port would block MAC7/MAC8), and the boundary
  (unlock stays Swift-authoritative; unlock material flows to the
  host via stdin only; plaintext never touches disk on Mac).
- Update agent_docs/mac_platform_dependency_audit.md with the new
  exception (mac-runner-host is allowed net8.0 + ASP.NET Core
  minimal APIs + RunnerCore reference; everything else outside the
  host stays inside the existing budget).
- Update agent_docs/project_arch.md: add mac-runner-host to the
  project layout, document the sidecar lifecycle (spawn / stdin
  handshake / shutdown / Lock semantics), and update the Network
  API section to note Mac parity.
- Update agent_docs/project_state.md In flight / Recently shipped /
  Last session.
- README and QUICKSTART get a "Mac LAN API" section: same toggle,
  same handshake, Companion works.
```
