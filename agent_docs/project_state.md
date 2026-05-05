# Project State

Last updated: 2026-05-04 (MAC0 docs honesty pass; F3 merged to main; H3 deferred)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**Between tasks.** F3 is now merged on `main` via PR #164 / `953fb1b`. No implementation branch is currently in flight. Optional follow-up `H3` covers a manual Windows PrepApp / FTUE smoke pass if we want one more validation sweep before starting the next feature branch.

## Recently shipped

- **PR #164 — F3 — merged `953fb1b` (2026-04-21).** PrepApp now uses the 2-tab Models/Drive flow with merged-grid explicit selection, batch Remove semantics, FTUE re-target, Runner disabled-tooltip polish, and focused PrepViewModel regression coverage. `windows-build` passed; local validation stayed green at 454 passed / 4 skipped.

- **PR #163 — H2 — merged `c8570d2` (2026-04-20).** Six-item housekeeping batch: `build.ps1` stale-artifact cleanup; `SsdLogger` write lock; `[SupportedOSPlatform("windows")]` on WMI methods (cascaded to `DriveService`); GitHub Actions SHA-pinned; README test count + TFM refreshed; xUnit `.Result` → `await`. 449 pass, 2 skip.

- **PR #162 — X13 — merged `40f41fd` (2026-04-20).** `ChatResult` / `TranscriptionResult` discriminated unions across 13 files; `/voice/query` 503 routing fix; 12 new tests (449 total).

- **PR #161 — X12 — merged `49fe0a2` (2026-04-20).** SHA-256 verification runs against the `.part` temp file before `File.Move`; on mismatch temp is deleted and destination stays absent. 3 new tests.

## Next up

Decide whether to run `H3` now as a manual Windows smoke pass. Otherwise pick the next implementation branch from `F4`, `B2`, `F2`, or `R1 Stage 2`.

**RAG audit backlog:** X17–X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 → X15 (expanded) → X19 → X20 → X22 → X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred — workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU indicator) — slot around the RAG audit queue.

See `project_backlog.md` for full item details.

**macOS support track:** `agent_docs/mac_project_backlog.md` is the source of
truth for turning the current Swift macOS beta into a supported platform. MAC0
completed the docs honesty pass; next Mac item is MAC1 (define supported Mac
baseline).

## Last session

2026-04-21 (F3 merged — PR #164, `953fb1b`) — Merged the 2-tab PrepApp rewrite after `windows-build` went green. Shipped the merged-grid safety pass, FTUE tab retarget, Runner disabled-tooltip copy, and focused PrepViewModel coverage. Manual PrepApp / FTUE smoke remains deferred as `H3`, not a merge blocker.

2026-04-21 (F3 close-out) — Finished the merged-grid safety pass for PrepApp: configured/downloaded rows are no longer auto-selected, `Remove` now applies one chosen action to all checked rows, and the dead standalone `VerifyCommand` path is deleted. Added focused PrepViewModel tests for explicit selection, batch remove, clear selection, and download-skip-on-drive behavior. Full build + test suite are green. Manual PrepApp / FTUE smoke is deferred to backlog item `H3`; branch is ready for PR + CI.

## Open questions

_None._
