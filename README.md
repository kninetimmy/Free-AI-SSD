# Free-AI-SSD

Prepare a portable SSD once (online), then run local AI fully from that SSD (offline) on target machines.

Free-AI-SSD ships two desktop apps:
- **PrepApp** (run on an online machine once): prepares drive structure, stages Ollama + prerequisites, and pulls selected models.
- **Runner** (run later on target machine): starts SSD-hosted Ollama locally and provides chat + Reference Documents (offline RAG).

---

## What this project does

Free-AI-SSD is a practical workflow for carrying a self-contained AI environment:

1. **Online prep phase (PrepApp)**
   - Select SSD
   - Pull Ollama/models
   - Stage offline dependencies
   - Finalize SSD
2. **Offline run phase (Runner)**
   - Start Ollama from SSD paths
   - Chat locally on `127.0.0.1`
   - Optionally use **Reference Documents** libraries stored on SSD

After prep/finalize, normal inference and RAG usage are designed to run without internet.

---

## Download and install

### Stable release (recommended)
- Download **`Free-AI-SSD-win.zip`** from GitHub Releases.
- Extract anywhere on Windows.
- Run `FreeAiSsd.PrepApp.exe`.

### Optional beta cross-platform bundle
- **`Free-AI-SSD-beta-crossplatform.zip`** includes mac artifacts and enables mac target prep options.
- mac build is currently unsigned/not notarized (expect Gatekeeper prompts).

### CI artifacts
- GitHub Actions artifacts are available for validation/testing.
- Prefer Releases for normal end-user use.

---

## Quick Start (Windows stable)

1. Open `FreeAiSsd.PrepApp.exe`.
2. Select target external SSD.
3. Add/select models in **Model Manager**.
4. Pull models.
5. Run **Check SSD Readiness** until checks are acceptable.
6. Click **Finalize SSD**.
7. Move SSD to destination machine.
8. Run Runner from SSD:
   - Windows: `<SSD>\windows\runner\FreeAiSsd.Runner.exe`
   - macOS (beta flow): `<SSD>/mac/Runner.app`

---

## Offline usage model

### Internet required
- During PrepApp download/pull/staging operations.

### Internet not required (expected)
- Runner start/stop.
- Chat requests against local Ollama host.
- Reference Documents indexing/retrieval using local files + local Ollama embed/generate APIs.

### Important exception
- If the embedding model is not already present on SSD, **Pull embedding model** may require temporary internet access (via local Ollama pull).

---

## Reference Documents (Offline RAG)

Runner includes a **Reference Documents** panel for local document-grounded chat.

### Supported file types
- `.pdf`, `.txt`, `.md`, `.json`, `.csv`

### Typical workflow
1. Start Ollama in Runner.
2. In **Reference Documents**, create a library (or select existing).
3. Add files directly (**Add files**), or attach folders (**Add folder**) to watch.
4. Run **Sweep folders now** to ingest new/changed files in watched folders.
5. Run **Rebuild index** when you want a full re-index from tracked files.
6. Ask a question in chat with the library selected.

### How citations/sources work
- Retrieved chunks are injected into the prompt with inline citations such as:
  - `[manual.pdf p.12]`
  - `[notes.txt]`
- The **Sources** list in Runner shows the distinct citations actually used in the injected context.
- If no active library is selected, chat falls back to plain prompt behavior. If a library is searched but no chunks meet the similarity threshold, the prompt notes "No relevant documents found" so the model knows to say it doesn't know rather than hallucinate.

### Current limitations
- PDF extraction quality depends on embedded text layer quality.
- Scanned/image-only PDFs may extract poorly without OCR.
- DOCX is not supported in current file parser.
- Retrieval uses SQLite with SIMD-optimized cosine search and is optimized for personal/small-medium libraries (up to ~10,000 chunks; a warning is logged if exceeded).

---

## Recent Improvements

**Cosine similarity threshold** — RAG retrieval now discards chunks below a configurable minimum cosine similarity score (default 0.3), preventing low-relevance content from polluting the LLM context. Configurable via `minimumSimilarityThreshold` in `config/portable-config.json`.

