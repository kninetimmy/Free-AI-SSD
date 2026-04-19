# Project State

Last updated: 2026-04-18 (Codex deep-review intake — 6 backlog items added; X1-Redux phase 1 still awaiting SSD log)

Last live-tested release: **v1.2.4**. Next tag target: **v1.2.5** (rolls up X8 + X1-Redux fix once the diagnostic round-trips).

## In flight

- **X1-Redux phase 1 — diagnostic-only branch `diag/x1-redux-send-hang`**
  (not a PR, never to merge). Instruments `Send_Click`, `SendStreamingAsync`,
  `StopTts`, and the token callback; twin heartbeats (500ms bg `Task.Run` +
  500ms `DispatcherTimer`) write to `%TEMP%\freeai-x1redux-diagnostic.log`.
  Pushed; **awaiting Stephen to repro on the SSD and return the log.** Gap
  pattern between `[watchdog-bg]` vs `[ui-hb]` pings will discriminate
  UI-thread deadlock vs process-level hang vs HTTP-stream-never-ends.

## Recently shipped

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

**Blocking v1.2.5 tag:** Track B (X1-Redux) log from SSD. Fix lands as a
follow-up PR once the log identifies the stall point.

**After v1.2.5 tag — Codex deep-review remediation queue (triaged 2026-04-18):**
X9 (encrypted config lifecycle — Critical, Opus plan), X10 (document
replacement + rebuild), X11 (companion keyboard PTT + first-run validation),
X12 (download verify-before-move), X13 (chat/STT surface real failures),
H2 (hardening batch). Each ships as its own PR + patch release per the
v1.2.x cadence decision.

**After hardening queue:** F3 PrepApp 3-tab restructure (Opus plan first),
then re-evaluate F2 / F4 stage 1 / B2 / R1 Stage 2 / X6 / X7 / F5.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5
(GPU/CPU indicator).

See `project_backlog.md` for full item details.

## Last session

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

2026-04-18 (wrap-up + K9 framework extraction) — **Session housekeeping, no code changes.**
Ran `/check-init` — Yellow: flagged `project_state.md` at 87 lines
(approaching 100-line budget) and a stale "in flight" item for the
docs restructure (already merged as `74629a4` / PR #139). Confirmed
K9-Claude-Framework is now its own repo at
`github.com/kninetimmy/K9-Claude-Framework`, portable install path
sorted.

## Open questions

_None right now. X8 merge-scope resolved by folding the race findings into PR #138._
