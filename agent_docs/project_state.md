# Project State

Last updated: 2026-04-18 (X8 shipped via PR #138; X1-Redux phase 1 diagnostic awaiting SSD log)

Last live-tested release: **v1.2.4**. Next tag target: **v1.2.5** (rolls up X8 + X1-Redux fix once the diagnostic round-trips).

## In flight

- **X1-Redux phase 1 — diagnostic-only branch `diag/x1-redux-send-hang`**
  (not a PR, never to merge). Instruments `Send_Click`, `SendStreamingAsync`,
  `StopTts`, and the token callback; twin heartbeats (500ms bg `Task.Run` +
  500ms `DispatcherTimer`) write to `%TEMP%\freeai-x1redux-diagnostic.log`.
  Pushed; **awaiting Stephen to repro on the SSD and return the log.** Gap
  pattern between `[watchdog-bg]` vs `[ui-hb]` pings will discriminate
  UI-thread deadlock vs process-level hang vs HTTP-stream-never-ends.

- **Docs framework restructure.** `CLAUDE.md` slimmed to a pointer file;
  `agent_docs/project_{arch,backlog,decisions,state}.md` is now the source
  of truth split. This doc is the dashboard loaded every session.

## Recently shipped

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

**After v1.2.5 tag:** F3 PrepApp 3-tab restructure (Opus plan first), then
re-evaluate F2 / F4 stage 1 / B2 / R1 Stage 2 / X6 / X7 / F5.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5
(GPU/CPU indicator).

See `project_backlog.md` for full item details.

## Last session

2026-04-18 (X8 follow-up) — **X8 race hardening shipped; PR #138 merged.**
Resumed from the prior session's Codex adversarial review which flagged
two residual races on top of the initial gate-lifetime fix: dispose vs
in-flight transcription, and unsynchronized init across the three
singleton consumers. Solved with a single `_lifecycleGate` + `_shutdownCts`
and a `CancellationToken` threaded through `ISpeechToTextService` into
PTT + network API. Commit `9c3a054` pushed to the X8 branch, merged
on GitHub as `fa34828`. Docs restructure opened as follow-up PR on
`docs/agent-docs-framework`.

2026-04-18 (X1-Redux phase 1 + X8 initial) — **X8 opened as PR #138;
X1-Redux diagnostic branch pushed.** Reading Stephen's Runner log for
X1-Redux surfaced a distinct bug — Whisper `ObjectDisposedException`
on STT re-init — which became Track A and shipped as `591a39b` with
4 reflection-based regression tests. Track B = diagnostic-only branch
`diag/x1-redux-send-hang` with instrumented Send path and twin
heartbeats; Stephen runs it on the SSD, sends back
`%TEMP%\freeai-x1redux-diagnostic.log`. Dead ends: tried to infer the
hang root cause from reading MainWindow/StreamingTtsSpeaker alone —
advisor correctly pointed out the code gaps would fire the finally
*too early*, not stall it, so instrumentation is the only way forward.

2026-04-18 (v1.2.4 field test) — **Field test walked; v1.2.4 tag deferred.**
Findings at `v1.2.4_field_test_findingd.md`. Sections 1 and 3 clean (B3-Redux
UAC auto-resume on real SSD confirmed end-to-end, closing v1.2.3 deferred
verification). Sections 2 + 4a surfaced the X1 hang is not actually fixed:
Runner crashed on first TTS attempt (Section 2 never ran), Section 4a
reproduced "generating…" stall via example-prompt Send. Four new backlog items
filed: X1-Redux (blocks tag), X6 (Create Library hang), X7 (DCS bindings
false-negative), F5 (in-app TTS settings UI).

## Open questions

_None right now. X8 merge-scope resolved by folding the race findings into PR #138._
