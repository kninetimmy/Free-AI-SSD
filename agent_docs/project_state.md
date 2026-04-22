# Project State

Last updated: 2026-04-21 (PR #166 merged; F4 Stage 1 shipped)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**Between tasks.** F4 Stage 1 is shipped via PR #166 / `166b8a2`. No implementation branch is currently in flight. `H3` remains deferred unless a regression from PR #165 or PR #166 pulls it forward.

## Recently shipped

- **PR #166 - F4 Stage 1 - merged from feature tip `34e5f5b` (2026-04-21).** PrepApp now owns first-run profile selection: the FTUE starts with the two-machine explainer, profile choice happens inline, Finalize writes `ActiveProfile` and applies `ProfileDefaults`, and Runner no longer blocks startup with the old required first-run profile dialog. `windows-build` passed; manual PrepApp FTUE visuals / older `ActiveProfile = null` SSD smoke is still pending.

- **PR #165 - F3 - merged from feature tip `2e14d67` (2026-04-21).** PrepApp's 2-tab rewrite shipped: merged Models + Drive tabs, unified model grid with explicit bulk selection, FTUE retarget, Runner disabled-tooltip polish, and focused regression coverage.

- **PR #163 - H2 - merged `c8570d2` (2026-04-20).** Six-item housekeeping batch: `build.ps1` stale-artifact cleanup; `SsdLogger` write lock; `[SupportedOSPlatform("windows")]` on WMI methods (cascaded to `DriveService`); GitHub Actions SHA-pinned; README test count + TFM refreshed; xUnit `.Result` -> `await`. 449 pass, 2 skip.

## Next up

Continue with `F4` Stage 2 if we stay on the same feature. Otherwise take the next implementation branch from `B2`, `F2`, or `R1` Stage 2. `H3` remains deferred unless a PR #165 or PR #166 regression appears and pulls it forward.

**RAG audit backlog:** X17-X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 -> X15 (expanded) -> X19 -> X20 -> X22 -> X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred - workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU indicator) - slot around the RAG audit queue.

See `project_backlog.md` for full item details.

## Last session

2026-04-21 (F4 Stage 1 shipped - PR #166, merge `166b8a2`) - Implemented the PrepApp-owned profile FTUE on `feat/f4-stage1-prepapp-profile-ftue`, verified with release build plus targeted and full tests, opened PR #166, watched CI, and merged after `windows-build` passed. Final local verification was 458 total tests: 454 passed, 4 skipped. Manual smoke remains pending for PrepApp FTUE visuals and an older SSD where `ActiveProfile` is null.

2026-04-21 (F4 kickoff planning + handoff) - Confirmed PR #165 merged and closed F3. Intentionally moved `H3` down the queue, drafted and approved the detailed `F4` plan, and saved the approved `F4` Stage 1 execution prompt at `agent_docs/f4_stage1_execution_prompt.md` for a fresh-session handoff. No code changes happened in that planning-only session.

## Open questions

_None._
