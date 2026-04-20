# Project State

Last updated: 2026-04-19 (v1.2.9 released — run 24654381970)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

Nothing — between tasks.

## Recently shipped

- **v1.2.9 released 2026-04-19** — `e385fff`. `Free-AI-SSD-win.zip` published (run 24654381970). Contains X24 (citation staleness fix) + X25 (shared `FileOps.ReplaceWithRetry`). Release: https://github.com/kninetimmy/Free-AI-SSD/releases/tag/v1.2.9

- **PR #155 — X24 + X25 — merged `e385fff` (2026-04-20).** `2f7dcd8` (X25): promoted private `ReplaceWithRetry` from `SsdEncryption.cs` to new `shared/Io/FileOps.cs`; all four `File.Replace` call sites now use the shared retry helper. `53ecdf9` (X24): added `VectorIndex.UpdateFileName` (parameterized UPDATE) and called it from the single-sha rename branch in `DocumentIngestor` — citations show current filename immediately after rename, no re-embed needed. Test written failing-first. 411/0/1.

- **v1.2.8 released 2026-04-20** — `4d269a7` + prior `af77abc`. `Free-AI-SSD-win.zip` published (run 24650221521). Contains X10 Stages 1–3 + CI `File.Replace` retry hardening (PR #153). Release: https://github.com/kninetimmy/Free-AI-SSD/releases/tag/v1.2.8

## Next up

**X21 — embedding provenance + compat gating** (Sonnet, small). Next feature item; slots before F3.

**Codex deep-review queue after X10:** X11, X12, X13 (expanded — now also covers RAG retrieval-failure result), H2. Each ships as its own PR + patch release.

**After hardening queue:** X21 slots before F3 PrepApp 3-tab restructure, then F4 / B2 / F2 / R1 Stage 2.

**RAG audit backlog:** X17–X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 → X15 (expanded) → X19 → X20 → X22 → X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred — workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU indicator) — slot around the RAG audit queue.

See `project_backlog.md` for full item details.

## Last session

2026-04-20 (X24 + X25 bundle — PR #155) — Planned and executed X24+X25 as v1.2.9 patch on Sonnet 4.6. X25 first: promoted private `ReplaceWithRetry` from `SsdEncryption.cs` to new `shared/Io/FileOps.cs` (`2f7dcd8`); routes `PortableConfig.cs` and `DocumentLibraryManager.cs` call sites through the shared helper. X24 test-first: wrote failing `source_file_name` assertion, added `VectorIndex.UpdateFileName` (parameterized UPDATE), called from single-sha rename branch in `DocumentIngestor` (`53ecdf9`). Also deleted two stray agents (project-manager, tech-lead-architect) to restore the intended seven. PR #155 green + merged; v1.2.9 not yet tagged.

2026-04-20 (CI File.Replace flake fix + v1.2.8 release) — `Build and Package` on `af77abc` failed with `IOException` at `SsdEncryption.cs:327` during `ConfigStore_SerializesConcurrentSaves` — Windows Defender/indexer sharing-violation flake. Fixed in PR #153 (`4d269a7`) by adding a private `ReplaceWithRetry` helper. 5/5 flaky test passes, 409/0 full suite. Dispatched v1.2.8 (run 24650221521) — succeeded, artifact published.

## Open questions

_None._
