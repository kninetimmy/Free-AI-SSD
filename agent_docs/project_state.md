# Project State

Last updated: 2026-05-05 (MAC5 shipped)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

Nothing in flight. MAC5 merged 2026-05-05 (`a6167ad`); MAC6 (Mac LAN API host
+ Windows-Companion-to-Mac connectivity + X4 web chat surface) is the next
Runner-parity item. After MAC6: MAC7 (RAG parity) and MAC8 (Mac document
management). Cross-platform PrepApp parity (MAC16/17/18) sequences after
Runner parity per the 2026-05-05 prep parity decision.

## Recently shipped

- **PR #179 - MAC5 macOS encrypted config unlock/save - merged `a6167ad` (2026-05-05).** Native Swift port of `SsdEncryption` at `mac-runner/Sources/SsdEncryption.swift`: PBKDF2-HMAC-SHA256 via CommonCrypto, AES-256-GCM via CryptoKit, two-file atomic commit with rollback on state-rename failure, plaintext-migration mirror of `TryMigratePlaintextAsync`. Mac runner UI gains an unlock sheet, a Lock button, and zeroizes the derived key on app background, app terminate, manual lock, and `deinit`. Cross-language format pin via committed Swift-produced fixture under `tests/Fixtures/MacEncryptedConfig/csharp-encrypted/` that `MacEncryptedConfigCrossLanguageTests` round-trips on Windows CI; both sides assert the JSON key shape so a silent C# `JsonNamingPolicy` change fails CI. Deliberate format-duplication waiver recorded in `agent_docs/project_decisions.md` (rationale: avoid cross-arch .NET hosting on Apple Silicon for a small, stable surface Apple ships native primitives for; exit ramp left open for MAC6 to consolidate back into `IConfigStore` if a Mac .NET host lands later). CI `windows-build` and `mac-runner-build` both passed.

- **PR #178 - MAC4 merge wrap-up - merged `f42ffa8` (2026-05-05).** Dashboard updates after MAC4: backlog status, next-up reordering, and the 2026-05-05 Mac Ollama trust gate decision entry. No runtime changes.

- **PR #177 - MAC4 macOS Ollama lifecycle + runtime trust gate - merged `648fcd9` (2026-05-05).** Generalized `OllamaPackageTrustPolicy` so `DefaultMacPackage` (pinned to Ollama v0.5.7) is a first-class peer to `DefaultWindowsPackage`, with a shared `ValidateExecutionAttestationCore` validator. Added `MacOllamaLifecycleService` in `runner-core/` (plain `net8.0`) with trust-gate, loopback bind, `OLLAMA_MODELS`, and argument-array `serve` launch. Apple Silicon (arm64) slice check runs in pure managed code via the new `MachOArchInspector`, so Windows-side PrepApp can refuse non-arm64 payloads without `lipo`. `ArtifactStagingService.StageMacOllamaAsync` now goes through `MacOllamaStagingPipeline` (verify SHA-256 + arm64 + write attestation; scrub partial dir on failure). Swift `mac-runner` re-checks the on-SSD attestation at every launch and refuses on missing / malformed / URL-mismatched / SHA-mismatched records. CI `windows-build` and `mac-runner-build` both passed.

- **PR #176 - macOS runner CI build enabled - merged `7870eb6` (2026-05-05).**
  Codex's small follow-up to MAC3 enabling the macOS runner job in
  `.github/workflows/build.yml`. No runtime changes.

- **PR #175 - MAC3 platform-neutral Runner core - merged `5c7311d` (2026-05-05).** Added `runner-core/FreeAiSsd.RunnerCore.csproj` as a plain `net8.0` home for platform-neutral Runner chat, document operations, model management, local API endpoint logic, and core service contracts. Windows process, voice, HOTAS/PTT, DCS import, and system-resource probes remain in the WPF Runner host behind adapters. `windows-build` passed; mac runner/package jobs were skipped by workflow settings.

