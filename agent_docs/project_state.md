# Project State

Last updated: 2026-05-06 (MAC10a in flight; PR pending)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**MAC10a — Windows PrepApp filesystem derives from PrepTargets** (PR
pending). Widens `DriveFormatCommand.NormalizeFileSystem` to accept
`exFAT` (canonical casing emitted regardless of input case), adds a
`PrepViewModel.ResolveFileSystem(PrepTargets)` mapping (Windows-only →
NTFS; anything including Mac → exFAT), and surfaces the chosen
filesystem in `EraseConfirmDialog` before the destructive Format-Volume
call. The "compatibility selector" the backlog asked for already exists
as the Windows / Mac checkboxes — see the 2026-05-06 decision entry for
why MAC10a derives from `PrepTargets` rather than introducing a new
control. APFS still deferred until MAC17.

## Recently shipped

- **PR #186 - MAC9 Mac UI strategy decision + MAC8 wrap-up - merged `408828d` (2026-05-06).** Docs-only. Locks in Swift/SwiftUI thin-UI over the `mac-runner-host` .NET sidecar as the supported long-term Mac UI; rejects Avalonia replacement and CLI-first-longer. Decision rests on MAC4-MAC8 evidence: ~1,730 lines of Swift, zero business logic in Swift, one approved business-logic duplication (`SsdEncryption.swift`, MAC5 waiver), zero UI-architecture-driven parity blockers. Exit-ramp criteria recorded in `project_decisions.md` for re-opening MAC9 if Swift starts duplicating non-trivial business logic, WPF/Swift drift outpaces parity work, Apple lifecycle exceeds Avalonia's, or a non-Apple Runner platform is added. Bundled the unfiled MAC8 wrap-up that had never been pushed (PR #185 entry, four 2026-05-06 decision entries on R1 Stage 2 supersession, NoOpConfigStore, NDJSON sync-queue + drain, camelCase JsonNamingPolicy, ASP.NET catch-all `%2F` decode; MAC8 status; Next up reorder). CI `windows-build` (3m1s) + `mac-runner-build` (1m0s) pass; no runtime changes.

- **PR #185 - MAC8 Mac document management - merged `62d6d1d` (2026-05-06).** Adds 8 `/api/library/*` endpoints to `RunnerLocalApiService` (auth-gated, multipart upload + NDJSON progress) and a Documents UI to the Mac Swift runner driving them through `mac-runner-host`. New `NoOpConfigStore` for the Mac sidecar preserves the MAC5/MAC6 plaintext-config invariant; mutating endpoints return updated `activeLibraryId` so Swift persists via `SsdEncryption.swift`. Supersedes `R1 Stage 2`'s narrower `/api/documents` plan. Two new test classes (`RunnerLocalApiLibraryTests`, `MacRunnerHostLibraryTests`) cover happy paths, file rejection, 404s, traversal, auth, and a full create->upload->chat-with-citations end-to-end. CI required 4 runs: initial fail (4 ingest tests); refactored Channel+Task.Run pump to sync queue+drain (still 4 fails); diagnostic dumps revealed `WriteNdjsonAsync` used default JsonSerializer options so the nested `LibraryDetail` record rendered PascalCase while anonymous fields rendered camelCase, breaking `library.fileCount` (525/526 after fix); restored `Uri.UnescapeDataString` on the DELETE catch-all relPath (ASP.NET preserves `%2F` literally) for the final test. All 526 tests green on `c796ba7`.

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

## Next up

1. **MAC10a** - Windows PrepApp OS compatibility selector (NTFS vs exFAT) before broad Mac distribution.
2. **MAC10b** - Mac app icon + Info.plist polish (Runner.app shows default icon today).
3. **MAC11** - Signing + notarization (Apple Developer setup; user has temporary access).
4. Cross-platform PrepApp parity (**MAC16/MAC17/MAC18**) - Mac-only user can prep + run without Windows.
5. **X4** still unblocked from the host side: only needs SPA assets at `runner-core/wwwroot/chat/`.
6. For non-Mac work, pick from `H3`, `F4` follow-up, `B2`, `F2`. (`R1 Stage 2` server endpoints shipped via MAC8; only the RunnerCli `/docs`+`/reindex` slash-commands remain.)

**RAG audit backlog:** X17-X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 -> X15 (expanded) -> X19 -> X20 -> X22 -> X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred -- workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

See `project_backlog.md` for full general backlog details. See
`agent_docs/mac_project_backlog.md` for the macOS support track.

## Last session

2026-05-06 (PR #186 MAC9 merged, `408828d`) - User asked "what's up next" between tasks, then "was there no MAC9". Walked through the three MAC9 options (keep Swift thin-UI / switch to Avalonia / CLI-first-longer) using MAC4-MAC8 evidence. User picked option 1. Discovered the prior session's MAC8 wrap-up docs (PR #185 entry, four decision entries, MAC8 status, Next up reorder) had never been committed — local `main` had ~314 uncommitted insertions sitting there. Bundled with MAC9 into a single docs-only PR rather than leave the wrap-up orphaned. Drafted MAC9 decision entry in `project_decisions.md` with explicit exit-ramp criteria, marked MAC9 done in `mac_project_backlog.md`, updated `project_state.md` In flight + Last session. Branched, committed, pushed, opened PR #186, CI green on first run (windows-build 3m1s, mac-runner-build 1m0s, package-release skipped — docs-only), user said "merge that sucker", merged `408828d`, fast-forwarded local `main`.

2026-05-06 (PR #185 MAC8 merged, `62d6d1d`) - User merged PR #185. Implementation spanned 6 commits: MAC8 prompt fleshed out in `mac_project_backlog.md` (broad-API design over Mac-tight subset); 8 `/api/library/*` endpoints added to `RunnerLocalApiService`; Mac Swift Documents UI in `main.swift`; `NoOpConfigStore` in `mac-runner-host`; `RunnerLocalApiLibraryTests` + `MacRunnerHostLibraryTests`. CI required 4 runs to green — two genuine bugs surfaced by Windows CI (default `JsonSerializer` policy made nested records PascalCase while anonymous frames stayed camelCase, breaking `library.fileCount`; ASP.NET catch-all routing leaves `%2F` encoded so DELETE relPath needed explicit `Uri.UnescapeDataString`) and one refactor that didn't matter on its own (Channel+Task.Run progress pump → sync queue + drain). Local validation gap: no `dotnet` on this Mac, only Swift compile + CI for C#. R1 Stage 2 server endpoints superseded; backlog entry updated to call out only the runner-cli `/docs`+`/reindex` slash-commands as remaining.

## Open questions

- Before a public signed Mac beta, verify whether the nested `payload/mac/Runner.app.zip` preserves the stapled notarization ticket after users download and extract the cross-platform ZIP. If not, ship a standalone notarized app ZIP or DMG.
- **MAC6 manual smoke (deferred until a Mac on the same LAN as a Windows Companion):** Windows Companion discovers and connects to a Mac-hosted Runner with Bearer auth + /api/health + /api/chat; Network Mode toggle with real Ollama serving a real model end-to-end. Bare-bundle launch (no SSD) was sanity-checked 2026-05-06 against the CI `mac-runner-artifact` from run `25448329363` — the bundle opens cleanly on a Mac without Windows tooling.
- **MAC8 manual smoke (deferred to a real Mac):** Network Mode on → Documents UI populates → Create library → Add Files (TXT) → ingest completes → Send chat → returns sources from the uploaded file. CI covers this via `MacRunnerHostLibraryTests` against the real Mac host DI, but not against a real on-Mac launch with NSOpenPanel + a user-picked SSD.
