# CODE REVIEW — Production Readiness Audit

## Methodology
- Performed static review across all source/config/build files in scope.
- Attempted build/test execution; local environment lacks `dotnet`, so runtime validation could not be executed.
- Review includes correctness, security, performance, maintainability, and comment accuracy checks.

## 1) Critical

### C1 — Silent partial/empty indexing can mark documents as successfully imported
- **File/lines:** `shared/Documents/DocumentIngestor.cs` lines **139-181**.
- **Problem:** embedding failures are swallowed per chunk, then `UpsertFileChunks(...)` and manifest metadata are still committed even when many/all chunks failed.
- **Why it matters:** normal usage can produce silently incomplete retrieval corpora (or zero-vector coverage) while UI/manifest imply success, causing incorrect answers and hard-to-debug data quality failures.
- **Suggested fix:** if `chunks.Count == 0` or failure ratio exceeds threshold, fail file ingestion explicitly, do not mutate manifest, and bubble/log an actionable error.
- **Status (Codex):** ✅ Fixed. Ingestion now aborts when no chunks are produced or when embedding failures exceed a defined threshold; in those failure paths it avoids `UpsertFileChunks(...)`/manifest mutation and emits actionable error context with success/failure counts.


## 2) High

### H1 — Streaming endpoint uses sync-over-async in token callback
- **File/lines:** `runner/Services/RunnerLocalApiService.cs` lines **175-178**.
- **Problem:** `WriteNdjsonAsync(...).GetAwaiter().GetResult()` blocks synchronously inside async flow.
- **Why it matters:** under slow clients/backpressure this can stall request processing threads and increase deadlock risk.
- **Suggested fix:** make callback async (`Func<string,Task>`) end-to-end and `await` writes; alternatively queue tokens to a channel and drain asynchronously.
- **Status (Codex):** ✅ Fixed. The streaming token callback path was converted to async end-to-end so NDJSON writes are awaited instead of using sync-over-async blocking (`GetAwaiter().GetResult()`), improving behavior under backpressure/slow clients.

### H2 — Companion voice handler is `async void` and runs blocking audio loop on UI context
- **File/lines:** `companion/CompanionRuntime.cs` lines **157-239**, **291-314**.
- **Problem:** `OnPttReleased` is `async void`; after awaits it calls `PlayTts`, which blocks with `Thread.Sleep` loop.
- **Why it matters:** can freeze tray/UI responsiveness and hide exceptions from callers.
- **Suggested fix:** change to `Task OnPttReleasedAsync()` and dispatch from event with safe fire-and-forget wrapper; move TTS playback wait loop off UI thread (or use playback-completed event + `TaskCompletionSource`).

### H3 — Keyboard hotkey registration ignores API failure
- **File/lines:** `companion/KeyboardPttHotkey.cs` lines **25-28**.
- **Problem:** return value of `RegisterHotKey` is ignored.
- **Why it matters:** key binding collisions or registration failures silently disable PTT with no operator feedback.
- **Suggested fix:** check return value, log Win32 error (`Marshal.GetLastWin32Error`), and show fallback guidance to user.

### H4 — Network API intentionally allows cleartext HTTP with API key auth only
- **File/lines:** `runner/Services/RunnerLocalApiService.cs` lines **60-69**.
- **Problem:** API key traverses LAN in cleartext when used off-loopback.
- **Why it matters:** on untrusted/shared LAN, credential interception enables remote command/use of host AI+audio actions.
- **Suggested fix:** add optional HTTPS mode (cert path or dev cert), or require reverse proxy/TLS termination when bind address is non-loopback.


## 3) Medium

### M1 — Default `HttpClient` in DownloadManager has no explicit timeout policy
- **File/lines:** `shared/DownloadManager.cs` lines **35-38**, usage at **64**.
- **Problem:** when instantiated without injected client, timeout/retry policy is implicit and not centrally controlled.
- **Why it matters:** long hangs or inconsistent behavior in degraded networks; difficult operational tuning.
- **Suggested fix:** inject `HttpClient` from DI with explicit timeout + retry/backoff policy; avoid ad-hoc `new HttpClient()`.

