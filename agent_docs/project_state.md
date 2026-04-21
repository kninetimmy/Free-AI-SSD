# Project State

Last updated: 2026-04-21 (F3 implementation complete on feature branch; build/tests green)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**F3 — PrepApp 2-tab restructure + UX simplification.** Implementation is complete on `feat/f3-prepapp-3-tab-restructure` and ready for PR/CI. The branch now has the 2-tab PrepApp rewrite, merged-grid safety pass (explicit selection only; batch Remove semantics), FTUE re-target, Runner disabled-tooltip polish, and focused regression tests. `dotnet build FreeAiSsd.sln -c Release` and `dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal` both passed. Manual PrepApp / FTUE smoke is deferred as backlog item `H3` and is not blocking the PR.

## Recently shipped

- **PR #163 — H2 — merged `c8570d2` (2026-04-20).** Six-item housekeeping batch: `build.ps1` stale-artifact cleanup; `SsdLogger` write lock; `[SupportedOSPlatform("windows")]` on WMI methods (cascaded to `DriveService`); GitHub Actions SHA-pinned; README test count + TFM refreshed; xUnit `.Result` → `await`. 449 pass, 2 skip.

- **PR #162 — X13 — merged `40f41fd` (2026-04-20).** `ChatResult` / `TranscriptionResult` discriminated unions across 13 files; `/voice/query` 503 routing fix; 12 new tests (449 total).

- **PR #161 — X12 — merged `49fe0a2` (2026-04-20).** SHA-256 verification runs against the `.part` temp file before `File.Move`; on mismatch temp is deleted and destination stays absent. 3 new tests.

## Next up

Open the F3 PR, watch CI, and only ask for merge once checks are green. Manual PrepApp / FTUE smoke is deferred as `H3` unless review or CI finds a regression that pulls it forward. Then F4 / B2 / F2 / R1 Stage 2.

**RAG audit backlog:** X17–X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 → X15 (expanded) → X19 → X20 → X22 → X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred — workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU indicator) — slot around the RAG audit queue.

See `project_backlog.md` for full item details.

## Last session

2026-04-21 (F3 close-out) — Finished the merged-grid safety pass for PrepApp: configured/downloaded rows are no longer auto-selected, `Remove` now applies one chosen action to all checked rows, and the dead standalone `VerifyCommand` path is deleted. Added focused PrepViewModel tests for explicit selection, batch remove, clear selection, and download-skip-on-drive behavior. Full build + test suite are green. Manual PrepApp / FTUE smoke is deferred to backlog item `H3`; branch is ready for PR + CI.

2026-04-21 (F3 review + handoff refresh) — Confirmed F3 is still a 3-stage item but the feature itself is now "PrepApp 2-tab restructure + UX simplification." Updated stale naming/status in backlog/state/plan docs. Current worktree already contains the Stage 2 XAML rewrite plus parts of Stage 3 (FTUE retarget, Runner tooltip, docs), but a review pass found merged-grid follow-up still needed before push: default-selected downloaded rows can cause accidental re-downloads, `Remove` still acts on the first checked row, and `VerifyCommand` still exists in the VM even though the new UI no longer exposes it.

2026-04-20 (F3 Stage 1 — commit `26d9a14`) — VM command consolidation on `feat/f3-prepapp-3-tab-restructure`. Renamed `PullInstallCommand` → `DownloadCommand` (semantics: all checked rows, not .Take(1)); deleted `PullSelectedCommand` and `AddStarterModelsCommand`; auto-verify folded into `PullModelsAsync` (SHA mismatch deletes `.part` + logs). Added `ModelRow.Status`. Stage 2 (XAML rewrite) queued.

2026-04-20 (F3 planning) — No code commits. Planned F3 PrepApp restructure across plan mode + design iteration. Mid-planning pivots: (1) dropped sub-VM split after full read of `PrepViewModel.cs` (1,154 lines) revealed tight cross-cutting (`AppendLog`, `SetModelOperationState`, `EnsureWritable`, etc.) — monolithic VM retained; (2) consolidated 3 tabs → **2 tabs** (Models + Drive) after UX pass for non-technical users; (3) merged Starter Models + Configured Models grids into single Status-column grid; (4) auto-verify on download replaces standalone Verify button; (5) full verbage overhaul (Pull/Install → Download, Finalize SSD → Finish setup, etc.). Plan file fully rewritten. Branch `feat/f3-prepapp-3-tab-restructure` created.

2026-04-20 (H2 — merged PR #163) — Six-item housekeeping batch on `chore/h2-repo-hardening`. Fixes: `build.ps1` stale staged artifact cleanup; `SsdLogger` write lock (mirrors `CompanionLog`); `[SupportedOSPlatform("windows")]` on WMI methods in `SystemResources`, `DriveInspector`, `DriveService` (clears all CA1416 warnings — cascaded fix to `DriveService` required); GitHub Actions SHA-pinned (`checkout`, `setup-dotnet`, `cache`, `upload-artifact`, `download-artifact`); README test count 375→449 and tests/ TFM net8.0→net10.0; xUnit `.Result` → `await` in concurrent STT test. 5 commits, 8 files. 449 pass, 2 skip.

## Open questions

_None._
