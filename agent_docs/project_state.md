# Project State

Last updated: 2026-04-20 (v1.2.8 released — CI File.Replace flake fix on top of X10)

Last released: **v1.2.8** (2026-04-20 — v1.2.7 content + CI `File.Replace` retry fix). Last field-tested: v1.2.5. Next tag target: TBD (X11 or X21 — see Next up).

> v1.2.7 tag exists on `af77abc` but its Build and Package run (24646703518) failed before the release job — flake in `ConfigStore_SerializesConcurrentSaves`. No GitHub release artifact was published for v1.2.7; v1.2.8 supersedes it with the retry fix merged in.

## In flight

Nothing — between tasks.

## Recently shipped

- **v1.2.8 released 2026-04-20** — `4d269a7` + prior `af77abc`. `Free-AI-SSD-win.zip` published (run 24650221521). Contains X10 Stages 1–3 + the CI `File.Replace` retry hardening (PR #153). Release: https://github.com/kninetimmy/Free-AI-SSD/releases/tag/v1.2.8

- **PR #153 — `File.Replace` retry in `SsdEncryption.SaveEncryptedConfigAsync` — merged as `4d269a7`.** Added private `ReplaceWithRetry` helper (5 attempts, 25 ms base backoff doubling, retries only `IOException` / `UnauthorizedAccessException`) at the three `File.Replace` sites in the encrypted-config save (blob commit, state commit, rollback). Unblocks Windows CI after run 24646703518 flaked on Defender/indexer sharing violation. Scope-limited: other `File.Replace` callers left for **X25**.

- **X10 Stage 4 — deep-review + v1.2.7 tag — 2026-04-19.** Non-adversarial review of merged Stages 1–3 done in-conversation (Opus 4.7, no subagents, not Codex plugin). 411/412 tests green. Verdict: ready to tag. Five Yellow findings logged (primary: rename detection leaves stale `source_file_name` in `chunks` table, user-visible via `CitationBuilder.cs:8-9` — filed as **X24**). Zero Red. Tag `v1.2.7` pushed on `af77abc`; win-x64 release workflow dispatched.

## Next up

**X24 — citation staleness after rename** (from Stage 4 Yellow #1). Small fix; candidate to pull forward with X21 or its own patch.

**X25 — extend `File.Replace` retry to remaining call sites.** Two sites still unprotected (`PortableConfig.cs:314`, `DocumentLibraryManager.cs:48,136`). Small follow-up; ride along with whatever next touches those files.

**Codex deep-review queue after X10:** X11, X12, X13 (expanded — now also covers RAG retrieval-failure result), H2. Each ships as its own PR + patch release.

**After hardening queue — reordered 2026-04-19:** **X21** (embedding provenance + compat gating, Sonnet, small) slots **before F3**. Then F3 PrepApp 3-tab restructure, then F4 / B2 / F2 / R1 Stage 2.

**RAG audit backlog:** X17–X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 → X15 (expanded) → X19 → X20 → X22 → X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred — workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU indicator) — slot around the RAG audit queue.

See `project_backlog.md` for full item details.

## Last session

2026-04-20 (CI File.Replace flake fix + v1.2.8 release) — `Build and Package` on `af77abc` (the v1.2.7 tag run) failed with `IOException: process cannot access the file` at `SsdEncryption.cs:327` during `ConfigStore_SerializesConcurrentSaves` — Windows Defender/indexer sharing-violation flake (same tree had passed on the prior run). Fixed in PR #153 (`4d269a7`) by adding a private `ReplaceWithRetry` helper around the three `File.Replace` sites in `SaveEncryptedConfigAsync`. Local verification: 5/5 passes of the flaky test, 409/0 full suite. Dispatched v1.2.8 (run 24650221521) — succeeded, artifact published. v1.2.7 tag left in place but has no GH release; v1.2.8 supersedes.

2026-04-19 (X10 Stage 4 — deep-review + v1.2.7 tag) — Non-adversarial deep-review of merged Stages 1–3 performed directly in-conversation on Opus 4.7 (no subagents, no Codex plugin). Verified all three stages' acceptance criteria, ran `dotnet test` (411 pass / 0 fail / 1 X21 skip, 14s), confirmed security invariants preserved. Five Yellow findings, zero Red. Tagged `v1.2.7` on `af77abc`, pushed, dispatched Build and Package workflow (run 24646703518) for win-x64. Primary Yellow: rename leaves stale `source_file_name` in chunks → user-visible via `CitationBuilder.cs:8-9`. Filed as X24.

## Open questions

_None._
