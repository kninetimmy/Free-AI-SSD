# Free-AI-SSD

Prepare a portable SSD with Ollama + LLMs once, then run them offline on any Windows PC.

## 🚀 Quick Start (No CLI Required)

1. Download `Free-AI-SSD.zip` from GitHub Releases.
2. Extract it anywhere on your Windows machine.
3. Run `FreeAiSsd.PrepApp.exe`.
4. Select your external SSD.
5. Use **Model Manager** to add/select model tags (freeform tags are supported).
6. Pull your selected models.
7. Run **Check SSD Readiness** until every check is green.
8. Click **Finalize SSD**.
9. On the target PC, run `<SSD>\runner\FreeAiSsd.Runner.exe`.

Internet is required only while preparing the SSD in PrepApp (download/pull phase). After finalization, Runner is designed to work offline from the SSD.

## Downloads

### Official Releases

- Official downloads are published on GitHub **Releases**.
- Maintainers create them manually from **Actions → Build and Package → Run workflow**.
- Enter a version such as `0.3.0`.
- The workflow creates tag/release `vX.Y.Z` (for example `v0.3.0`) and attaches `Free-AI-SSD.zip`.

### Dev Builds

- Every CI build also publishes GitHub Actions artifacts.
- These artifacts are intended for testing/validation, not stable distribution.

## Current Features

### PrepApp (GUI)

- Drive selection with safety warnings (for example, filesystem checks).
- Model Manager:
  - Add custom model tags (freeform).
  - Status tracking includes `ConfiguredNotDownloaded`, `OnDiskOnly`, and `Ready` delineation in the grid.
  - SHA256 + size tracking for installed models.
  - Verify model integrity against stored hashes.
- SSD Readiness checklist with re-verification support.
- Atomic config writes for `config/portable-config.json`.

### Runner (GUI)

- Starts/stops Ollama directly from SSD.
- Uses SSD-stored models and config.
- Sends prompt → local Ollama API → response.
- Writes logs to SSD.

- Remove/Delete options: remove from config only, or delete from disk via `ollama rm` using SSD model path.
- Orphaned (on-disk-only) models can be added to config from the grid.
- Drive Preparation section can format removable drives as NTFS with a custom label (default `Portable AI`) and then prepare SSD folders.
- Formatting requires running PrepApp as Administrator and explicit `ERASE` confirmation.

## SSD Layout & Integrity

PrepApp creates and uses this SSD layout:

- `tools/`
- `tools/ollama/`
- `models/`
- `models/blobs/`
- `config/`
- `logs/`
- `cache/`
- `runner/`

Integrity behavior:

- Ollama archive download supports SHA256 verification.
- For each model, SHA256, size, and last-verified timestamp are stored in config.
- **Check SSD Readiness** can re-verify installed models and update verification status.

## Offline Use

After PrepApp setup is complete:

1. Connect the SSD to another Windows PC.
2. Run `<SSD>\runner\FreeAiSsd.Runner.exe`.
3. Runner starts Ollama from SSD paths and serves locally.

No internet is required for normal offline inference after assets are prepared.

<details>
<summary><b>Developer / Build from Source (CLI)</b></summary>

### Prerequisites

- Windows
- .NET 8 SDK

### Build

```powershell
dotnet build FreeAiSsd.sln
```

### Stage runner payload

```powershell
./build.ps1
```

### Run PrepApp from source

```powershell
dotnet run --project prep-app
```

Notes:

- Intended for contributors only.
- End users should download the ZIP from Releases instead.

</details>


Offline prerequisites
- Release ZIP now includes offline installers under tools/prereqs with prereqs-manifest.json. PrepApp copies them to <SSD>\tools\prereqs during finalize.
- On first run, Runner checks compatibility (GPU/CPU/OS) and dependency presence.
- If a required dependency is missing, Runner can install it offline from SSD media using <SSD>\tools\prereqs\prereqs-manifest.json and may request Administrator elevation.
