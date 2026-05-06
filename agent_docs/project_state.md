# Project State

Last updated: 2026-05-06 (PR #184 merged; Mac runner artifact launch-tested)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

Between tasks. PR #184 (MAC7 docs wrap-up) merged at `fa055c9`. Next
implementation target is MAC8: Mac document management.

## Recently shipped

- **PR #184 - MAC7 merge wrap-up - merged `fa055c9` (2026-05-06).** Docs-only follow-up to PR #183: marks MAC7 as done in `mac_project_backlog.md`, moves PR #183's full summary into `Recently shipped`, and reorders Next up so MAC8 is #1. CI `windows-build` + `mac-runner-build` green; no runtime changes.

- **PR #183 - MAC7 RAG parity on Mac - merged `23245f7` (2026-05-06).** Mac chat now routes through the MAC6 sidecar `/api/chat` path instead of direct Ollama, displays citations/sources in Swift, and preserves MAC5's stdin-only plaintext-config invariant. `RunnerLocalApiService` forwards `ChatService` logs through the API logger and includes `ragWarning` in `/api/chat` retrieval-failed responses. Added `MacRunnerHostRagParityTests` covering real Mac-host DI against a deterministic fake Ollama endpoint for `/api/chat`, `/api/chat/stream`, returned sources, and embedding dimension mismatch warnings. CI passed on final run `25447266282`: `windows-build` ran restore, build, full `dotnet test`, WPF guardrails, and publishes; `mac-runner-build` ran Swift tests, Mac host publish/smoke, runner-core/CLI sanity, and Runner.app bundle.

- **PR #182 - MAC6 follow-up hardening - merged `66a94d9` (2026-05-06).** Follow-up to PR #181 fixing Mac Network Mode sidecar startup config, fail-closed host startup when `RunnerLocalApiService` does not actually start, static-file serving from the published RunnerCore content root instead of `<ssdRoot>/wwwroot`, and executable-bit fallback for the Mac host binary. CI `windows-build` and `mac-runner-build` both passed, including `dotnet build`, `dotnet test`, Swift tests, Mac host publish, Mac host smoke, and Runner.app bundle.

- **PR #181 - MAC6 Mac LAN API host + Companion compatibility + X4 plumbing - merged `3557f9c` (2026-05-06).** New `mac-runner-host/` net8.0 sidecar project hosts `RunnerLocalApiService` on osx-arm64 self-contained, reusing the runner-core implementation byte-for-byte. Swift `mac-runner` spawns the sidecar on Network Mode toggle, hands the unlocked PortableConfig over stdin (plaintext never touches disk on Mac), and tears it down on Lock / app background / app terminate. Added `NoOpSpeechToTextService` + `NoOpTtsProvider` in runner-core so hosts without Whisper/SAPI/Piper produce clean 403/503 via existing config gates. `UseDefaultFiles` + `UseStaticFiles` middleware added to `RunnerLocalApiService` so X4 SPA assets dropped at `runner-core/wwwroot/chat/` are served from both Windows and Mac Kestrel with no platform-specific code. CI `windows-build` and `mac-runner-build` both passed; Mac CI publishes the host, runs an end-to-end bash smoke (handshake -> /api/health -> /api/chat -> clean shutdown), and bundles into `Runner.app/Contents/Resources/runner-host/`. Decision recorded ("MAC6 Mac net8.0 sidecar host: approved exit ramp from MAC5") with the boundary that encrypted-config IO stays Swift-authoritative. Manual real-Mac smoke (Windows Companion -> Mac Runner over LAN; Network Mode with real Ollama) called out as deferred gaps.

- **PR #179 - MAC5 macOS encrypted config unlock/save - merged `a6167ad` (2026-05-05).** Native Swift port of `SsdEncryption` at `mac-runner/Sources/SsdEncryption.swift`: PBKDF2-HMAC-SHA256 via CommonCrypto, AES-256-GCM via CryptoKit, two-file atomic commit with rollback on state-rename failure, plaintext-migration mirror of `TryMigratePlaintextAsync`. Mac runner UI gains an unlock sheet, a Lock button, and zeroizes the derived key on app background, app terminate, manual lock, and `deinit`. Cross-language format pin via committed Swift-produced fixture under `tests/Fixtures/MacEncryptedConfig/csharp-encrypted/` that `MacEncryptedConfigCrossLanguageTests` round-trips on Windows CI; both sides assert the JSON key shape so a silent C# `JsonNamingPolicy` change fails CI. Deliberate format-duplication waiver recorded in `agent_docs/project_decisions.md` (rationale: avoid cross-arch .NET hosting on Apple Silicon for a small, stable surface Apple ships native primitives for; exit ramp left open for MAC6 to consolidate back into `IConfigStore` if a Mac .NET host lands later). CI `windows-build` and `mac-runner-build` both passed.

- **PR #178 - MAC4 merge wrap-up - merged `f42ffa8` (2026-05-05).** Dashboard updates after MAC4: backlog status, next-up reordering, and the 2026-05-05 Mac Ollama trust gate decision entry. No runtime changes.

- **PR #177 - MAC4 macOS Ollama lifecycle + runtime trust gate - merged `648fcd9` (2026-05-05).** Generalized `OllamaPackageTrustPolicy` so `DefaultMacPackage` (pinned to Ollama v0.5.7) is a first-class peer to `DefaultWindowsPackage`, with a shared `ValidateExecutionAttestationCore` validator. Added `MacOllamaLifecycleService` in `runner-core/` (plain `net8.0`) with trust-gate, loopback bind, `OLLAMA_MODELS`, and argument-array `serve` launch. Apple Silicon (arm64) slice check runs in pure managed code via the new `MachOArchInspector`, so Windows-side PrepApp can refuse non-arm64 payloads without `lipo`. `ArtifactStagingService.StageMacOllamaAsync` now goes through `MacOllamaStagingPipeline` (verify SHA-256 + arm64 + write attestation; scrub partial dir on failure). Swift `mac-runner` re-checks the on-SSD attestation at every launch and refuses on missing / malformed / URL-mismatched / SHA-mismatched records. CI `windows-build` and `mac-runner-build` both passed.

- **PR #176 - macOS runner CI build enabled - merged `7870eb6` (2026-05-05).** Codex's small follow-up to MAC3 enabling the macOS runner job in `.github/workflows/build.yml`. No runtime changes.

- **PR #175 - MAC3 platform-neutral Runner core - merged `5c7311d` (2026-05-05).** Added `runner-core/FreeAiSsd.RunnerCore.csproj` as a plain `net8.0` home for platform-neutral Runner chat, document operations, model management, local API endpoint logic, and core service contracts. Windows process, voice, HOTAS/PTT, DCS import, and system-resource probes remain in the WPF Runner host behind adapters. `windows-build` passed; mac runner/package jobs were skipped by workflow settings.

- **PR #174 - MAC1 supported Mac baseline - merged `16eb729` (2026-05-05).** Recorded Apple Silicon-only Mac support with macOS 11 Big Sur minimum, arm64-only Free-AI-SSD app artifacts, exFAT as the supported shared Windows + macOS SSD filesystem, NTFS as Windows-only, APFS deferred until Mac-native prep exists, and Swift/SwiftUI as the default thin native Mac UI over shared/core services. `Build and Package` run #433 passed.

- **PR #173 - MAC2 platform guardrails - merged `72139ac` (2026-05-05).** Added the macOS platform dependency audit, guardrail tests for the portable/shared boundary, and the MAC2 decision/backlog updates. `Build and Package` run #431 passed; PR #172 was closed/recreated as #173 because the GitHub connector hit the known draft-ready schema issue.

- **PR #171 - macOS support merge wrap-up - merged `3e5e831` (2026-05-05).** Updated the dashboard after the macOS support backlog merge; no runtime changes.

- **PR #170 - macOS support backlog - merged `a1d63c2` (2026-05-05).** Added the macOS support track, corrected README / QUICKSTART so macOS is described as a limited Swift direct-Ollama beta, added the MAC1 execution prompt, and passed `windows-build`.

- **PR #167 - F4 Stage 1 wrap-up docs - merged `8ada5e4` (2026-05-05).** Resolved conflicts against latest `main` via branch merge commit `34d0a4e`; `windows-build` passed.

## Next up

1. **MAC8** - Mac document management (library CRUD + ingestion).
2. Cross-platform PrepApp parity (**MAC16/MAC17/MAC18**) sequences after Runner parity (MAC4-MAC8). Decision recorded 2026-05-05; APFS dropped from supported targets, exFAT is universal. MAC17 is now unblocked from the encrypted-config side (MAC5 done).
3. Track **MAC10a** before broad Mac distribution: Windows PrepApp OS compatibility selector preselecting NTFS vs exFAT.
4. **X4** is unblocked from the host side: MAC6 wired the static-file middleware on both platforms (PR #181); X4 only needs to drop SPA assets at `runner-core/wwwroot/chat/`.
5. For non-Mac work, pick from `H3`, `F4` follow-up, `B2`, `F2`, or `R1 Stage 2`.

**RAG audit backlog:** X17-X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 -> X15 (expanded) -> X19 -> X20 -> X22 -> X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred -- workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

See `project_backlog.md` for full general backlog details. See
`agent_docs/mac_project_backlog.md` for the macOS support track.

## Last session

2026-05-06 (PR #184 merge + Mac runner launch smoke, `fa055c9`) - User merged PR #184 (the MAC7 docs wrap-up). Reviewed the MAC7 implementation in PR #183 post-merge: `RunnerLocalApiService` change is surgical, `MacRunnerHostRagParityTests` (real DI against a fake Ollama Kestrel server with deterministic embeddings) is arguably better Mac-side coverage than Windows currently has, and the architectural call to require the sidecar (no direct-Ollama fallback) preserves the RAG invariant. Three small follow-ups flagged: Gemini's `id: \.self` SwiftUI nit at `mac-runner/Sources/main.swift:627` (mitigated server-side by `RagPromptBuilder.cs:54` `CitationBuilder.BuildDistinct` but cheap to tighten), `_chatService.LogMessage` event unhooks only in `DisposeAsync` not `StopAsync`, and Swift-side auth-key selection is reimplemented locally rather than shared. With user's Windows machine awaiting a replacement PSU and no SSD prep available, validated the Mac release path another way: downloaded `mac-runner-artifact` from CI run `25448329363` (head `f74b70b`), stripped quarantine, launched Runner.app — bundle opens cleanly with all MAC1-MAC7 controls present (Select SSD, Start, Stop, Lock, Model dropdown, prompt/response panes, Network Mode toggle, status "Stopped"). User confirmed via screenshot. App icon flagged as future work (added as MAC10b in `mac_project_backlog.md`).

2026-05-06 (PR #183 MAC7 merge wrap-up, `23245f7`) - User merged PR #183. Fast-forwarded local `main` to `origin/main`. MAC7 is now shipped on `main`; next Mac implementation target is MAC8.

## Open questions

- Before a public signed Mac beta, verify whether the nested `payload/mac/Runner.app.zip` preserves the stapled notarization ticket after users download and extract the cross-platform ZIP. If not, ship a standalone notarized app ZIP or DMG.
- **MAC6 manual smoke (deferred until a Mac on the same LAN as a Windows Companion):** Windows Companion discovers and connects to a Mac-hosted Runner with Bearer auth + /api/health + /api/chat; Network Mode toggle with real Ollama serving a real model end-to-end. Bare-bundle launch (no SSD) was sanity-checked 2026-05-06 against the CI `mac-runner-artifact` from run `25448329363` — the bundle opens cleanly on a Mac without Windows tooling.
