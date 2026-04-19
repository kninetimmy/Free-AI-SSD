# Project State

Last updated: 2026-04-19 (X9 Stage 1 plan locked — Opus + advisor pass; Stage 2 code unblocked; X1-Redux log wait parked)

Last live-tested release: **v1.2.4**. **v1.2.5** tagged on `74629a4` (docs-framework restructure + X8 rollup) but not field-tested. Next tag target: **v1.2.6** (X1-Redux fix, once the diagnostic round-trips).

## In flight

- **X9 Stage 2 — shared lib for encrypted config lifecycle.** Stage 1 plan
  locked 2026-04-19 (see `project_backlog.md` → X9). Next: implement
  `IConfigStore` + `ConfigStore` + `UnlockMaterial`, symmetric
  `SaveEncryptedConfigAsync` with two-file atomic commit,
  in-memory `EnableConfigEncryptionAsync` overload, and
  `TryUnlockPortableConfigWithMaterial`. Real-crypto unit tests only — no
  mocks on the encrypt/decrypt path.

- **X1-Redux phase 1 — diagnostic branch `diag/x1-redux-send-hang`.**
  **Parked** awaiting Stephen's SSD repro log. Branch never merges. When the
  log arrives: gap pattern between `[watchdog-bg]` vs `[ui-hb]` pings
  discriminates UI-thread deadlock vs process-level hang vs
  HTTP-stream-never-ends. Fix will retarget v1.2.6.

## Recently shipped

- **X9 Stage 1 plan locked — PR #142 merged** as commit `54b276a`.
  Locks `IConfigStore` contract, `UnlockMaterial` shape, two-file
  atomic commit for encrypted blob + state file, bounded
  `FlushAsync(5s)` on close, and mtime-aware migration for existing
  field drives. Stage 2 code is unblocked. Advisor pass surfaced the
  plaintext-newer-than-encrypted migration branch (matters for
  Stephen's drive — all his post-unlock edits have been landing in
  plaintext).

- **Rollup retargeted to v1.2.6 — PR #141 merged** as commit
  `f58481b`. v1.2.5 already tagged on the docs-framework commit
  without the X1-Redux fix; dashboard corrected to retarget the
  rollup at v1.2.6.

- **v1.2.5 tagged on `74629a4`** — rolls up X8 (commit `fa34828`) and the
  docs-framework restructure. Not field-tested; X1-Redux fix deferred to v1.2.6.

- **Codex deep-review intake docs — PR #140 merged** as commit `bfac019`.
  Adds X9–X13 + H2 to `project_backlog.md` and renumbers the priority queue.

- **Docs framework restructure — PR #139 merged** as commit `74629a4`.
  `CLAUDE.md` slimmed to a pointer file;
  `agent_docs/project_{arch,backlog,decisions,state}.md` split is now
  the source of truth loaded every session.

- **X8 (post-v1.2.4) — PR #138 merged** as commit `fa34828`.
  - Initial commit `591a39b` split model teardown from full `Dispose()` so
    `InitializeAsync`'s internal reset no longer disposed the semaphore,
    closing the `ObjectDisposedException` Stephen hit on v1.2.4 field test.
  - Follow-up `9c3a054` folded in the three races Gemini + Codex review
    flagged: `InitializeAsync` now holds the gate, window-close `Dispose()`
    cancels and drains in-flight `ProcessAsync` (5s bounded), and the
    singleton's three consumers (MainWindow voice button, PTT pipeline,
    network API) serialize through a single `_lifecycleGate`. `ISpeechToTextService`
    gained `CancellationToken` overloads so PTT + network callers thread
    their existing CTs into STT.
  - 382/382 tests green on merge.

- Side-find from Stephen's field log: `POST /api/embed → 404`. nomic-embed-text
  not pulled on his Ollama; RAG silently inert. Field-setup gap, not a bug.

## Next up

**Blocking v1.2.6 tag:** Track B (X1-Redux) log from SSD. Fix lands
as a follow-up PR once the log identifies the stall point.

**Running parallel (X1-Redux parked):** X9 Stage 2 code — shared lib
for encrypted config lifecycle. Spec lives in `project_backlog.md` → X9.

**After X9 ships — remaining Codex deep-review queue:**
X10 (document replacement + rebuild), X11 (companion keyboard PTT +
first-run validation), X12 (download verify-before-move), X13
(chat/STT surface real failures), H2 (hardening batch). Each ships
as its own PR + patch release per the v1.2.x cadence decision.

**After hardening queue:** F3 PrepApp 3-tab restructure (Opus plan first),
then re-evaluate F2 / F4 stage 1 / B2 / R1 Stage 2 / X6 / X7 / F5.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5
(GPU/CPU indicator).

See `project_backlog.md` for full item details.

## Last session

2026-04-19 (doc flush + X9 Stage 1 plan lock) — **Docs-only session, three PRs.**
PR #140 (`bfac019`) flushed the dirty Codex-intake backlog edits from
the prior session. PR #141 (`f58481b`) caught that v1.2.5 was already
tagged on `74629a4` without the X1-Redux fix — dashboard corrected
and rollup retargeted at v1.2.6. PR #142 (`54b276a`) locked the X9
Stage 1 plan after an Opus + advisor pass; advisor caught three real
gaps (two-file atomic commit, drain-on-shutdown, migration must
handle plaintext-newer-than-encrypted — Stephen's actual case).
X1-Redux log wait explicitly parked so X9 Stage 2 becomes the
active work.

2026-04-18 (Codex deep-review intake) — **Session housekeeping, no code changes.**
Stephen pasted a large Codex deep-review report. Verified every Critical /
High / Medium finding against live source — all confirmed real (encrypted
config `SaveAsync` always-plaintext + finalize bootstrap fail-closed;
`DocumentIngestor` stale-vector + rebuild-from-originals; `KeyboardPttHotkey`
100ms fake release; `DownloadManager` hash-after-move; `ChatService` /
`WhisperSpeechToTextService` empty-on-failure). No false positives on the
sample checked. Routed findings into backlog as X9 (Critical, Opus plan),
X10, X11, X12, X13, H2. Priority order in `project_backlog.md` updated to
slot the queue between X1-Redux and F3.

## Open questions

_None right now. X8 merge-scope resolved by folding the race findings into PR #138._
