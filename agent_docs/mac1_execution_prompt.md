# MAC1 Execution Prompt

Use this prompt to start the next macOS support planning pass after the
`mac-support-backlog` PR is merged or otherwise ready to build on.

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
- Apple Silicon / Intel support strategy, including whether artifacts are
  universal or architecture-specific.
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

Baseline proposal to evaluate and either adopt or adjust:
- Minimum macOS: macOS 14 Sonoma for the first supported Mac release.
- Architecture: Apple Silicon first, with universal artifacts only where the
  upstream payload already provides them reliably; Intel remains best-effort
  beta unless validated on real hardware.
- Filesystem: exFAT is the supported shared Windows + macOS SSD format;
  NTFS remains preferred for Windows-only full-runner use; APFS is Mac-only
  and not a shared Windows target.
- First supported Mac release requires encrypted config unlock/save, verified
  macOS Ollama start/stop, streaming and non-streaming chat, RAG citations,
  document library use, useful diagnostics, and honest unsigned/notarized
  packaging state.
- Deferred beyond first supported Mac release: voice/STT/TTS, HOTAS/PTT,
  DCS import UI, Companion split-PC workflows, and Windows-equivalent prep UI.
- UI stance: keep Swift as a thin native UI over a shared/local host after
  MAC3-MAC7 prove the host/core boundary.

Validation:
- Documentation review only.
- Run `dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal` only if
  code or project files are changed unexpectedly.

GitHub workflow:
- Never push directly to `main`.
- Use a feature branch for MAC1.
- Open a PR, watch CI, and report results.