### M2 — Authenticode validation is fail-open when tooling unavailable
- **File/lines:** `shared/PrereqInstallValidator.cs` lines **226-243**.
- **Problem:** if PowerShell/signature check fails, code logs warning and accepts installer on hash-only validation.
- **Why it matters:** weakens defense-in-depth during compromised local trust/tooling scenarios.
- **Suggested fix:** make policy configurable (`strictSignatureValidation`); default strict in production builds, permissive only in explicitly opted-in/dev contexts.

### M3 — CI workflow still uses tag-pinned first-party actions (not SHA-pinned)
- **File/lines:** `.github/workflows/build.yml` lines **6-15**, **55-57**.
- **Problem:** explicit TODO acknowledges missing commit-SHA pinning for core actions.
- **Why it matters:** weaker supply-chain immutability versus fully pinned actions.
- **Suggested fix:** pin all actions to audited commit SHAs and document update procedure.

### M4 — Potentially misleading cross-platform comment in DCS locator
- **File/lines:** `shared/Documents/DcsSavedGamesLocator.cs` line **34**.
- **Problem:** comment highlights UserProfile cross-platform behavior, but logic still assumes Windows-style `Saved Games` path immediately after.
- **Why it matters:** readers may overestimate non-Windows support for auto-discovery.
- **Suggested fix:** clarify comment: profile lookup is cross-platform, but auto-detect path convention is Windows-specific.


## 4) Low

### L1 — Redundant API key headers from Companion
- **File/lines:** `companion/CompanionRuntime.cs` lines **258-262**.
- **Problem:** sends both `Authorization: Bearer` and `X-API-Key` simultaneously.
- **Why it matters:** unnecessary duplication increases header surface and log exposure chance.
- **Suggested fix:** send one canonical auth mechanism (prefer Bearer).

### L2 — Minor comment debt in workflow hardening notes
- **File/lines:** `.github/workflows/build.yml` lines **3-26**.
- **Problem:** long historical commentary mixed with active pipeline logic reduces readability.
- **Why it matters:** maintainers must scan large prolog before actionable config.
- **Suggested fix:** move rationale to `docs/` and keep concise pointers in workflow.


## Comment Accuracy/Readability Review
- **Accurate and helpful overall:** security/trust and architecture comments in `shared/Prereqs/PrereqResolver.cs`, `shared/SsdEncryption.cs`, `shared/Documents/VectorIndex.cs`, and `runner/App.xaml.cs` are consistent with implementation.
- **Inaccurate/misleading comment flagged:** `shared/Documents/DcsSavedGamesLocator.cs` line 34 (see M4).
- **Potentially excessive comments:** top-of-file historical notes in `.github/workflows/build.yml` are informative but verbose for operational maintenance (see L2).


## Files explicitly reviewed with no material issues found


### (repo root)

- FreeAiSsd.sln
- build.ps1
- global.json

### companion

- companion/App.xaml
- companion/App.xaml.cs
- companion/CompanionLog.cs
- companion/FreeAiSsd.Companion.csproj
- companion/GlobalUsings.cs
- companion/PttOverlayWindow.xaml
- companion/PttOverlayWindow.xaml.cs
- companion/SettingsWindow.xaml
- companion/SettingsWindow.xaml.cs

### mac-runner

- mac-runner/Sources/main.swift

### prep-app

