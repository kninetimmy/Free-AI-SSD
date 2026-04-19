# Project State

Last updated: 2026-04-19 (v1.2.5 field-tested — X1-Redux dormant, could not reproduce across 10+ prompts; X9 Stage 2 now the active blocker for v1.2.6)

Last live-tested release: **v1.2.5** (field-tested 2026-04-19 — chat, TTS, library creation, PTT all healthy; the v1.2.4 X1-Redux / X6 / X8 symptoms did not reproduce). Next tag target: **v1.2.6** (X9 Stages 2-4, encrypted config lifecycle).

## In flight

- **X9 Stage 2 — shared lib for encrypted config lifecycle.** Stage 1 plan
  locked 2026-04-19 (see `project_backlog.md` → X9). Next: implement
  `IConfigStore` + `ConfigStore` + `UnlockMaterial`, symmetric
  `SaveEncryptedConfigAsync` with two-file atomic commit,
  in-memory `EnableConfigEncryptionAsync` overload, and
  `TryUnlockPortableConfigWithMaterial`. Real-crypto unit tests only — no
  mocks on the encrypt/decrypt path.

- **Doc PR #143 pending merge.** Marks X1-Redux dormant, records v1.2.5
  field-test findings, adds X14 (50 MB upload UX), folds `vectors.db`
  file-lock into X10 scope. Fresh-session Claude: once this merges,
  `project_state.md` and `project_backlog.md` on `main` are the
  authoritative post-merge state.

## Recently shipped

- **v1.2.5 field test — 2026-04-19.** Stephen ran `main` (commit
  `54b276a`) on the SSD; chat, TTS, library creation, and PTT all
  healthy. The three v1.2.4 symptoms (X1-Redux text-Send hang, X6
  Create Library hang, X8 Whisper crash) did **not** reproduce across
  10+ varied prompts. Runner log at `G:\logs\runner-20260419.log`
  surfaced three side-finds: RAG silently 404s (known — `nomic-embed-text`
  not pulled on Stephen's Ollama), `vectors.db` rebuild failed with a
  file-lock error (rolled into X10 scope), and a 140 MB PDF upload
  was silently rejected against the 50 MB limit (triaged as X14).

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
  docs-framework restructure. Not field-tested at tag time; cleared by the
  2026-04-19 field test above.

## Next up

**Blocking v1.2.6 tag:** X9 Stages 2-4 — encrypted config lifecycle.
Stage 2 (shared lib) is the immediate next PR; plan is approved and
lives at `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-lexical-wren.md`.

**After X9 ships — remaining Codex deep-review queue:**
X10 (document replacement + rebuild — now also covers `vectors.db`
file lock observed in 2026-04-19 field log), X11 (companion keyboard
PTT + first-run validation), X12 (download verify-before-move), X13
(chat/STT surface real failures), H2 (hardening batch). Each ships
as its own PR + patch release per the v1.2.x cadence decision.

**Dormant (could not reproduce — keep on the radar):** X1-Redux.
Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged,
ready to rebuild if the hang ever returns.

**After hardening queue:** F3 PrepApp 3-tab restructure (Opus plan first),
then re-evaluate F2 / F4 stage 1 / B2 / R1 Stage 2 / X6 / X7 / F5.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5
(GPU/CPU indicator).

See `project_backlog.md` for full item details.

## Last session

2026-04-19 (v1.2.5 field test + doc PR) — **Field test cleared X1-Redux
off the blocker list.** Stephen ran `main` at `54b276a` on the SSD,
exercised chat via example prompts and custom prompts across 10+
varied inputs, TTS played and cleaned up normally, Create Library
completed in ~60 ms, and PTT pipeline cancelled without crashing
Runner. None of the v1.2.4 symptoms (X1-Redux text-Send hang / X6
library-create hang / X8 Whisper crash) reproduced. Doc PR flushed
dashboard to match: X1-Redux moved from "in flight, parked" to
"dormant," X10 scope grew to cover the `vectors.db` file-lock error
seen in the field log, new X14 item filed for 50 MB upload silent
rejection. v1.2.6 tag now gated on X9 Stages 2-4 alone.

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

## Open questions

_None right now. X8 merge-scope resolved by folding the race findings into PR #138._
