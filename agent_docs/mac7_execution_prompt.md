# MAC7 Execution Prompt

- Item: `MAC7 - RAG parity on Mac`
- Status: `approved`
- Saved: `2026-05-06`
- Recommended execution model: `claude-opus-4-7` or `gpt-5.4`

Architectural decision (locked in this prompt): MAC7 uses the existing
net8.0 `mac-runner-host` sidecar from MAC6 and the shared RunnerCore
RAG pipeline. Do not port embeddings, vector search, prompt packing, or
citations to Swift. The Swift app remains a thin native UI and must call
the sidecar `/api/chat` / `/api/chat/stream` endpoints for RAG-backed
chat. Encrypted-config IO stays Swift-authoritative; the sidecar receives
the in-memory PortableConfig over stdin only, never through a plaintext
file.

Use the prompt below to resume in a fresh session after approval.

```text
Implement MAC7 only in /Users/stephenelswick/Free-AI-SSD.

Start by reading:
- agent_docs/project_state.md
- agent_docs/project_arch.md (Security invariants, Mac Runner sidecar host,
  RAG pipeline, SSD runtime layout, Network surface)
- agent_docs/project_decisions.md (MAC5 native Swift encryption; 2026-05-06
  MAC6 sidecar host exit ramp)
- agent_docs/mac_project_backlog.md (MAC7 entry, plus MAC8 boundary)
- agent_docs/mac_platform_dependency_audit.md (thin Swift over shared/core
  services; do not duplicate RAG in Swift)
- agent_docs/mac6_execution_prompt.md for sidecar context only
- mac-runner-host/Program.cs
- mac-runner-host/HostLifetime.cs
- mac-runner/Sources/main.swift
- mac-runner/Sources/MacRunnerHostController.swift
- runner-core/Services/ChatService.cs
- runner-core/Services/IChatService.cs
- runner-core/Services/RunnerLocalApiService.cs
- runner-core/Services/DocumentOperationsService.cs
- shared/Documents/DocumentLibraryManager.cs
- shared/Documents/DocumentIngestor.cs
- shared/Documents/EmbeddingClient.cs
- shared/Documents/VectorIndex.cs
- shared/Documents/RagPromptBuilder.cs
- shared/Documents/CitationBuilder.cs
- shared/Documents/EmbeddingModelMismatchException.cs
- shared/PortableConfig.cs
- tests/ChatServiceTests.cs
- tests/RagPipelineIntegrationTests.cs
- tests/RunnerLocalApiServiceTests.cs
- tests/MacRunnerHostSmokeTests.cs
- .github/workflows/build.yml

Goal:
Mac chat must use the same RAG behavior as Windows when an active document
library already exists on the SSD: query embeddings, vector search, prompt
packing, and returned citations/sources. The Mac UI must display citations
from the API response. MAC7 does not implement Mac document CRUD, file
pickers, watched folders, ingestion, rebuild, or sweep UI; those remain
MAC8.

Scope boundary:
- In scope: RAG-backed `/api/chat` and `/api/chat/stream` behavior on the
  Mac sidecar; Swift UI calling the sidecar instead of direct Ollama for
  normal chat; citations/source display in the Mac UI; clear user-visible
  RAG retrieval/mismatch warnings.
- Out of scope: creating/selecting libraries on Mac, adding/removing files,
  ingestion/rebuild/sweep surfaces, DOCX support, OCR, voice/STT/TTS,
  HOTAS/PTT, DCS import UI, X4 SPA, signing/notarization.
- Existing supported document formats remain PDF, TXT, Markdown only.
- Do not weaken security invariants: AES-256-GCM config encryption,
  SHA-256 + URL allowlist for downloaded binaries, `PathGuards` for path
  handling, and `ProcessRunner.ArgumentList` for process launches.
- Do not write plaintext PortableConfig to disk from Mac. The Swift app may
  keep the unlocked config dictionary in memory and pass it to the host over
  stdin exactly as MAC6 does.

Repo context:
- `ChatService` already prepares RAG context when
  `PortableConfig.ActiveDocumentLibraryId` is set. It loads the manifest,
  queries Ollama embeddings through `EmbeddingClient`, checks vector-index
  provenance, searches `VectorIndex`, builds the packed prompt with
  `RagPromptBuilder`, and returns `ChatResponse.Sources`.
- `RunnerLocalApiService` already exposes `/api/chat` and `/api/chat/stream`
  and serializes sources / `usedRagContext`. MAC7 should not fork those
  endpoints for Mac.
- `mac-runner-host/HostLifetime.cs` already wires real `ChatService` unless
  `--test-mode` is passed. MAC7 should add focused tests proving that the
  real path works in a Mac-compatible host shape.
- `mac-runner/Sources/main.swift` still sends normal chat directly to
  Ollama `/api/generate`. That bypasses RAG. MAC7 must move normal chat to
  the sidecar API path when available.

Implement:

1. Test-first host RAG parity.
   - Add focused tests that construct or spawn the Mac host with real
     `ChatService`, a temporary SSD layout, an active document library,
     and a deterministic fake Ollama server/handler for both embeddings and
     generate responses.
   - Verify `/api/chat` returns HTTP 200 with:
     - response text from the fake generate path,
     - `usedRagContext = true`,
     - non-empty `sources`,
     - a source/citation that names the seeded document.
   - Verify `/api/chat/stream` emits NDJSON `complete` with
     `usedRagContext = true` and non-empty `sources`.
   - Verify an embedding model mismatch or missing/mismatched vector-index
     provenance is surfaced clearly. Acceptable behavior is the existing
     `ChatResult.RagRetrievalFailed` path: chat still answers without RAG,
     `X-RAG-Status` is `retrieval-failed`, and the log/status text
     includes the mismatch detail.
   - Keep tests runnable on Windows CI where possible by testing
     `HostRunner.RunAsync` / service wiring in-process. Only published
     osx-arm64 binary smoke should stay Mac-only skipped on non-macOS hosts.

2. Harden HostLifetime DI only if tests show a gap.
   - Prefer using the existing `ChatService`, `DocumentLibraryManager`,
     `EmbeddingClient`, and `DocumentIngestor` wiring as-is.
   - Do not introduce a Mac-specific RAG service or a Swift RAG layer.
   - If a small host seam is needed for deterministic tests, keep it internal
     and test-scoped; do not affect production behavior.

3. Swift Mac chat path.
   - Update `RunnerViewModel.sendPrompt()` so normal Mac chat calls
     `RunnerLocalApiService` through the sidecar rather than direct Ollama
     `/api/generate` when the sidecar is running.
   - If Network Mode / sidecar is not running, start or require the same
     local sidecar path used by MAC6 rather than silently bypassing RAG. A
     clear fallback/status message is better than a hidden direct-Ollama path
     that drops citations.
   - Send requests to `/api/chat` with `{ model, prompt }`.
   - Include the configured API key header when `networkRequireApiKey` is
     enabled. Use Bearer or `X-API-Key`; match existing API semantics.
   - Parse `responseText`, `sources`, and `usedRagContext` from the JSON
     response. Preserve graceful error display for 400/401/503 responses.
   - For streaming, either wire `/api/chat/stream` end-to-end if it fits the
     current Swift UI cleanly, or leave the UI on non-streaming `/api/chat`
     while tests prove the host stream endpoint. Do not fake streaming in
     Swift.

4. Swift citation/source display.
   - Add view-model state for `sources` / `usedRagContext` / optional RAG
     warning.
   - Display returned sources below the answer in the existing Mac UI. Keep
     the UI compact and native; no marketing copy or tutorial text.
   - Clear stale sources on a new prompt, failed request, lock, or SSD
     switch.
   - If RAG retrieval fails but chat still returns a response, show the
     response and surface a concise warning. Do not hide the answer.

5. Config and lifecycle correctness.
   - Ensure the sidecar receives the current in-memory config dictionary
     containing `activeDocumentLibraryId`, `embeddingModelName`,
     `retrievalTopK`, and `minimumSimilarityThreshold`.
   - If the user toggles settings that affect API auth or active library
     in the Swift app during MAC7, send a `config-update` to the host using
     the existing MAC6 protocol. If there is no Mac UI for changing active
     library yet, do not add one in MAC7.
   - Lock/background/terminate must still shut the sidecar down before
     zeroing unlock material.

6. Documentation updates.
   - Update `agent_docs/mac_project_backlog.md` MAC7 status/outcome after
     implementation.
   - Update `agent_docs/project_state.md` with the branch/PR/test status.
   - Update README / docs/QUICKSTART only where public Mac limitation text
     is now wrong. Do not imply MAC8 document management exists.
   - If a durable architectural note is needed, append to
     `agent_docs/project_decisions.md`; otherwise keep MAC6's sidecar
     decision as the governing entry.

Acceptance criteria:
- A Mac-hosted Runner sidecar honors `ActiveDocumentLibraryId` for existing
  indexed libraries.
- `/api/chat` returns citations/sources and `usedRagContext=true` when
  relevant context is found.
- `/api/chat/stream` returns final sources and `usedRagContext=true` in the
  completion event when relevant context is found.
- Embedding model mismatch / index provenance mismatch is clear and does not
  crash the host.
- The Swift Mac UI no longer silently bypasses RAG by posting normal chat
  directly to Ollama.
- The Swift Mac UI displays returned sources.
- MAC5 plaintext-config invariant still holds.
- MAC8 remains unimplemented.

Suggested verification:
- `dotnet build shared/FreeAiSsd.Shared.csproj`
- `dotnet build runner-core/FreeAiSsd.RunnerCore.csproj`
- `dotnet test tests/FreeAiSsd.Tests.csproj --filter "FullyQualifiedName~ChatServiceTests|FullyQualifiedName~RagPipelineIntegrationTests|FullyQualifiedName~RunnerLocalApiServiceTests|FullyQualifiedName~MacRunnerHostSmokeTests" --verbosity normal`
- `dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal`
- On macOS CI or a real Mac:
  - `dotnet publish mac-runner-host/FreeAiSsd.MacRunnerHost.csproj -c Release -r osx-arm64 --self-contained true /p:PublishSingleFile=true`
  - Swift compile/test path from `.github/workflows/build.yml`
  - Manual smoke: Windows-prepped SSD with an already indexed TXT/Markdown/PDF
    library, Mac Runner unlocked, ask a question whose answer appears in the
    library, confirm citation/source appears.

Branch / PR:
- Branch: `mac7-rag-parity`
- PR title: `[codex] MAC7 RAG parity on Mac`
- Watch CI before reporting complete. Do not merge without explicit user
  confirmation.
```
