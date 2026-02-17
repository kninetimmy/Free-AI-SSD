# Free-AI-SSD

Free-AI-SSD is a Windows-first toolkit for preparing an external SSD so Ollama + selected models can run portably from that SSD across Windows 10/11 machines.

## Downloads (no local build required)

For normal usage, you do **not** need to build from source.

1. Download the `Free-AI-SSD.zip` bundle from GitHub **Releases** or **Actions artifacts**.
2. Extract the ZIP.
3. Run `FreeAiSsd.PrepApp.exe`.
4. Click **Finalize SSD** to:
   - download/extract Ollama,
   - pull selected models,
   - stage the packaged `runner-publish` payload to SSD `runner/`,
   - write portable config.

First setup requires internet access. After finalize completes, the SSD can be used offline.

## Architecture (Iteration 1 Vertical Slice)

The solution is split into 3 projects:

- `prep-app/` (WPF GUI): run on an online host machine to prepare the SSD.
- `runner/` (WPF GUI): copied to SSD and launched later on any target machine.
- `shared/` (.NET library): drive inspection, config, download, process, and logging utilities.

### Chosen Ollama distribution strategy

**Approach:** stage an Ollama Windows binary package on the SSD (portable runtime style) rather than requiring a per-host install.

- Prep downloads `ollama-windows-amd64.zip` (configurable URL), resumes partial downloads, and extracts it to `tools/ollama` on the SSD.
- Prep then uses the staged `ollama.exe` with `OLLAMA_MODELS=<SSD>\\models` to pull selected models directly onto SSD.
- Runner later executes the same staged `ollama.exe` from SSD.

**Why this approach is reliable for portable/offline goals:**

- avoids system-wide install requirements on each host,
- keeps model/data on SSD,
- supports offline run after prep because runtime + model payloads are pre-staged.

If a future Ollama release changes packaging, update the URL and extraction logic in `prep-app`.

## SSD Layout

Created by prep under SSD root:

- `tools/ollama/` - staged Ollama binaries.
- `models/` - Ollama model store.
- `models/blobs/` - model blob content.
- `config/portable-config.json` - persisted portable settings.
- `logs/` - prep and runner logs.
- `cache/` - downloaded archives.
- `runner/` - published runner executable + dependencies.

## Offline Mode

After prep completes:

1. Plug SSD into another Windows PC.
2. Launch `runner/FreeAiSsd.Runner.exe` from SSD.
3. Runner reads `config/portable-config.json`, starts `tools/ollama/ollama.exe serve`, and points Ollama storage to SSD.
4. Runner can send generation requests to local Ollama API without network.

## Path and process behavior details

### Detecting “this SSD” in runner

Runner uses `AppContext.BaseDirectory`:

- if executable is in `...\\runner\\`, it resolves SSD root to parent folder,
- then loads `config\\portable-config.json` from SSD root.

### Enforcing SSD model/data location

Both prep and runner set:

- `OLLAMA_MODELS=<SSD>\\models`
- `OLLAMA_HOST=127.0.0.1:<port>`

Runner also sets `WorkingDirectory` to the staged Ollama folder.

### Port selection/conflicts

Config stores preferred port (default `11434`).
Runner resolves an available port once at startup, stores it as the active port, and reuses that same port for API calls and browser launch.

### Downloads + integrity

- `DownloadManager` supports resumable archive download via HTTP Range and `.part` files.
- Optional SHA256 verification is supported when expected hash is provided in `DownloadRequest`.
- Current vertical slice leaves model integrity to Ollama pull semantics.

### Multiple host machines

Persisted on SSD:

- Ollama runtime package,
- all pulled models,
- portable config,
- logs.

Host-specific state should remain minimal. Some unavoidable temporary writes may occur in system temp directories by .NET/Windows process runtime.

## Current GUI features (vertical slice)

### Prep app

- select target drive,
- filesystem warning for non-NTFS,
- choose model list (`llama3.2:3b`, `qwen2.5:3b`),
- finalize flow:
  - create folders,
  - download/extract Ollama package,
  - pull selected models into SSD model path,
  - stage runner artifacts,
  - write config JSON.

### Runner app

- Start/Stop Ollama,
- model dropdown populated from config,
- simple prompt textbox + send button using local `/api/generate`,
- Open browser button for local endpoint,
- writes logs to SSD `logs/`.

## Build and run

Prerequisites:

- Windows 10/11
- .NET 8 SDK
- internet access for prep phase

### Recommended: one-step build + staging

```powershell
./build.ps1
```

`build.ps1` performs all required setup steps:

1. Builds `FreeAiSsd.sln` in Release.
2. Publishes runner as self-contained single-file `win-x64`.
3. Copies runner publish output to `prep-app/bin/Release/net8.0-windows/runner-publish/` so prep can stage runner artifacts without manual copy steps.

You can still run the commands manually if needed, but `build.ps1` is the preferred workflow.

### Run prep app

```powershell
dotnet run --project prep-app/FreeAiSsd.PrepApp.csproj
```

Select drive, choose models, click **Finalize SSD**.

### Run portable runner from SSD

```powershell
<SSD>\\runner\\FreeAiSsd.Runner.exe
```

## Limitations (known)

- Uses assumed Ollama zip package URL; validate against current official release channel.
- Model pull progress is logged from CLI output (not token-perfect progress bars yet).
- No code-signing validation yet; checksum hook exists and should be wired to a trusted manifest.
- GPU acceleration selection is not implemented yet (defaults to CPU-friendly behavior).
- AutoRun for removable media is intentionally not used.
