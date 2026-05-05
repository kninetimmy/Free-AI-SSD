# Project State

Last updated: 2026-05-05 (macOS support backlog merged; MAC1 next)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**Between tasks.** The macOS support docs/backlog work is merged via PR #170.
Next Mac work is MAC1: define the supported Mac baseline before starting code
extraction or runtime parity work.

## Recently shipped

- **PR #170 - macOS support backlog - merged `a1d63c2` (2026-05-05).** Added the macOS support track, corrected README / QUICKSTART so macOS is described as a limited Swift direct-Ollama beta, added the MAC1 execution prompt, and passed `windows-build`.

- **PR #167 - F4 Stage 1 wrap-up docs - merged `8ada5e4` (2026-05-05).** Resolved conflicts against latest `main` via branch merge commit `34d0a4e`; `windows-build` passed.

- **PR #168 - Linux strategy doc - merged `a93785b` (2026-04-27).** Added the staged Linux support strategy document from branch `codex/create-linux-support-strategy-document`.

## Next up

1. Start **MAC1** from `agent_docs/mac_project_backlog.md`: define the supported Mac baseline.
2. For non-Mac work, pick from `H3`, `F4` follow-up, `B2`, `F2`, or `R1 Stage 2`.

**RAG audit backlog:** X17-X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 -> X15 (expanded) -> X19 -> X20 -> X22 -> X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred -- workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

See `project_backlog.md` for full general backlog details. See
`agent_docs/mac_project_backlog.md` for the macOS support track.

## Last session

2026-05-05 (macOS support backlog merged - PR #170, `a1d63c2`) - Reconciled `mac-support-backlog` with latest `main`, resolved the dashboard conflict, added `agent_docs/mac1_execution_prompt.md`, pushed the branch, and opened PR #169. The draft PR could not be marked ready because the GitHub connector's ready-for-review mutation returned a schema error, so #169 was closed and recreated as non-draft PR #170. `windows-build` passed on both runs for head `702607e`; PR #170 merged to `main` as `a1d63c2`, and local `main` was fast-forwarded.

2026-05-05 (PR #167 conflict fix + merge - `34d0a4e` / `8ada5e4`) - Identified the conflicting open PR as #167 (`docs/f4-stage1-wrap-up`), not the current `mac-support-backlog` branch. Stashed the local dashboard edit, switched to the PR branch, merged `origin/main`, resolved conflicts in `agent_docs/project_state.md` and `agent_docs/project_backlog.md`, pushed merge commit `34d0a4e`, confirmed GitHub reported the PR mergeable, watched CI, then merged PR #167 as `8ada5e4`. Restored the original `mac-support-backlog` checkout and its uncommitted dashboard edit.

## Open questions

_None._
