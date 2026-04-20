# Project State

Last updated: 2026-04-20 (X12 — PR #161 open)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**PR #161 — X12** — `fix/x12-download-verify-before-move`. CI green, pending merge.

## Recently shipped

- **PR #159 — X11 — merged `329bcf2e` (2026-04-20).** Four companion fixes: `WH_KEYBOARD_LL` replaces `RegisterHotKey` for real PTT hold tracking; startup gate prevents falling through on cancelled first-run; HOTAS null-device guard; API key `PasswordBox` + `ServerRequiresApiKey`. `PttBindingParser` extracted to shared for testability. 8 new tests (431 pass, 1 skip). `bed0f02`/`c754fd4`/`2b668bb`/`4cffe19`.

- **PR #158 — X21b — merged `92625a9` (2026-04-19).** `DocumentLibraryManager.ScanProvenanceMismatches` scans all library manifests and returns those with a known, non-"unknown" `LastEmbeddingModel` that differs from the current config model (case-insensitive). `PrepViewModel.CheckAndPromptLibraryReindexAsync` fires once per drive root per session on drive selection; guards encrypted/busy/no-config; posts per-library confirmation dialog to UI thread; on confirm resolves existing Ollama exe (no download), starts temp server, runs `RebuildIndexAsync` per library, catches per-library failures independently. 7 new TDD tests (423 pass, 1 skip).

- **v1.2.9 released 2026-04-19** — `e385fff`. `Free-AI-SSD-win.zip` published (run 24654381970). Contains X24 (citation staleness fix) + X25 (shared `FileOps.ReplaceWithRetry`). Release: https://github.com/kninetimmy/Free-AI-SSD/releases/tag/v1.2.9

## Next up

**X13** (expanded — also covers RAG retrieval-failure result), **H2**. Each ships as its own PR + patch release.

**After hardening queue:** F3 PrepApp 3-tab restructure (X21 complete — no longer blocking F3), then F4 / B2 / F2 / R1 Stage 2.

**RAG audit backlog:** X17–X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 → X15 (expanded) → X19 → X20 → X22 → X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred — workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU indicator) — slot around the RAG audit queue.

See `project_backlog.md` for full item details.

## Last session

2026-04-20 (X12 — implement + PR #161) — Planned and implemented X12 (`shared/DownloadManager.cs:94-101`): SHA-256 verification now runs against the `.part` temp file before `File.Move`; on mismatch temp is deleted and destination stays absent. 3 new tests in `DownloadManagerTests.cs` (437 pass, 1 skip). PR #161 open, CI green. Also established new workflow: /wrap-up runs on the feature branch before merging so doc updates ship in the same PR.

2026-04-20 (X11 — review + merge) — Reviewed Codex's PR #159 and Gemini's inline comment. Gemini's regression flag (API key cleared on save) was a false positive — `ResolveApiKeyForSave` falls through to preserve the existing key when the PasswordBox is blank and no explicit clear was requested. Merged `329bcf2e`. X11 complete.

## Open questions

_None._
