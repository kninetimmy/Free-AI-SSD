# Project State

Last updated: 2026-04-20 (X13 — implementation complete, PR pending)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**X13** — `feat/x13-surface-chat-stt-failures`. Implementation complete, all changes uncommitted on branch. Ready to commit + open PR.

## Recently shipped

- **PR #161 — X12 — merged `49fe0a2` (2026-04-20).** SHA-256 verification runs against the `.part` temp file before `File.Move`; on mismatch temp is deleted and destination stays absent. 3 new tests (437 pass, 1 skip).

- **PR #159 — X11 — merged `329bcf2` (2026-04-20).** Four companion fixes: `WH_KEYBOARD_LL` replaces `RegisterHotKey` for real PTT hold tracking; startup gate prevents falling through on cancelled first-run; HOTAS null-device guard; API key `PasswordBox` + `ServerRequiresApiKey`. `PttBindingParser` extracted to shared for testability. 8 new tests.

## Next up

**H2** after X13 PR merges. Each ships as its own PR + patch release.

**After hardening queue:** F3 PrepApp 3-tab restructure (X21 complete — no longer blocking F3), then F4 / B2 / F2 / R1 Stage 2.

**RAG audit backlog:** X17–X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 → X15 (expanded) → X19 → X20 → X22 → X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred — workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU indicator) — slot around the RAG audit queue.

See `project_backlog.md` for full item details.

## Last session

2026-04-20 (X13 — implementation complete, PR pending) — Introduced `ChatResult` (Success / RagRetrievalFailed / Failure) and `TranscriptionResult` (Success / Failure) discriminated unions across 13 files. All callers — `RunnerLocalApiService`, `PttVoicePipelineService`, `MainWindow.xaml.cs`, `Repl`, `RunnerApiClient` — updated to switch exhaustively. `/voice/query` blocker fixed: `ChatServiceFailureException` private class routes chat failures to 503 (was incorrectly catching as `InvalidOperationException` → 400). 449 tests pass (+12: `ChatServiceTests.cs` new file, new tests in `RunnerLocalApiServiceTests` and `WhisperSpeechToTextServiceTests`). All changes uncommitted on `feat/x13-surface-chat-stt-failures`.

2026-04-20 (X12 — implement + PR #161) — Planned and implemented X12 (`shared/DownloadManager.cs`): SHA-256 verification now runs against the `.part` temp file before `File.Move`; on mismatch temp is deleted and destination stays absent. 3 new tests. PR #161 merged `49fe0a2`. Also established new workflow: /wrap-up runs on the feature branch before merging so doc updates ship in the same PR.

## Open questions

_None._