- **PR #174 - MAC1 supported Mac baseline - merged `16eb729` (2026-05-05).** Recorded Apple Silicon-only Mac support with macOS 11 Big Sur minimum, arm64-only Free-AI-SSD app artifacts, exFAT as the supported shared Windows + macOS SSD filesystem, NTFS as Windows-only, APFS deferred until Mac-native prep exists, and Swift/SwiftUI as the default thin native Mac UI over shared/core services. `Build and Package` run #433 passed.

- **PR #173 - MAC2 platform guardrails - merged `72139ac` (2026-05-05).** Added the macOS platform dependency audit, guardrail tests for the portable/shared boundary, and the MAC2 decision/backlog updates. `Build and Package` run #431 passed; PR #172 was closed/recreated as #173 because the GitHub connector hit the known draft-ready schema issue.

- **PR #171 - macOS support merge wrap-up - merged `3e5e831` (2026-05-05).** Updated the dashboard after the macOS support backlog merge; no runtime changes.

- **PR #170 - macOS support backlog - merged `a1d63c2` (2026-05-05).** Added the macOS support track, corrected README / QUICKSTART so macOS is described as a limited Swift direct-Ollama beta, added the MAC1 execution prompt, and passed `windows-build`.

- **PR #167 - F4 Stage 1 wrap-up docs - merged `8ada5e4` (2026-05-05).** Resolved conflicts against latest `main` via branch merge commit `34d0a4e`; `windows-build` passed.

## Next up

1. **MAC6** - Mac LAN API host + Windows-Companion-to-Mac connectivity + X4 web chat UI served by the same Mac Kestrel. Unblocks streaming chat on Mac and the cross-platform composition story.
2. **MAC7** - RAG parity on Mac (embeddings, vector search, citations).
3. **MAC8** - Mac document management (library CRUD + ingestion).
4. Cross-platform PrepApp parity (**MAC16/MAC17/MAC18**) sequences after Runner parity (MAC4-MAC8). Decision recorded 2026-05-05; APFS dropped from supported targets, exFAT is universal. MAC17 is now unblocked from the encrypted-config side (MAC5 done).
5. Track **MAC10a** before broad Mac distribution: Windows PrepApp OS compatibility selector preselecting NTFS vs exFAT.
6. For non-Mac work, pick from `H3`, `F4` follow-up, `B2`, `F2`, or `R1 Stage 2`.

**RAG audit backlog:** X17-X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 -> X15 (expanded) -> X19 -> X20 -> X22 -> X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred -- workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

See `project_backlog.md` for full general backlog details. See
`agent_docs/mac_project_backlog.md` for the macOS support track.

## Last session

