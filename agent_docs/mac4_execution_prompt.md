# MAC4 Execution Prompt

- Item: `MAC4 - macOS Ollama lifecycle + runtime trust gate`
- Status: `approved`
- Saved: `2026-05-05`
- Recommended execution model: `claude-opus-4-7` or `gpt-5.4`

Use the prompt below to resume in a fresh session.

```text
Implement MAC4 only in /Users/stephenelswick/Free-AI-SSD.

Start by reading:
- agent_docs/project_state.md
- agent_docs/project_arch.md (especially "Security invariants")
- agent_docs/project_decisions.md (MAC1 baseline + 2026-05-05 cross-platform PrepApp parity entry)
- agent_docs/mac_project_backlog.md (MAC4 entry plus MAC5/MAC6 for downstream context)
- shared/OllamaPackageTrustPolicy.cs (Windows trust pattern to mirror)
- runner-core/Services/IOllamaLifecycleService.cs (interface to implement)
- runner/Services/OllamaLifecycleService.cs (Windows reference implementation)
- shared/Prereqs/MacToolCatalog.cs (existing Mac tool definition - has URL but no pinned SHA-256)
- shared/SsdLayout.cs (`MacOllama` = "mac/tools/ollama")
- mac-runner/Sources/main.swift (current Swift Ollama launch; must respect the new trust gate)
- prep-app/Services/ArtifactStagingService.cs (Windows attestation write pattern to extend for Mac)

Goal:
Start and stop the macOS Ollama binary through shared/core logic with the same
security posture as Windows: SHA-256 + URL allowlist on the package source, an
on-SSD trust attestation that gates execution, loopback bind, OLLAMA_MODELS
environment, stdout/stderr/exit logging, and Apple Silicon (arm64) validation.

Repo context:
- MAC1, MAC2, MAC3 are merged. RunnerCore exists at runner-core/ as plain
  net8.0; `IOllamaLifecycleService` is already part of it. The Windows
  implementation in runner/Services/OllamaLifecycleService.cs is the reference.
- Today's `OllamaPackageTrustPolicy` only knows about `DefaultWindowsPackage`.
  MAC4 must extend this so a `DefaultMacPackage` (Apple Silicon, pinned SHA-256)
  is a first-class peer.
- `MacToolCatalog.Ollama` exists with the universal darwin URL but no SHA-256.
  MAC4 must pin the digest and register it in `OllamaPackageTrustPolicy`.
- mac-runner today launches `mac/tools/ollama/ollama serve` directly from
  Swift with no trust check. After MAC4, that launch path must consult the
  same on-SSD attestation file before starting the process. The Swift app
  doesn't need to call into C# - re-implementing the JSON trust check in
  Swift is acceptable as long as the attestation file format is shared.
- The full C# Mac runner host wiring is MAC6 territory; do not build it here.
  MAC4 ships the trust gate, the C# `MacOllamaLifecycleService`, the staging
  attestation, and the Swift gate-check.

Security invariants (non-negotiable):
- AES-256-GCM encrypted config - do not touch.
- SHA-256 + URL allowlist for downloaded binaries - extend to Mac, do not
  bypass.
- `PathGuards` for path handling.
- `ProcessRunner.ArgumentList` for any new process launches - never string
  concat arguments. The `ollama serve` invocation in Swift must also use
  argument-array launches (Swift `Process.arguments`), not a shell string.
- No new dependencies without justification. Standard System.Diagnostics
  Process is fine for the C# adapter; standard Foundation Process is fine
  for Swift.
- Loopback only: bind `OLLAMA_HOST` to 127.0.0.1, never 0.0.0.0.

Implement:

1. Generalize `OllamaPackageTrustPolicy` for Mac.
   - Add `DefaultMacPackage` pinned to **v0.5.7** to match the Windows
     `DefaultWindowsPackage` version. Url:
     `https://github.com/ollama/ollama/releases/download/v0.5.7/ollama-darwin.zip`.
     Compute the SHA-256 from a fresh download of that exact pinned version
     and record the digest in the source file. Do not check the archive
     into the repo.
   - Update `shared/Prereqs/MacToolCatalog.cs` so `Ollama.SourceUrl` points
     at the same pinned `v0.5.7` URL instead of the current
     `releases/latest/...` pattern. The catalog and the trust policy must
     agree.
   - Register the Mac URL in `PinnedMetadataByUrl`.
   - Mac trust attestation file lives at `<ssdRoot>/mac/tools/ollama/ollama-package-trust.json`
     (mirror Windows shape, but under MacOllama). Reuse the existing
     attestation JSON schema (Version/Url/Sha256/VerifiedAtUtc) - same record,
     different on-disk location.
   - Add `GetMacTrustAttestationPath(string ssdRoot)` and a Mac-flavored
     `ValidateMacExecutionAttestation(string ssdRoot)` that calls the same
     core logic as the Windows execution validator, parameterized by package
     metadata + attestation path. Refactor the Windows path to share the
     same core helper rather than duplicating it.
   - Update `WriteTrustAttestation` (or add a Mac sibling) so prep-app can
     write the Mac attestation when staging the Mac Ollama.

