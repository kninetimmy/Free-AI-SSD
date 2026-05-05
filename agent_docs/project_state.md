# Project State

Last updated: 2026-05-05 (MAC4 prompt drafted; ready for fresh session)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**MAC4 prompt staged.** `agent_docs/mac4_execution_prompt.md` is ready to run
in a fresh session. The prompt pins Mac Ollama to v0.5.7 to match the Windows
`DefaultWindowsPackage`, generalizes `OllamaPackageTrustPolicy` for Mac, adds a
new `MacOllamaLifecycleService` in `runner-core/`, gates Swift `mac-runner`
launch on the on-SSD trust attestation, and validates the Apple Silicon (arm64)
slice. After MAC4: MAC5 encrypted config, then MAC6 Mac LAN API + X4 web UI.

## Recently shipped

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

1. Execute **MAC4** in a fresh session via `agent_docs/mac4_execution_prompt.md`.
2. Then **MAC5** (encrypted config on Mac) -> **MAC6** (Mac LAN API + Windows-Companion-to-Mac connectivity + X4 web chat UI surface).
3. Cross-platform PrepApp parity (**MAC16/MAC17/MAC18**) sequences after Runner parity (MAC4-MAC8). Decision recorded 2026-05-05; APFS dropped from supported targets, exFAT is universal.
4. Track **MAC10a** before broad Mac distribution: Windows PrepApp OS compatibility selector preselecting NTFS vs exFAT.
5. For non-Mac work, pick from `H3`, `F4` follow-up, `B2`, `F2`, or `R1 Stage 2`.

**RAG audit backlog:** X17-X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 -> X15 (expanded) -> X19 -> X20 -> X22 -> X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred -- workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

See `project_backlog.md` for full general backlog details. See
`agent_docs/mac_project_backlog.md` for the macOS support track.

## Last session

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

2026-05-05 (MAC3 platform-neutral Runner core - PR #175, `5c7311d`) - Created, pushed, CI-validated, and merged `mac3-runner-core`. Added `runner-core/FreeAiSsd.RunnerCore.csproj`, moved platform-neutral Runner chat, document operations, model management, local API endpoint logic, and core contracts into it, and kept Windows Ollama process, voice, HOTAS/PTT, and DCS import implementations in the WPF runner host. Added `WindowsSystemResourceProbe` as the Windows adapter behind the core `ISystemResourceProbe` contract, updated tests to reference RunnerCore directly, and extended `MacPlatformBoundaryTests` so RunnerCore remains plain `net8.0`, non-WPF, non-Windows-targeted, and free of blocked Windows-only package references. Local `dotnet` was unavailable, but PR #175 `windows-build` passed before merge.

## Open questions

- Before a public signed Mac beta, verify whether the nested `payload/mac/Runner.app.zip` preserves the stapled notarization ticket after users download and extract the cross-platform ZIP. If not, ship a standalone notarized app ZIP or DMG.
