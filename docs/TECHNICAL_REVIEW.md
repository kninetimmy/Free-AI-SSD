# Free-AI-SSD Technical Review (Evidence-Based)

## A. Executive Summary
The repository implements a clear two-phase architecture: an **online preparation phase** (PrepApp) and an **offline runtime phase** (Runner), with shared logic in a dedicated shared library. Entry-point behavior aligns with project goals: PrepApp stages models/tools/prerequisites and finalizes SSD state, while Runner consumes only SSD-local artifacts and serves inference against a loopback Ollama endpoint.

Overall, this is a cohesive design for a portable offline media workflow. In particular, trust controls around Windows Ollama package source/digest/attestation and prerequisite installer validation are materially stronger than typical prototype tooling, and the encryption-read-only guard is implemented both in UI enablement and operation-level checks.

**Major strengths**
- Clear separation of concerns across `prep-app`, `runner`, and `shared` projects, with shared state/contracts centralized in `PortableConfig`, `SsdLayout`, and related utilities.
- Strong trust gating for Windows Ollama package lifecycle: source allowlist + pinned metadata + SHA256 verification + execution attestation.
- Defensive prerequisite install path: allow-listed IDs, filename and SHA validation, path containment/reparse-point checks, plus signer verification attempts.
- Encryption workflow has explicit enabled-state metadata, authenticated decryption path, and a centralized write-block helper used across mutating PrepApp actions.
- CI pipeline runs restore/build/test and packages stable/beta artifacts with explicit assembly steps.

**Confirmed risks (code-supported)**
- If `config/encryption-state.json` is corrupt/unreadable, `SsdEncryption.IsEncryptionEnabled` returns `false`, which causes PrepApp write guard checks to treat the drive as writable. This is demonstrable and tested behavior. This creates a fail-open condition for the guard decision path.

No additional critical risks were identified from code inspection.

## B. Architecture & Design Evaluation
**System structure**
- **PrepApp (WPF):** `prep-app/MainWindow.xaml.cs` orchestrates drive selection, model config/pull/verify/remove, prerequisite update, readiness checks, staging, and finalize.
- **Runner (WPF):** `runner/MainWindow.xaml.cs` resolves SSD root, loads/unlocks config, validates trust/dependencies, starts local Ollama, and submits prompts to loopback API.
- **Shared layer:** key abstractions in `shared/` include layout (`SsdLayout`), config (`PortableConfig`), encryption (`SsdEncryption`), trust policy (`OllamaPackageTrustPolicy`), prerequisite catalog/manifest/validation, and path guards.

**Cohesion & separation of concerns**
- Cohesion is good: state formats and policies are in shared code, while UI orchestration remains in app projects.
- Config persistence is centralized via `PortableConfig.SaveAsync` with atomic temp+replace semantics.
- Folder/path conventions are centralized in `SsdLayout`, reducing hard-coded drift.

**Design reasonableness**
- The prepare-once / run-offline split is consistently reflected in implementation and docs.
- Operation guards for encryption are centralized (`PrepDriveWriteGuard`) and then applied at both UI-state and action handlers, which is a robust pattern against accidental bypass.

No material structural refactor is required based on current evidence.

## C. Security Review (Evidence-Based)
**Secrets handling**
- macOS signing/notarization credentials are referenced as CI secrets in docs rather than committed values.
- No obvious hardcoded credentials found in repository code.

**Downloads and package trust**
- Windows Ollama package source validation enforces HTTPS, host allowlist, and pinned URL metadata.
- Downloaded archive digest is checked before extraction.
- Execution requires matching trust attestation metadata.

**Prerequisite installers**
- Runner validates manifest consistency, installer hash, path containment, and (for selected packages) Authenticode signer/status before install planning.
- Validation failures block installation and provide explicit remediation guidance.

**Privilege boundaries and logging**
- Destructive formatting path requires removable drive and administrator role.
- Runner prerequisite install requests elevation only when selected entries require admin.
- Logging is SSD-local (`logs/`) via `SsdLogger`; no sensitive credential logging observed in reviewed paths.