- prep-app/App.xaml
- prep-app/App.xaml.cs
- prep-app/EncryptionSetupDialog.xaml
- prep-app/EncryptionSetupDialog.xaml.cs
- prep-app/EraseConfirmDialog.xaml
- prep-app/EraseConfirmDialog.xaml.cs
- prep-app/FreeAiSsd.PrepApp.csproj
- prep-app/GlobalUsings.cs
- prep-app/MacArtifactAvailability.cs
- prep-app/MainWindow.xaml
- prep-app/MainWindow.xaml.cs
- prep-app/ModelOperations.cs
- prep-app/OllamaServerHandle.cs
- prep-app/PrepTargetPreferenceStore.cs
- prep-app/RemoveModelDialog.xaml
- prep-app/RemoveModelDialog.xaml.cs
- prep-app/Resources/starter-models.json
- prep-app/Services/ArtifactStagingService.cs
- prep-app/Services/DialogService.cs
- prep-app/Services/DriveService.cs
- prep-app/Services/EncryptionService.cs
- prep-app/Services/LogService.cs
- prep-app/Services/ModelService.cs
- prep-app/Services/OllamaPackageService.cs
- prep-app/Services/PrereqService.cs
- prep-app/Services/ReadinessService.cs
- prep-app/StarterModelCatalog.cs
- prep-app/UiConverters.cs

### runner

- runner/App.xaml
- runner/App.xaml.cs
- runner/DcsAircraftImportItem.cs
- runner/DependencyInstallDialog.xaml
- runner/DependencyInstallDialog.xaml.cs
- runner/FreeAiSsd.Runner.csproj
- runner/GlobalUsings.cs
- runner/MainWindow.xaml
- runner/MainWindow.xaml.cs
- runner/PttOverlayWindow.xaml
- runner/PttOverlayWindow.xaml.cs
- runner/Services/ChatService.cs
- runner/Services/DcsBindingsImportService.cs
- runner/Services/DocumentOperationsService.cs
- runner/Services/IChatService.cs
- runner/Services/IDcsBindingsImportService.cs
- runner/Services/IDocumentOperationsService.cs
- runner/Services/IModelManagementService.cs
- runner/Services/IOllamaLifecycleService.cs
- runner/Services/IPttVoicePipelineService.cs
- runner/Services/IRunnerLocalApiService.cs
- runner/Services/ISpeechToTextService.cs
- runner/Services/ITextToSpeechService.cs
- runner/Services/ITtsProvider.cs
- runner/Services/ModelManagementService.cs
- runner/Services/OllamaLifecycleService.cs
- runner/Services/PiperTextToSpeechService.cs
- runner/Services/PttVoicePipelineService.cs
- runner/Services/StreamingTtsSpeaker.cs
- runner/Services/SystemTextToSpeechService.cs
- runner/Services/WhisperModelManager.cs
- runner/Services/WhisperSpeechToTextService.cs
- runner/UiConverters.cs
- runner/UnlockDriveDialog.xaml
- runner/UnlockDriveDialog.xaml.cs

### shared

