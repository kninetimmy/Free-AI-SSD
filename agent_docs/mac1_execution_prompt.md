# MAC1 Execution Prompt

Historical prompt used to start the MAC1 macOS support planning pass.

MAC1 is now completed locally on `mac1-supported-mac-baseline`. Keep this file
as context for why the decision exists; do not reuse the old baseline proposal
without checking `agent_docs/project_decisions.md` first.

## Prompt

You are working in `/Users/stephenelswick/Free-AI-SSD`.

Start by reading:
- `agent_docs/project_state.md`
- `agent_docs/project_arch.md`
- `agent_docs/project_decisions.md`
- `agent_docs/mac_project_backlog.md`

Task: complete **MAC1 - Define supported Mac baseline**.

Scope:
- Planning / decision record only.
- Do not change runtime behavior.
- Do not start MAC2 dependency extraction or any Swift/.NET implementation.
- Keep all security invariants intact: AES-256-GCM encrypted config,
  SHA-256 plus URL allowlist for downloaded binaries, `PathGuards` for path
  handling, and `ProcessRunner.ArgumentList` for process launches.
- Do not duplicate RAG, encryption, or network API logic in Swift.

Decisions to record:
- Minimum supported macOS version for the first supported Mac release.
- Apple Silicon support strategy, including whether artifacts are universal or
  architecture-specific.
- Supported SSD filesystem expectations for Windows + macOS use.
- Required features for the first "supported Mac" release.
- Explicitly deferred Mac features.
- Long-term UI stance: current Swift app as thin UI over shared/local host,
  replacement later, or CLI-first interim path.

Expected outcome:
- Add a dated decision to `agent_docs/project_decisions.md`.
- Update `agent_docs/mac_project_backlog.md` so MAC1 is marked done and later
  MAC items reflect the baseline where needed.
- Update `agent_docs/project_state.md` with the new last-session summary and
  next Mac item.

Final baseline recorded by MAC1:
- Minimum macOS: macOS 11 Big Sur, the earliest macOS generation that runs
  production Apple Silicon Macs.
- Architecture: Apple Silicon only. Intel Macs are unsupported.
- Artifacts: Free-AI-SSD Mac app artifacts are arm64-only. Universal upstream
  tools may be consumed only when the arm64 path is verified.
- Filesystem: exFAT is the supported shared Windows + macOS SSD format; NTFS
  remains Windows-only; APFS is Mac-only and deferred until a Mac-native
  prep/staging workflow exists.
- PrepApp follow-up: add an OS compatibility selector that preselects NTFS for
  Windows only and exFAT for Windows + macOS.
- First supported Mac release requires encrypted config unlock/save, verified
  macOS Ollama start/stop, streaming and non-streaming chat, RAG citations,
  document library use, useful diagnostics, and honest packaging/signing state.
- Deferred beyond first supported Mac release: voice/STT/TTS, HOTAS/PTT,
  DCS import UI, Companion split-PC workflows, and Windows-equivalent prep UI.
- UI stance: keep Swift/SwiftUI as a thin native UI over shared/core services
  unless MAC3-MAC7 prove that path blocks parity or duplicates business logic.

Validation:
- Documentation review only.
- Run `dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal` only if
  code or project files are changed unexpectedly.

GitHub workflow:
- Never push directly to `main`.
- Use a feature branch for MAC1.
- Open a PR, watch CI, and report results.
