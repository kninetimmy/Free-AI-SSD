# Project State

Last updated: 2026-04-19 (X9 Stage 2 shared lib shipped — Stage 3 Runner wiring is the next PR)

Last live-tested release: **v1.2.5** (field-tested 2026-04-19 — chat, TTS, library creation, PTT all healthy; the v1.2.4 X1-Redux / X6 / X8 symptoms did not reproduce). Next tag target: **v1.2.6** (X9 Stages 2-4, encrypted config lifecycle).

## In flight

- **X9 Stage 3 — Runner wiring for encrypted config.** Stage 2 shipped
  (PR #144, `49ce6a0`) — shared-lib `IConfigStore` + `ConfigStore` +
  `UnlockMaterial`, symmetric `SaveEncryptedConfigAsync`, in-memory
  finalize overload, `TryUnlockPortableConfigWithMaterial`. Nothing is
  wired to the store yet. Stage 3 routes `MainWindow.SaveConfigAsync` +
  `DocumentOperationsService` through `IConfigStore`, captures
  `UnlockMaterial` on unlock, adds `OnClosing` flush+lock. Plan calls
  for Sonnet 4.6 implementation with an Opus advisor pass.

## Recently shipped

- **X9 Stage 2 — shared lib shipped — PR #144 merged** as commit `49ce6a0`.
  `IConfigStore` / `ConfigStore` / `UnlockMaterial` added, plus three new
  `SsdEncryption` members: symmetric `SaveEncryptedConfigAsync` with
  two-file atomic commit + `File.Replace`-based rollback, in-memory
  `EnableConfigEncryptionAsync` overload, and `TryUnlockPortableConfigWithMaterial`
  (zeros the derived key on every failure path). 10 new real-crypto
  tests — full suite 392/392 green on `main`. Nothing wired to the new
  store yet; Stages 3-4 land that.

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
  field drives. Advisor pass surfaced the plaintext-newer-than-encrypted
  migration branch (matters for Stephen's drive — all his post-unlock
  edits have been landing in plaintext).

## Next up

**Blocking v1.2.6 tag:** X9 Stages 3-4 — encrypted config lifecycle.
Stage 3 (Runner wiring) is the immediate next PR; plan is approved and
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

2026-04-19 (X9 Stage 2 — shared lib shipped) — **Stage 2 implemented
and merged as PR #144 (`49ce6a0`).** Added `IConfigStore` /
`ConfigStore` / `UnlockMaterial` + three symmetric-crypto members on
`SsdEncryption`. Advisor pass on the implementation caught two real
issues that got fixed before merge: rollback used Delete+Move instead
of `File.Replace` (a crash mid-rollback could have left stale-state +
no-blob), and `TryUnlockPortableConfigWithMaterial` didn't zero the
derived key on wrong-password / decrypt-exception branches. Also filed
backlog X15 (revisit RAG file-size + 10k chunk caps — Chuck's Guides
run 120-160 MB and the current 50 MB cap rejects them). Model choice
for Stages 3-4 settled on Sonnet 4.6 + Opus advisor per the Stage 1
plan; saved a persistent feedback memory so Sonnet uses a lower
advisor-call threshold on this project.

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
rejection.

## Open questions

_None right now. X8 merge-scope resolved by folding the race findings into PR #138._
