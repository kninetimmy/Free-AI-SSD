# Project State

Last updated: 2026-04-20 (H2 — implementation complete, PR pending)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**H2** — `chore/h2-repo-hardening`. 5 commits on branch, ready to push + open PR.

## Recently shipped

- **PR #162 — X13 — merged `40f41fd` (2026-04-20).** `ChatResult` / `TranscriptionResult` discriminated unions across 13 files; `/voice/query` 503 routing fix; 12 new tests (449 total).

- **PR #161 — X12 — merged `49fe0a2` (2026-04-20).** SHA-256 verification runs against the `.part` temp file before `File.Move`; on mismatch temp is deleted and destination stays absent. 3 new tests.

## Next up

**After H2 merges:** F3 PrepApp 3-tab restructure (X21 complete — no longer blocking F3), then F4 / B2 / F2 / R1 Stage 2.

**RAG audit backlog:** X17–X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 → X15 (expanded) → X19 → X20 → X22 → X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred — workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU indicator) — slot around the RAG audit queue.

See `project_backlog.md` for full item details.

## Last session

2026-04-20 (H2 — implementation complete, PR pending) — Six-item housekeeping batch on `chore/h2-repo-hardening`. Fixes: `build.ps1` stale staged artifact cleanup; `SsdLogger` write lock (mirrors `CompanionLog`); `[SupportedOSPlatform("windows")]` on WMI methods in `SystemResources`, `DriveInspector`, `DriveService` (clears all CA1416 warnings — cascaded fix to `DriveService` required); GitHub Actions SHA-pinned (`checkout`, `setup-dotnet`, `cache`, `upload-artifact`, `download-artifact`); README test count 375→449 and tests/ TFM net8.0→net10.0; xUnit `.Result` → `await` in concurrent STT test. 5 commits, 8 files. 449 pass, 2 skip.

2026-04-20 (X13 — merged PR #162) — Introduced `ChatResult` / `TranscriptionResult` discriminated unions across 13 files. `/voice/query` 503 routing fix. 12 new tests (449 total).

## Open questions

_None._
