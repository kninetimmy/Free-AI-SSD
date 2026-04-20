# Project State

Last updated: 2026-04-19 (X10 Stage 2 merged — delete-on-replace + rename detection, PR #151)

Last released: **v1.2.6** (2026-04-19 — X9 Stages 2-4, encrypted config lifecycle). Last field-tested: v1.2.5. Next tag target: **v1.2.7** (X10 — document replacement + rebuild consistency + SQLite hardening).

## In flight

Nothing — between X10 stages. Stage 3 (rebuild-from-stored fallback + gating) is next.

## Recently shipped

- **X10 Stage 2 — delete-on-replace + rename detection — PR #151 merged** as `a430ab0` / `ad45e6d`.
  `DocumentIngestor.cs`: rename detection (single sha match → update `SourceOriginalPath`/`FileName`/
  `LastModifiedUtc`, skip re-embed; >1 collision falls through to new entry). Delete-on-replace:
  `RemoveFile` + `TryDeleteStoredFile` old path before `UpsertFileChunks` when sha changes.
  5 new tests in `DocumentReplacementTests.cs`. 408/408.

- **X10 Stage 1 — SQLite WAL + busy_timeout — PR #150 merged** as `b6536b3` / `f7cf41f`.
  `VectorIndex.OpenConnection()` helper sets `PRAGMA journal_mode=WAL` +
  `PRAGMA busy_timeout=5000` on every connection. All 5 bare `SqliteConnection` sites
  replaced. 3 new tests (WAL mode, busy_timeout value, writer-vs-writer contention).
  403/403. Fixes WAL-level concurrent reader/writer contention. **Note:** Stage 1 does NOT fix
  Stephen's field-log "file is being used by another process" error — that is a connection-pool
  OS-level lock, fixed in Stage 3's `ClearAllPools()` before `File.Delete`.

- **v1.2.6 released 2026-04-19** — Windows-only build via workflow dispatch.
  `Free-AI-SSD-win.zip` 319 MB, sha256 `01ca7f04…c62606`. Closes X9 (encrypted
  config lifecycle) across all 4 stages. Release:
  https://github.com/kninetimmy/Free-AI-SSD/releases/tag/v1.2.6

## Next up

**X10 Stage 3** (rebuild-from-stored fallback + gating) is next. Medium-high risk — touches
the rebuild critical path. Stage 3.5 provenance gate is dead code until X21 lands.
Plan: `C:\Users\Kninetimmy\.claude\plans\x10-doc-replace-rebuild.md`.

**After X10 — Codex review + v1.2.7 tag:** run Codex deep-review on merged state, address
findings, tag.

**Codex deep-review queue after X10:** X11, X12, X13 (expanded — now also covers RAG
retrieval-failure result), H2. Each ships as its own PR + patch release.

**After hardening queue — reordered 2026-04-19:** **X21** (embedding provenance +
compat gating, Sonnet, small) slots in **before F3**. Then F3 PrepApp 3-tab restructure,
then F4 / B2 / F2 / R1 Stage 2.

**RAG audit backlog (2026-04-19 plan session):** X17-X23 added covering audit findings;
X10/X13/X15 scope expansions recorded in backlog. Full staged plan in
`C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence:
X18 → X15 (expanded) → X19 → X20 → X22 → X23. X17 reduced to Stage 1 textless-page
diagnostic only (full OCR deferred — workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang`
stays on remote, unmerged, ready to rebuild if the hang returns.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU
indicator) — slot around the RAG audit queue.

See `project_backlog.md` for full item details.

## Last session

2026-04-19 (X10 Stage 2 — delete-on-replace + rename detection) — PR #151 (`a430ab0` /
`ad45e6d`) merged, CI green. `DocumentIngestor.cs`: rename detection (single sha match →
update `SourceOriginalPath`/`FileName`/`LastModifiedUtc`, skip re-embed; >1 sha collision
falls through to new entry). Delete-on-replace: `RemoveFile` + `TryDeleteStoredFile` old
path before `UpsertFileChunks` when sha changes. 5 tests in `DocumentReplacementTests.cs`
(replace, rename, moved-source rename, edit+rename=new entry, sha collision). 408/408.

2026-04-19 (X10 Stage 1 — SQLite WAL + busy_timeout) — PR #150 (`b6536b3` / `f7cf41f`)
merged, CI green. `VectorIndex.OpenConnection()` centralizes all 5 connection opens; WAL
and 5 s busy_timeout on every connection. Advisor catch: original test #3 used a reader
(unblocked in WAL mode) — fixed to writer-vs-writer contention. 403/403.

## Open questions

_None._
