# Project State

Last updated: 2026-04-19 (X21b — PrepApp reindex prompt — PR #158 merged)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

Between tasks. X21 (all stages) complete.

## Recently shipped

- **PR #158 — X21b — merged `92625a9` (2026-04-19).** `DocumentLibraryManager.ScanProvenanceMismatches` scans all library manifests and returns those with a known, non-"unknown" `LastEmbeddingModel` that differs from the current config model (case-insensitive). `PrepViewModel.CheckAndPromptLibraryReindexAsync` fires once per drive root per session on drive selection; guards encrypted/busy/no-config; posts per-library confirmation dialog to UI thread; on confirm resolves existing Ollama exe (no download), starts temp server, runs `RebuildIndexAsync` per library, catches per-library failures independently. 7 new TDD tests (423 pass, 1 skip).

- **v1.2.9 released 2026-04-19** — `e385fff`. `Free-AI-SSD-win.zip` published (run 24654381970). Contains X24 (citation staleness fix) + X25 (shared `FileOps.ReplaceWithRetry`). Release: https://github.com/kninetimmy/Free-AI-SSD/releases/tag/v1.2.9

- **PR #155 — X24 + X25 — merged `e385fff` (2026-04-20).** `2f7dcd8` (X25): promoted private `ReplaceWithRetry` from `SsdEncryption.cs` to new `shared/Io/FileOps.cs`; all four `File.Replace` call sites now use the shared retry helper. `53ecdf9` (X24): added `VectorIndex.UpdateFileName` (parameterized UPDATE) and called it from the single-sha rename branch in `DocumentIngestor` — citations show current filename immediately after rename, no re-embed needed. Test written failing-first. 411/0/1.

- **v1.2.8 released 2026-04-20** — `4d269a7` + prior `af77abc`. `Free-AI-SSD-win.zip` published (run 24650221521). Contains X10 Stages 1–3 + CI `File.Replace` retry hardening (PR #153). Release: https://github.com/kninetimmy/Free-AI-SSD/releases/tag/v1.2.8

## Next up

**Codex deep-review queue after X10:** X11, X12, X13 (expanded — now also covers RAG retrieval-failure result), H2. Each ships as its own PR + patch release.

**After hardening queue:** F3 PrepApp 3-tab restructure (X21 complete — no longer blocking F3), then F4 / B2 / F2 / R1 Stage 2.

**RAG audit backlog:** X17–X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 → X15 (expanded) → X19 → X20 → X22 → X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred — workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

**v1.3.x territory:** X4 (bundled web chat UI), Runner tab restructure, X5 (GPU/CPU indicator) — slot around the RAG audit queue.

See `project_backlog.md` for full item details.

## Last session

2026-04-19 (X21b — PrepApp reindex prompt — PR #158) — `ScanProvenanceMismatches` added to `DocumentLibraryManager`; `CheckAndPromptLibraryReindexAsync` added to `PrepViewModel` (fires once per drive root per session, guards encrypted/busy/no-config, per-library dialog + per-library rebuild, no Ollama download). 7 new TDD tests. 423 pass, 1 skip. `92625a9`, PR #158 merged.

2026-04-19 (X21 Stages 1–2 — embedding provenance + compat gate — PR #157) — Implemented embedding provenance schema (M2 migration): `embedding_model`, `embedding_dimension`, `parser_version`, `chunker_version` added to `chunks` table via non-destructive `ALTER TABLE`; existing rows backfilled with dimension from `LENGTH(embedding)/4` (Option B — no forced reindex on upgrade). `VectorIndex.CheckProvenance()` added: throws `EmbeddingModelMismatchException` on dimension mismatch, warns on model-name drift from `'unknown'`. `DocumentIngestor` populates all four fields on new chunks and calls `CheckProvenance` before `UpsertFileChunks`. `ChatService` surfaces mismatch as a distinct `[Error]` log. `DocumentLibraryManifest` gains `LastEmbeddingModel`/`LastEmbeddingDimension` hint fields. 5 new TDD provenance tests (416 pass, 1 skip). `449ec2e`, PR #157 merged.

## Open questions

_None._