2. Apple Silicon (arm64) validation.
   - After SHA-256 verifies, before writing the trust attestation, validate
     that the extracted `ollama` binary contains an arm64 slice. Use one of:
     - Read Mach-O header magic bytes directly (cross-platform - works from
       Windows-side prep too).
     - Or shell out to `lipo -archs` on Mac when available.
   - If only x86_64 is present, fail closed with a clear message
     ("Mac Ollama payload missing arm64 slice"). Universal payloads pass as
     long as arm64 is one of the slices. Pure-arm64 payloads also pass.
   - Surface this as a new `OllamaPackageTrustFailureReason` value
     (e.g., `Arm64SliceMissing`).

3. New `MacOllamaLifecycleService` in `runner-core/Services/`.
   - Implements `IOllamaLifecycleService`.
   - Resolves the binary at `<ssdRoot>/mac/tools/ollama/ollama` (no `.exe`).
   - Calls the new Mac trust validator before launching.
   - Sets `OLLAMA_MODELS=<ssdRoot>/models`.
   - Sets `OLLAMA_HOST=127.0.0.1:<port>` using the same `ResolvePort` pattern
     as Windows.
   - Sets `OLLAMA_ORIGINS=http://127.0.0.1,http://localhost`.
   - Launches with `Process.Start`, redirects stdout/stderr to `LogMessage`,
     fires `ProcessExited` on exit.
   - `Stop()` kills the process tree like Windows.
   - Cross-platform .NET 8 `Process` is fine - this class lives in
     `runner-core/` (plain net8.0) and must not pull in any Windows-only
     packages. `MacPlatformBoundaryTests` should still pass after this.

4. PrepApp staging writes the Mac trust attestation.
   - In `prep-app/Services/ArtifactStagingService.cs` (or the Mac path
     within it), when `mac/tools/ollama/ollama` is staged from the
     downloaded archive: verify SHA-256, validate the arm64 slice, then
     write the Mac trust attestation JSON to
     `<ssdRoot>/mac/tools/ollama/ollama-package-trust.json`.
   - If verification fails, refuse to stage and surface the failure to the
     PrepApp UI like the Windows path does today.
   - Update `MacToolCatalog` if the manifest path / filename needs to align
     with `OllamaPackageTrustPolicy.GetMacTrustAttestationPath`.