**Concern (confirmed)**
- Encryption guard decision can fail open on corrupted `encryption-state.json` as noted in section A.

## D. Offline & Determinism Review
- Docs and code are aligned that internet is needed during prep (downloads/model pulls), while Runner serves inference locally from staged artifacts.
- Runner inference path uses `http://127.0.0.1:<port>/api/generate` and sets Ollama host/origins to loopback.
- Dependency installation is designed for offline operation from SSD-bundled installers after local validation.

No hidden runtime network dependency was identified in Runner’s normal inference path.

## E. Windows Robustness Review
- Drive handling uses `DriveInfo` filtering and explicit warnings for fixed/internal drives.
- Formatting workflow has clear safety rails: removable-only + admin check + explicit ERASE confirmation.
- Path safety includes root containment and reparse-point checks before using installer paths.
- Runner includes admin relaunch path for prerequisite installs requiring elevation.

No material Windows robustness concerns identified beyond the encryption fail-open point already listed.

## F. Encryption Guard Evaluation
1. **How “encryption enabled” is represented**
   - `SsdEncryption.IsEncryptionEnabled` reads `config/encryption-state.json` and returns `state.Enabled == true`.
   - Finalize encryption writes encrypted config file + state metadata and deletes plaintext config.

2. **Actual config-mutating actions in PrepApp**
   - `AddModel_Click` updates model list and saves config.
   - `AddSelectedStarterModels_Click` updates model list and saves config.
   - `AddOrphanToConfig_Click` writes discovered models into config.
   - `PullInstall_Click`, `PullSelected_Click`, and pull pipeline update model status/hash/verification fields via `UpdateModelStatusAsync`.
   - `Verify_Click` updates status/verification timestamps.
   - `Remove_Click` mutates config state and optionally performs disk deletion.
   - `CheckReadiness_Click` / `RunReadinessChecksAsync` can mark models failed and save config.
   - `CheckPrereqUpdates_Click` writes prereq installers/manifest.
   - `FormatPrepare_Click` formats drive and prepares structure.
   - `Finalize_Click` writes config, stages payloads/tools, and optionally enables encryption.

3. **Centralized guard existence**
   - Yes. `EnsureSelectedDriveWritableForPrep` uses `PrepDriveWriteGuard.IsWriteBlocked` and is called by mutating handlers.
   - UI controls are also disabled when encrypted drive is selected (`UpdateModelActionButtons`).

4. **Guard implementation quality**
   - Largely well implemented due to dual enforcement (UI + handler checks).

5. **Minimal improvements warranted**
   - Adjust encryption-state detection to fail closed when encrypted metadata artifacts are inconsistent/corrupt (e.g., if encrypted config exists but state parse fails, treat as encrypted/blocked).
   - Optionally surface explicit “metadata corrupt; write operations blocked” status to guide remediation.

## G. Testing & CI
- Test suite covers key security/guard behaviors: encryption flows, trust policy behavior, path guards, prerequisite validator logic, and prep write guard.
- CI workflow executes restore/build/test and validates WPF builds on Windows before packaging.

Assessment: coverage is appropriate for current project size and risk profile (core logic and safety-critical helpers are tested). The main justified addition would be a regression test for the encryption fail-open scenario after remediation.

## H. Documentation & Developer Experience
- README + QUICKSTART are clear, task-oriented, and explicit about stable vs beta packaging and offline expectations.
- Build/test/release workflow is documented in both README and CI, including artifact assembly behavior.

No material documentation concerns identified.

## I. Conclusion
Overall maturity: **Beta** (well-structured with meaningful safety controls, but still evolving packaging/platform flows).

Most impactful improvements (justified):
1. Make encryption-enabled detection fail closed on corrupt/inconsistent metadata to harden write guard behavior.
2. Add a targeted test that proves prep writes are blocked when encryption metadata is corrupted but encrypted artifacts are present.
3. Add a small remediation UX path in PrepApp for corrupted encryption metadata (clear operator instructions).

The codebase is otherwise structurally sound and shows disciplined implementation in several security-relevant areas.