**Binary BLOB embedding storage** — Embeddings are stored as raw binary BLOBs in SQLite instead of JSON text, reducing index file size by ~60% and eliminating serialization overhead on every query. Existing indexes are migrated automatically on first open.

**Parallel embedding ingestion** — Document ingestion embeds chunks concurrently under a bounded concurrency cap (default 4), replacing the previous sequential per-chunk loop. Large libraries index significantly faster. Configurable via `maxEmbeddingConcurrency` in `config/portable-config.json`.

**SIMD-optimized vector search** — Embeddings are pre-normalized at write time so search reduces to a dot product. The dot product itself is SIMD-accelerated via `System.Numerics.Vector<float>` (no new native dependencies — built into .NET 8), and top-K selection uses an O(N log K) priority queue instead of a full sort.

**Runner service layer** — `MainWindow.xaml.cs` has been refactored from a 983-line monolith into a thin UI shell. Business logic now lives in four injectable, interface-backed services: `OllamaLifecycleService`, `ModelManagementService`, `DocumentOperationsService`, and `ChatService`. Each service has no UI references and can be unit-tested independently.

---

## SSD layout overview (high level)

Free-AI-SSD prepares a layout similar to:

- `config/` — portable config + runtime state
- `models/` — Ollama model store
- `logs/` — app logs
- `docs/libraries/` — Reference Documents library files/manifests/index DB
- `windows/runner/` — Runner app payload
- `windows/tools/ollama/` — staged Ollama runtime
- `windows/tools/prereqs/` — offline prerequisite installers + manifest
- `mac/` — beta mac payloads/tools (when included)
- `cache/` — prep-time download cache

---

## Troubleshooting

### Runner won’t start / dependency warnings
- Use Runner’s **Re-run dependency check**.
- Ensure SSD includes `windows/tools/prereqs` and manifest.
- If prereq bundle is missing or invalid, reconnect SSD to online PrepApp machine and run **Update Prereqs**.

### Missing embedding model while offline
- RAG indexing/retrieval can fail if embedding model is not installed.
- Start Ollama and click **Pull embedding model**.
- If fully offline, connect temporarily to internet, pull once, then return offline.

### PDF citations/pages seem wrong or sparse
- Confirm source PDF has machine-readable text.
- For scans/image PDFs, run OCR externally before importing.

### .NET/runtime prerequisites on target machine
- Runner can install staged prerequisites offline (Windows) when bundle is valid.
- If install is blocked, refresh prereqs from PrepApp online and retry.

---

<details>
<summary><b>Developer (build from source)</b></summary>

### Prerequisites
- Windows
- .NET 8 SDK

### Build + test
```powershell
dotnet restore FreeAiSsd.sln
dotnet build FreeAiSsd.sln -c Release
dotnet test FreeAiSsd.sln -c Release
```

### Stage runner payload into PrepApp output
```powershell
./build.ps1 -Configuration Release -Runtime win-x64
```

### Run PrepApp from source
```powershell
dotnet run --project prep-app
```

### Runner architecture
Runner's business logic is split into four services under `runner/Services/`:
- `OllamaLifecycleService` — process start/stop, port resolution, trust validation
- `ModelManagementService` — installed model listing, sizing warnings, embedding model pull
- `DocumentOperationsService` — library CRUD, file ingestion, folder sweep, index rebuild
- `ChatService` — RAG-augmented prompt sending via Ollama `/api/generate`

Each implements an interface (`IOllamaLifecycleService`, etc.) for unit testing without a UI host.

</details>

---

## macOS signing/notarization (CI)

The workflow supports optional signing/notarization via repository secrets:
- `MACOS_CERT_P12_BASE64`
- `MACOS_CERT_PASSWORD`
- `APPLE_TEAM_ID`
- `APPLE_ID`
- `APPLE_APP_SPECIFIC_PASSWORD`
- `MACOS_SIGN_IDENTITY` (optional if derived)

Signing is currently disabled by default in CI (`MAC_SIGNING_ENABLED=false`).