5. Swift mac-runner respects the trust gate.
   - In `mac-runner/Sources/main.swift`, before launching `ollama serve`,
     read `<ssdRoot>/mac/tools/ollama/ollama-package-trust.json`, parse the
     JSON, and refuse to launch if:
     - File missing.
     - URL doesn't match the expected pinned Mac URL.
     - SHA-256 doesn't match the expected pinned digest (re-hashing the
       binary at runtime is preferred; if too costly on launch, at least
       cross-check the attestation against an embedded constant).
   - Use Foundation `Process` with `arguments: ["serve"]` (array form, not
     a shell string).
   - Bind to 127.0.0.1 via `OLLAMA_HOST` env var.
   - Append failures and successes to `logs/macos-runner.log` with the
     same severity model as today's Swift app.
   - Keep existing Swift behavior intact for the unencrypted-config beta;
     this PR is the trust gate, not encrypted-config unlock (that's MAC5).

Likely files to touch:
- shared/OllamaPackageTrustPolicy.cs
- shared/Prereqs/MacToolCatalog.cs
- runner-core/Services/MacOllamaLifecycleService.cs (new)
- prep-app/Services/ArtifactStagingService.cs
- mac-runner/Sources/main.swift
- tests/OllamaPackageTrustPolicyTests.cs (or new
  tests/MacOllamaTrustPolicyTests.cs)
- tests/MacOllamaLifecycleServiceTests.cs (new)
- tests/MacPlatformBoundaryTests.cs (verify no regressions on RunnerCore
  package boundary)

Likely tests to add or update:
- Mac trust validation: missing attestation, wrong URL, wrong digest,
  malformed JSON, arm64-slice-missing, happy path.
- `MacOllamaLifecycleService`: path resolution given a fake SSD root,
  refusal when binary missing, refusal when trust fails, environment
  variables set correctly (OLLAMA_MODELS / OLLAMA_HOST / OLLAMA_ORIGINS),
  port resolution behavior.
- ArtifactStagingService: Mac path writes attestation on success, refuses
  to stage on hash/arm64 failure.
- Manual smoke (call out as gap if the agent can't run Mac locally):
  on a real Mac, `mac-runner` refuses to start when the attestation is
  deleted or tampered, and starts cleanly when staged from an unmodified
  archive.

Constraints:
- Do not start MAC5 (encrypted config unlock on Mac) or MAC6 (Mac LAN API
  host) here.
- Do not modify the Windows Ollama lifecycle behavior.
- Do not change the encrypted-config format.
- Do not bypass `OllamaPackageTrustPolicy` for any path - extend it.
- Do not check the downloaded `ollama-darwin.zip` into the repo. Only the
  pinned SHA-256 and URL belong in source.
- Keep `runner-core/` plain net8.0; no Windows-only packages, no WPF.
- Keep existing Swift Ollama beta features working; the gate is additive.

Acceptance criteria:
- `OllamaPackageTrustPolicy` exposes a Mac package + attestation path peer
  to the Windows one, sharing the validator core.
- `MacOllamaLifecycleService` implements `IOllamaLifecycleService` from
  `runner-core/`, builds in plain net8.0, sets OLLAMA_MODELS/HOST/ORIGINS
  correctly, refuses missing binaries, refuses missing or bad attestation,
  refuses payloads without an arm64 slice.
- PrepApp staging writes the Mac trust attestation on a good payload and
  refuses to stage a bad one.
- mac-runner Swift app refuses to launch `ollama serve` when the
  attestation is missing or tampered, and launches cleanly when staged.
- All existing tests still pass.
- New tests cover the failure modes and the happy path.
- `MacPlatformBoundaryTests` still asserts RunnerCore is non-WPF and
  non-Windows-targeted.

Validation:
- `dotnet build FreeAiSsd.sln -c Release`
- `dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal` (full suite)
- For mac-runner Swift changes: `swift build` from `mac-runner/` if
  practical; otherwise call out as a manual-smoke gap.
- Call out manual-smoke gaps explicitly: real-Mac launch with a tampered
  attestation, real-Mac launch with a clean attestation, behavior when the
  Mac Ollama binary is not arm64.

GitHub workflow:
- Never push directly to main.
- Branch: `mac4-macos-ollama-lifecycle`.
- Open a PR titled "[codex] MAC4 macOS Ollama lifecycle + runtime trust gate".
- Watch CI; on failure, push fixes to the same branch.
- Wait for explicit confirmation before merging.

After merge:
- Update agent_docs/mac_project_backlog.md MAC4 status to "done <date>"
  with a brief outcome paragraph mirroring MAC1/MAC2/MAC3 entries.
- Append a dated decision to agent_docs/project_decisions.md if any
  trust-policy shape changes deserve a record (e.g., the validator
  refactor or the arm64-slice rule).
- Update agent_docs/project_state.md "In flight" / "Recently shipped" /
  "Last session" sections.
```