- shared/AssemblyInfo.cs
- shared/Client/AudioCaptureService.cs
- shared/Client/HotasInputService.cs
- shared/Client/IAudioCaptureService.cs
- shared/Client/IHotasInputService.cs
- shared/Client/PttSounds.cs
- shared/DependencyChecker.cs
- shared/Documents/CitationBuilder.cs
- shared/Documents/DcsAircraftScanner.cs
- shared/Documents/DcsBatchProcessor.cs
- shared/Documents/DcsBindingModels.cs
- shared/Documents/DcsBindingParser.cs
- shared/Documents/DcsScannerModels.cs
- shared/Documents/DocumentChunker.cs
- shared/Documents/DocumentHasher.cs
- shared/Documents/DocumentLibraryManager.cs
- shared/Documents/DocumentModels.cs
- shared/Documents/DocumentParser.cs
- shared/Documents/EmbeddingClient.cs
- shared/Documents/EmbeddingSerializer.cs
- shared/Documents/RagPromptBuilder.cs
- shared/Documents/VectorIndex.cs
- shared/DriveInspector.cs
- shared/FreeAiSsd.Shared.csproj
- shared/GlobalUsings.cs
- shared/Helpers/CryptoUtils.cs
- shared/ModelSizing.cs
- shared/Models/CompanionConfig.cs
- shared/Models/PrepModels.cs
- shared/Mvvm/AsyncRelayCommand.cs
- shared/Mvvm/BaseViewModel.cs
- shared/Mvvm/RelayCommand.cs
- shared/NetUtils.cs
- shared/OllamaPackageTrustPolicy.cs
- shared/PathGuards.cs
- shared/PortableConfig.cs
- shared/PrepDriveWriteGuard.cs
- shared/PrereqManifest.cs
- shared/Prereqs/MacToolCatalog.cs
- shared/Prereqs/PrereqCatalog.cs
- shared/Prereqs/PrereqResolver.cs
- shared/ProcessRunner.cs
- shared/RunnerFirstRunState.cs
- shared/Services/IArtifactStagingService.cs
- shared/Services/IDialogService.cs
- shared/Services/IDriveService.cs
- shared/Services/IEncryptionService.cs
- shared/Services/ILogService.cs
- shared/Services/IModelService.cs
- shared/Services/IOllamaPackageService.cs
- shared/Services/IPrereqService.cs
- shared/Services/IReadinessService.cs
- shared/SsdEncryption.cs
- shared/SsdLayout.cs
- shared/SsdLogger.cs
- shared/SystemCompatibility.cs
- shared/SystemResources.cs
- shared/UI/Theme/Colors.xaml
- shared/UI/Theme/Controls.xaml
- shared/UI/Theme/LedStatusIndicator.xaml
- shared/UI/Theme/LedStatusIndicator.xaml.cs
- shared/UI/Theme/ReducedMotion.cs
- shared/UI/Theme/Shadows.xaml
- shared/UI/Theme/Theme.xaml
- shared/UI/Theme/ThemePreview.xaml
- shared/UI/Theme/ThemePreview.xaml.cs
- shared/UI/Theme/Typography.xaml
- shared/ViewModels/PrepViewModel.cs

### tests

- tests/CitationBuilderTests.cs
- tests/CompanionConfigTests.cs
- tests/DcsAircraftScannerTests.cs
- tests/DcsBindingParserTests.cs
- tests/DocumentChunkerTests.cs
- tests/DocumentHashDedupTests.cs
- tests/DocumentIngestionSecurityTests.cs
- tests/DocumentLibraryWorkflowTests.cs
- tests/DocumentParserTests.cs
- tests/FreeAiSsd.Tests.csproj
- tests/GlobalUsings.cs
- tests/ModelOperationsTests.cs
- tests/OllamaPackageTrustPolicyTests.cs
- tests/PathGuardsTests.cs
- tests/PortableConfigSaveGuardTests.cs
- tests/PrepDriveWriteGuardTests.cs
- tests/PrepViewModelTests.cs
- tests/PrereqInstallValidatorTests.cs
- tests/PrereqResolverTests.cs
- tests/RagPipelineIntegrationTests.cs
- tests/RagPromptBuilderTests.cs
- tests/RunnerLocalApiServiceTests.cs
- tests/SsdEncryptionTests.cs
- tests/VectorIndexRetrievalTests.cs

### tools

- tools/FreeAiSsd.PrereqFetch/FreeAiSsd.PrereqFetch.csproj
- tools/FreeAiSsd.PrereqFetch/Program.cs


## Prioritized Top-10 Fix List
1. Prevent silent partial/empty document ingest commits (C1).
2. Remove sync-over-async from `/api/chat/stream` token path (H1).
3. Refactor Companion PTT release path away from `async void` + blocking playback (H2).
4. Add explicit hotkey registration failure handling and user feedback (H3).
5. Add TLS support / secure deployment mode for non-loopback network API (H4).
6. Introduce centralized HttpClient timeout/retry policy for downloads (M1).
7. Add strict-mode signature enforcement for prereq installers (M2).
8. SHA-pin all GitHub Actions in CI (M3).
9. Clean up auth header duplication in Companion requests (L1).
10. Update misleading and high-volume comments to reduce maintenance drift (M4, L2).
