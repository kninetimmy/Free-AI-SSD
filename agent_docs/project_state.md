# Project State

Last updated: 2026-05-05 (MAC3 local)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**MAC3 local branch in progress:** `mac3-runner-core` introduces a
platform-neutral `runner-core` project for Runner business logic. Local
verification is blocked because `dotnet` is unavailable in this environment;
CI or a machine with the pinned SDK must run the build/test suite before merge.

## Recently shipped

- **PR #174 - MAC1 supported Mac baseline - merged `16eb729` (2026-05-05).** Recorded Apple Silicon-only Mac support with macOS 11 Big Sur minimum, arm64-only Free-AI-SSD app artifacts, exFAT as the supported shared Windows + macOS SSD filesystem, NTFS as Windows-only, APFS deferred until Mac-native prep exists, and Swift/SwiftUI as the default thin native Mac UI over shared/core services. `Build and Package` run #433 passed.

- **PR #173 - MAC2 platform guardrails - merged `72139ac` (2026-05-05).** Added the macOS platform dependency audit, guardrail tests for the portable/shared boundary, and the MAC2 decision/backlog updates. `Build and Package` run #431 passed; PR #172 was closed/recreated as #173 because the GitHub connector hit the known draft-ready schema issue.

- **PR #171 - macOS support merge wrap-up - merged `3e5e831` (2026-05-05).** Updated the dashboard after the macOS support backlog merge; no runtime changes.

- **PR #170 - macOS support backlog - merged `a1d63c2` (2026-05-05).** Added the macOS support track, corrected README / QUICKSTART so macOS is described as a limited Swift direct-Ollama beta, added the MAC1 execution prompt, and passed `windows-build`.

- **PR #167 - F4 Stage 1 wrap-up docs - merged `8ada5e4` (2026-05-05).** Resolved conflicts against latest `main` via branch merge commit `34d0a4e`; `windows-build` passed.

## Next up

1. Validate/open/merge **MAC3** from `mac3-runner-core`.
2. Start **MAC4** after MAC3 lands: macOS Ollama lifecycle + runtime trust gate.
3. Track **MAC10a** before broad Mac distribution: PrepApp OS compatibility selector should preselect NTFS vs exFAT from the MAC1 baseline.
4. Optional Mac release prep: enable and validate the existing Developer ID signing/notarization path before distributing the Mac beta broadly.
5. For non-Mac work, pick from `H3`, `F4` follow-up, `B2`, `F2`, or `R1 Stage 2`.

**RAG audit backlog:** X17-X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 -> X15 (expanded) -> X19 -> X20 -> X22 -> X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred -- workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

See `project_backlog.md` for full general backlog details. See
`agent_docs/mac_project_backlog.md` for the macOS support track.

## Last session

2026-05-05 (MAC3 platform-neutral Runner core - local branch) - Created
`mac3-runner-core`. Added `runner-core/FreeAiSsd.RunnerCore.csproj`, moved
platform-neutral Runner chat, document operations, model management, local API
endpoint logic, and core contracts into it, and kept Windows Ollama process,
voice, HOTAS/PTT, and DCS import implementations in the WPF runner host. Added
`WindowsSystemResourceProbe` as the Windows adapter behind the core
`ISystemResourceProbe` contract. Updated tests to reference RunnerCore directly
and extended `MacPlatformBoundaryTests` so RunnerCore remains plain `net8.0`,
non-WPF, non-Windows-targeted, and free of blocked Windows-only package
references. Local `dotnet` was unavailable, so build/test verification must run
in CI or another SDK-equipped environment.

2026-05-05 (MAC1 supported Mac baseline - PR #174, `16eb729`) - Created, pushed, CI-validated, and merged `mac1-supported-mac-baseline`. Recorded Apple Silicon-only macOS support with macOS 11 Big Sur as the minimum OS, arm64-only Free-AI-SSD app artifacts, exFAT as the supported shared Windows + macOS SSD format, NTFS as Windows-only, APFS deferred until Mac-native prep exists, first-supported-Mac requirements, deferred parity features, and Swift/SwiftUI as the default thin native UI over shared/core services. Added MAC10a for the PrepApp OS compatibility filesystem selector so Windows-only defaults to NTFS and Windows + macOS defaults to exFAT. `Build and Package` run #433 passed restore, build, tests, WPF guard rail, PrepApp publish, Companion publish, and artifact upload.

2026-05-05 (MAC2 platform guardrails - PR #173, `72139ac`) - Created and merged `mac2-platform-guardrails`. Added `agent_docs/mac_platform_dependency_audit.md`, marked MAC2 done in the Mac backlog, appended the MAC2 platform-boundary decision, and added `tests/MacPlatformBoundaryTests.cs`. Local `dotnet` was unavailable, but GitHub Actions `Build and Package` run #431 passed restore, build, `dotnet test`, WPF guard rail, PrepApp publish, Companion publish, and artifact upload. Draft PR #172 was closed and recreated as non-draft PR #173 because the connector's ready-for-review mutation still hits a schema error.

## Open questions

- Before a public signed Mac beta, verify whether the nested `payload/mac/Runner.app.zip` preserves the stapled notarization ticket after users download and extract the cross-platform ZIP. If not, ship a standalone notarized app ZIP or DMG.
