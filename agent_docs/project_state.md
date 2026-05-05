# Project State

Last updated: 2026-05-05 (MAC2 platform audit/guardrails local)

Last released: **v1.2.9** (2026-04-19). Last field-tested: v1.2.5.

> v1.2.7 tag exists on `af77abc` but has no GH release artifact; v1.2.8 supersedes it.

## In flight

**MAC2 local branch in progress:** `mac2-platform-guardrails` records the
macOS platform dependency audit, adds boundary guardrail tests, and updates the
Mac backlog/decision docs. Local test execution is blocked on this machine
because `dotnet` is not installed/on PATH; run the targeted test command before
opening/merging the PR.

## Recently shipped

- **PR #171 - macOS support merge wrap-up - merged `3e5e831` (2026-05-05).** Updated the dashboard after the macOS support backlog merge; no runtime changes.

- **PR #170 - macOS support backlog - merged `a1d63c2` (2026-05-05).** Added the macOS support track, corrected README / QUICKSTART so macOS is described as a limited Swift direct-Ollama beta, added the MAC1 execution prompt, and passed `windows-build`.

- **PR #167 - F4 Stage 1 wrap-up docs - merged `8ada5e4` (2026-05-05).** Resolved conflicts against latest `main` via branch merge commit `34d0a4e`; `windows-build` passed.

## Next up

1. Validate MAC2 guardrails with `dotnet test tests/FreeAiSsd.Tests.csproj --filter MacPlatformBoundaryTests --verbosity normal`, then open the MAC2 PR and watch CI.
2. Complete **MAC1** from `agent_docs/mac_project_backlog.md`: define the supported Mac baseline before MAC3 code extraction.
3. Start **MAC3** only after MAC1 is recorded: introduce the platform-neutral Runner core.
4. Optional Mac release prep: enable and validate the existing Developer ID signing/notarization path before distributing the Mac beta broadly.
5. For non-Mac work, pick from `H3`, `F4` follow-up, `B2`, `F2`, or `R1 Stage 2`.

**RAG audit backlog:** X17-X23 cover audit findings; X10/X13/X15 scope expansions recorded. Plan: `C:\Users\Kninetimmy\.claude\plans\okay-i-want-to-glowing-galaxy.md`. v1.3.x sequence: X18 -> X15 (expanded) -> X19 -> X20 -> X22 -> X23. X17 reduced to Stage 1 textless-page diagnostic (full OCR deferred -- workload is text-layer PDFs).

**Dormant (could not reproduce):** X1-Redux. Diag branch `diag/x1-redux-send-hang` stays on remote, unmerged.

See `project_backlog.md` for full general backlog details. See
`agent_docs/mac_project_backlog.md` for the macOS support track.

## Last session

2026-05-05 (MAC2 platform dependency audit / guardrails - local branch) - Created branch `mac2-platform-guardrails`. Added `agent_docs/mac_platform_dependency_audit.md` with the current Windows-only blockers and split plan for platform-neutral core plus host adapters. Marked MAC2 done in `agent_docs/mac_project_backlog.md`, added a dated MAC2 decision, and added `tests/MacPlatformBoundaryTests.cs` to keep `shared/` plain `net8.0`, bound the current Windows-only shared packages as explicit debt, and keep `runner-cli/` portable. Attempted `dotnet test tests/FreeAiSsd.Tests.csproj --filter MacPlatformBoundaryTests --verbosity normal`, but `dotnet` was not installed/on PATH in this shell.

2026-05-05 (macOS signing guidance - no repo changes) - Reviewed the current Mac track and confirmed MAC1 is next. Explained the Developer ID signing + notarization path and noted the existing CI signing steps are present but disabled by `MAC_SIGNING_ENABLED=false`. Created a granular Markdown guide at `/Users/stephenelswick/Desktop/Free-AI-SSD-macOS-signing-notarization-guide.md`; temporary repo scratch was removed and the working tree remained clean.

2026-05-05 (macOS support merge wrap-up - PR #171, `3e5e831`) - Updated the dashboard after PR #170 so `main` reflects the merged macOS support backlog and MAC1 as the next Mac task. This was documentation-only.

2026-05-05 (macOS support backlog merged - PR #170, `a1d63c2`) - Reconciled `mac-support-backlog` with latest `main`, resolved the dashboard conflict, added `agent_docs/mac1_execution_prompt.md`, pushed the branch, and opened PR #169. The draft PR could not be marked ready because the GitHub connector's ready-for-review mutation returned a schema error, so #169 was closed and recreated as non-draft PR #170. `windows-build` passed on both runs for head `702607e`; PR #170 merged to `main` as `a1d63c2`, and local `main` was fast-forwarded.

## Open questions

- Before a public signed Mac beta, verify whether the nested `payload/mac/Runner.app.zip` preserves the stapled notarization ticket after users download and extract the cross-platform ZIP. If not, ship a standalone notarized app ZIP or DMG.