2026-05-05 (MAC5 macOS encrypted config unlock/save - PR #179, `a6167ad`) - Created, pushed, CI-validated, and merged `mac5-mac-encrypted-config`. Native Swift port of `SsdEncryption` at `mac-runner/Sources/SsdEncryption.swift`: PBKDF2-HMAC-SHA256 (CommonCrypto), AES-256-GCM (CryptoKit), two-file atomic commit (`portable-config.encrypted.json` + `encryption-state.json`) with rollback when state-rename fails, plaintext-migration mirror of `TryMigratePlaintextAsync` (branch A merges newer plaintext, branch B silently removes stale plaintext). `mac-runner/Sources/main.swift` replaces the "mac unlock not supported yet" short-circuit with a SwiftUI unlock sheet, holds an `UnlockMaterial` while unlocked, and zeroes the derived 32-byte key on `willResignActiveNotification`, `willTerminateNotification`, manual Lock button, and `deinit`. `saveConfig` routes through the encrypted save when the drive is encrypted; the in-memory plaintext is held as `[String: Any]` so unknown PortableConfig fields survive a Mac save. Cross-language pin: `tests/Fixtures/MacEncryptedConfig/csharp-encrypted/` is a Swift-produced encrypted blob that the new `MacEncryptedConfigCrossLanguageTests` decrypt via `SsdEncryption.TryUnlockPortableConfig` on Windows CI; both sides also assert the JSON key shape (`enabled`/`scheme`/`iterations`/… on state, `version`/`scheme`/`salt`/`nonce`/… on blob) so a silent `JsonNamingPolicy` change fails the build. CI `windows-build` and `mac-runner-build` both passed (Swift test binary 15/15: PBKDF2 RFC 6070 vector, AES-GCM roundtrip, wrong password, tampered ciphertext, missing metadata, save preserves unknown fields, state file fields stay correct, plaintext migration both branches, key zeroize, cross-language fixture decrypt). Dated decision recorded ("MAC5 native Swift encryption: deliberate format duplication") with rationale, drift-prevention plan, and exit ramp. MAC2 audit doc records a scoped waiver. README + QUICKSTART drop the "no Mac unlock" caveat. Manual real-Mac smoke (Windows-prepped → Mac unlock → Mac save → Windows re-unlock; mid-save crash rollback) called out as deferred gaps.

2026-05-05 (MAC4 macOS Ollama lifecycle + runtime trust gate - PR #177, `648fcd9`) - Created, pushed, CI-validated, and merged `mac4-macos-ollama-lifecycle`. Generalized `OllamaPackageTrustPolicy` so `DefaultMacPackage` (pinned to Ollama v0.5.7, matching `DefaultWindowsPackage`) is a first-class peer; refactored Windows + Mac validators to share a single `ValidateExecutionAttestationCore`. Added pure-managed `MachOArchInspector` so the Apple Silicon (arm64) slice check runs from Windows-side PrepApp without `lipo`, with a new `Arm64SliceMissing` failure reason. Added `MacOllamaLifecycleService` in `runner-core/` (plain `net8.0`) with trust-gate, `127.0.0.1` loopback bind, `OLLAMA_MODELS`, and argument-array `serve` launch. Wired `ArtifactStagingService.StageMacOllamaAsync` through the new `MacOllamaStagingPipeline` (verify SHA-256 + arm64 + write attestation, scrub partial dir on failure). Repinned `tools/FreeAiSsd.PrereqFetch` to the same v0.5.7 release. Swift `mac-runner` re-checks the on-SSD attestation at every launch and refuses on missing / malformed / URL-mismatched / SHA-mismatched records. New tests: `MacOllamaTrustPolicyTests`, `MachOArchInspectorTests`, `MacOllamaLifecycleServiceTests`, `MacOllamaStagingPipelineTests`, plus shared `MachOFixtures`. CI `windows-build` and `mac-runner-build` both passed before merge. Manual real-Mac smoke (tampered/clean attestation, non-arm64 payload) called out as gaps.

2026-05-05 (planning + MAC4 prompt) - Discussion identified a Mac-native PrepApp
gap (Mac-only users couldn't onboard without a Windows machine) and addressed it
by adding MAC16 (extract `prep-core/`, mirrors MAC3 pattern), MAC17 (macOS
PrepApp MVP, exFAT-only), and MAC18 (cross-platform compatibility docs). MAC6
expanded to require Windows Companion -> Mac Runner connectivity plus X4-on-Mac
served by `runner-core/wwwroot/`. X4 affected files updated to
`runner-core/wwwroot/chat/` so Mac Kestrel serves it for free post-MAC3.
Appended "Cross-platform PrepApp parity (amends MAC1)" decision: APFS dropped
from supported targets (NTFS-from-Mac and APFS-from-Windows accepted as OS
limits, exFAT is universal); Companion-on-Mac stays deferred. Drafted
`agent_docs/mac4_execution_prompt.md` pinned to Ollama v0.5.7 to match the
Windows `DefaultWindowsPackage`. No code changes; ready for fresh session to
execute MAC4.

## Open questions

- Before a public signed Mac beta, verify whether the nested `payload/mac/Runner.app.zip` preserves the stapled notarization ticket after users download and extract the cross-platform ZIP. If not, ship a standalone notarized app ZIP or DMG.
