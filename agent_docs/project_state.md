# Project State

Last updated: 2026-05-05 (PR #167 conflict fix merged; mac-support PR still next)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**macOS support docs/backlog branch:** `mac-support-backlog` is pushed to
origin at `e8d7b24` and ready for PR after reconciling with latest `main`.
It adds the macOS support backlog, marks MAC0 done, and corrects README /
QUICKSTART so macOS is described as a limited Swift direct-Ollama beta rather
than a full Windows Runner equivalent.

## Recently shipped

- **PR #167 - F4 Stage 1 wrap-up docs - merged `8ada5e4` (2026-05-05).** Resolved conflicts against latest `main` via branch merge commit `34d0a4e`; `windows-build` passed.

- **PR #168 - Linux strategy doc - merged `a93785b` (2026-04-27).** Added the staged Linux support strategy document from branch `codex/create-linux-support-strategy-document`.

- **PR #166 - F4 Stage 1 - merged `166b8a2` (2026-04-21).** Moved first-run profile setup into PrepApp.

## Next up

1. Sync/reconcile `mac-support-backlog` with latest `main`, then open its PR and let CI/review run.
2. Start **MAC1** from `agent_docs/mac_project_backlog.md`: define the supported Mac baseline.
3. For non-Mac work, pick from `H3`, `F4` follow-up, `B2`, `F2`, or `R1 Stage 2`.

**RAG audit backlog:** X17-X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 -> X15 (expanded) -> X19 -> X20 -> X22 -> X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred -- workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

See `project_backlog.md` for full general backlog details. See
`agent_docs/mac_project_backlog.md` for the macOS support track.

## Last session

2026-05-05 (PR #167 conflict fix + merge - `34d0a4e` / `8ada5e4`) - Identified the conflicting open PR as #167 (`docs/f4-stage1-wrap-up`), not the current `mac-support-backlog` branch. Stashed the local dashboard edit, switched to the PR branch, merged `origin/main`, resolved conflicts in `agent_docs/project_state.md` and `agent_docs/project_backlog.md`, pushed merge commit `34d0a4e`, confirmed GitHub reported the PR mergeable, watched CI, then merged PR #167 as `8ada5e4`. Restored the original `mac-support-backlog` checkout and its uncommitted dashboard edit.

2026-05-04 (MAC0 docs/backlog - `e8d7b24`) - Created `agent_docs/mac_project_backlog.md` as the ordered macOS support track, with MAC0 marked done and MAC1 as the next Mac step. Updated README and QUICKSTART to state that macOS is currently a limited Swift direct-Ollama beta, while RAG/citations, encrypted config unlock, Runner LAN API hosting, RunnerCli against a Mac host, voice/TTS, HOTAS/PTT, and DCS import remain Windows-only for now. Committed and pushed branch `mac-support-backlog`.

## Open questions

_None._
