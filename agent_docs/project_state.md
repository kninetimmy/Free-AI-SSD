# Project State

Last updated: 2026-04-19 (RAG audit triage plan — X17-X23 scoped, X21 slots before F3)

Last live-tested release: **v1.2.5** (field-tested 2026-04-19 — chat, TTS, library creation, PTT all healthy; the v1.2.4 X1-Redux / X6 / X8 symptoms did not reproduce). Next tag target: **v1.2.6** (X9 Stages 2-4, encrypted config lifecycle — **X9 complete**).

## In flight

Between tasks.

## Recently shipped

- **X9 Stage 4 — Prep finalize + migration + guard rewrite shipped — PR #147 merged**
  as `36c9a7a` + `b75e42a`. In-memory finalize (no plaintext intermediate);
  `TryMigratePlaintextAsync` with mtime-aware Branch A (absorb + "Settings Recovery"
  confirmation) / Branch B (silent delete + log); 7 new real-crypto guard tests —
  400/400. Two post-review fixes: in-memory finalize now deletes pre-existing plaintext
  (advisor catch, vacuous test replaced with seeded version); `LoadWithValidationAsync`
  guards migration from corrupt plaintext overwriting encrypted blob; `UnlockDriveButton`
  disabled across async unlock/migrate span (both Gemini findings). X9 is **complete**.

## Next up

**v1.2.6 tag unblocked** — X9 complete. Tag when ready.

**Codex deep-review queue:** X10 (expanded — now also covers SQLite WAL/busy_timeout +
rebuild-from-stored per RAG audit), X11, X12, X13 (expanded — now also covers RAG
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

2026-04-19 (RAG audit triage plan) — third-party audit
(`C:\Users\Kninetimmy\Documents\ssd md files\RAG_Issues_With_Prop_Fixes.md`) reviewed and
mapped onto backlog. Verified 9 findings against current code (3 parallel Explore agents).
4 decisions locked via AskUserQuestion: X17 multimodal scoped down to Stage 1 diagnostic
only; X21 provenance slots before F3 (reorders roadmap); 7 new X-items (no umbrella);
X10 stable-doc-ID spun out as X10-Redux for later. Plan saved; no code changed. Two
audit findings flagged as goal-mismatch: "no ANN index" (deliberate portable constraint)
and "multimodal PDF Critical" (workload is text-layer manuals, not scans).

2026-04-19 (X9 Stage 4 — finalize + migration + guard rewrite) — **Stage 4 implemented
and merged as PR #147 (`b75e42a`).** In-memory finalize, mtime-aware
`TryMigratePlaintextAsync`, 7 new guard tests. Advisor catch: in-memory overload didn't
clean up pre-existing plaintext — fixed; seeded test replaced the vacuous one. Gemini:
corrupt plaintext could overwrite valid encrypted blob (`LoadWithValidationAsync` fix);
re-entrancy on unlock button (`try/finally` disable). X9 complete.

## Open questions

_None._
