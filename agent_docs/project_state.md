# Project State

Last updated: 2026-04-19 (v1.2.6 released; X10 plan locked, queued for Sonnet)

Last released: **v1.2.6** (2026-04-19 — X9 Stages 2-4, encrypted config lifecycle). Last field-tested: v1.2.5. Next tag target: **v1.2.7** (X10 — document replacement + rebuild consistency + SQLite hardening).

## In flight

**X10 — Stage 1 (SQLite WAL + busy_timeout) queued for Sonnet.** Plan locked at `C:\Users\Kninetimmy\.claude\plans\x10-doc-replace-rebuild.md`. 3 impl PRs + review → v1.2.7.

## Recently shipped

- **v1.2.6 released 2026-04-19** — Windows-only build via workflow dispatch.
  `Free-AI-SSD-win.zip` 319 MB, sha256 `01ca7f04…c62606`. Closes X9 (encrypted
  config lifecycle) across all 4 stages. Release:
  https://github.com/kninetimmy/Free-AI-SSD/releases/tag/v1.2.6

- **X9 Stage 4 — Prep finalize + migration + guard rewrite shipped — PR #147 merged**
  as `36c9a7a` + `b75e42a`. In-memory finalize (no plaintext intermediate);
  `TryMigratePlaintextAsync` with mtime-aware Branch A (absorb + "Settings Recovery"
  confirmation) / Branch B (silent delete + log); 7 new real-crypto guard tests —
  400/400. Two post-review fixes: in-memory finalize now deletes pre-existing plaintext
  (advisor catch, vacuous test replaced with seeded version); `LoadWithValidationAsync`
  guards migration from corrupt plaintext overwriting encrypted blob; `UnlockDriveButton`
  disabled across async unlock/migrate span (both Gemini findings). X9 is **complete**.

## Next up

**X10 first** (plan locked 2026-04-19) — Stage 1 SQLite hardening ships alone as v1.2.7
to address Stephen's field-log lock error; Stages 2 (delete-on-replace) and 3
(rebuild-from-stored) follow. Plan: `C:\Users\Kninetimmy\.claude\plans\x10-doc-replace-rebuild.md`.

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

2026-04-19 (v1.2.6 release + X10 plan) — PR #148 (`2b88aef`) merged; v1.2.6
released Windows-only via workflow dispatch (`Free-AI-SSD-win.zip` 319 MB,
sha256 `01ca7f04…c62606`). X10 plan written to
`C:\Users\Kninetimmy\.claude\plans\x10-doc-replace-rebuild.md`: 3 impl stages
(SQLite WAL → delete-on-replace + rename detection → rebuild-from-stored
gated on X21) + Codex review → v1.2.7. Decisions locked: path-primary +
sha256-assisted rename detection (X10-Redux GUID deferred); WAL + busy_timeout
on every SqliteConnection open; rebuild-from-stored dead-code until X21
provenance lands. No code changes. User switching to Sonnet to implement.

2026-04-19 (X9 Stage 4 — finalize + migration + guard rewrite) — **Stage 4 implemented
and merged as PR #147 (`b75e42a`).** In-memory finalize, mtime-aware
`TryMigratePlaintextAsync`, 7 new guard tests. Advisor catch: in-memory overload didn't
clean up pre-existing plaintext — fixed; seeded test replaced the vacuous one. Gemini:
corrupt plaintext could overwrite valid encrypted blob (`LoadWithValidationAsync` fix);
re-entrancy on unlock button (`try/finally` disable). X9 complete.

## Open questions

_None._
