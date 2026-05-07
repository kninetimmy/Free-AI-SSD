# MAC17a Execution Prompt

- Item: `MAC17a - PrepApp follow-ups from PR #193 review`
- Status: `executed 2026-05-07 (branch kninetimmy/mac17a-prep-followups)`
- Saved: `2026-05-07`
- Recommended execution model: `claude-opus-4-7` or `claude-sonnet-4-6`

Architectural decisions (locked in this prompt):
- Bundle Gemini-review issues #1, #2, #3, #4, #6, #7 in this PR.
  Defer issue #5 (hardcoded SSD layout duplicating `shared/SsdLayout.cs`)
  to MAC17b — it is a structural refactor that benefits from landing
  after the threading cluster settles.
- Stay inside `mac-prep-app/` Swift code. Do not modify
  `mac-prep-host/`, `prep-core/`, or `shared/SsdLayout.cs` for this
  PR. No protocol additions to the sidecar; Issue #5 is what would
  introduce `ensure-structure`.
- Do not weaken any MAC17 invariant: encrypted-config format,
  destructive NSAlert with `.critical`, MAC5 plaintext-config
  invariant, cross-language fixture roundtrip with Windows Runner.

Use the prompt below to resume in a fresh session after approval.

```text
Implement MAC17a only in /Users/stephenelswick/Free-AI-SSD.

Start by reading:
- agent_docs/project_state.md
- agent_docs/project_arch.md (Security invariants, Mac PrepApp section)
- agent_docs/project_decisions.md (2026-05-06 MAC17 entries: brand-tinted
  native UI; canonical SsdEncryption scheme name pin)
- agent_docs/mac_project_backlog.md (MAC17a entry, MAC17b boundary)
- agent_docs/mac17_followup_notes.md (per-issue file:line + proposed fixes
  — this is the source of truth for what to change)
- mac-prep-app/Sources/PrepHostController.swift
- mac-prep-app/Sources/PrepViewModel.swift
- mac-prep-app/Sources/main.swift
- mac-prep-app/Sources/EncryptedConfigWriter.swift
- mac-prep-app/Sources/DiskutilDriveService.swift
- mac-prep-app/Tests/PrepAppTests.swift
- .github/workflows/build.yml (mac-prep-build job)

Goal:
Land the high-signal Gemini-review follow-ups from PR #193: fix the one
real correctness bug (continuation leak in PrepHostController.send) and
move the long-running diskutil format / PBKDF2 encryption / sidecar
shutdown / drive scan calls off `@MainActor` so the SwiftUI ProgressView
ticks during prep. Also remove the misleading encryption-toggle UI.

Scope boundary:
- In scope: PrepHostController.send cancellation correctness; threading
  fixes for formatSelected, writeEncryptionAndProceed, shutdown, and
  refreshCandidates; encryption-toggle UX cleanup.
- Out of scope (deferred to MAC17b): replacing the hardcoded
  `macSubdirs` list in PrepViewModel.runStaging with a sidecar
  `ensure-structure` command that delegates to
  `shared/SsdLayout.cs` EnsureStructure. Do not touch SsdLayout.cs
  or add new sidecar commands in this PR.
- Out of scope: any Windows PrepApp / Runner changes; signing /
  notarization (MAC11); cross-platform compatibility docs (MAC18);
  RAG / model / SPA work.
- Do not weaken security invariants: AES-256-GCM config encryption,
  MAC5 plaintext-config invariant (encrypted-config IO stays
  Swift-authoritative; the sidecar still receives PortableConfig over
  stdin only), `PathGuards` and explicit-argv `ProcessRunner` /
  `Process` patterns where they apply.

Repo context:
- `PrepHostController.send` (mac-prep-app/Sources/PrepHostController.swift:154)
  uses `withThrowingTaskGroup` to race a continuation registration
  against a `Task.sleep` timeout. On timeout the continuation in
  `pendingResults[commandName]` is never resumed and never removed —
  memory leak today, double-resume crash if the same command name is
  retried.
- `PrepViewModel` is `@MainActor`. Its `formatSelected`
  (PrepViewModel.swift:151), `writeEncryptionAndProceed`
  (PrepViewModel.swift:231), and `refreshCandidates`
  (PrepViewModel.swift:87) call into synchronous services that block
  for hundreds of ms to tens of seconds — diskutil eraseDisk,
  PBKDF2 + AES-GCM seal, diskutil list. The ProgressView and log
  scroll freeze for the duration.
- `PrepHostController.shutdown` (PrepHostController.swift:180) busy-waits
  with `Thread.sleep(forTimeInterval:)` up to 2.5s and is called from
  `@MainActor` finalize/restart paths — the Finish/Restart button
  freezes the UI.
- `EncryptionSetupStepView` in main.swift:222 exposes a `Toggle` for
  `enableEncryption` that the user can flip off, but
  `writeEncryptionAndProceed` hard-fails the flow with
  `.failed(message: "Plaintext-mode prep is out of scope for MAC17 MVP …")`
  if it is off. Encryption is mandatory for MAC17 MVP per existing
  logic — the toggle is wishful UI for a future plaintext mode that
  doesn't exist.

Implement (issues from agent_docs/mac17_followup_notes.md):

1. Issue #1 (HIGH) — Fix continuation leak in `PrepHostController.send`.
   - Wrap the `withCheckedThrowingContinuation` in
     `withTaskCancellationHandler` so cancellation removes the slot
     from `pendingResults` and resumes the continuation with
     `CancellationError()` (or `PrepHostError.timedOut(commandName)`
     if that fits the existing throw shape better).
   - Ensure the `cancelAll()` path in the existing
     `withThrowingTaskGroup` triggers the cancellation handler — i.e.
     do not catch and swallow the cancellation before the handler
     runs. The slot must be empty after a timeout regardless of which
     racer wins.
   - Add a `PrepAppTests.swift` test that exercises the cancel path:
     enqueue a command against a stub host (or directly drive
     `pendingResults` via a test seam if simpler), cancel the
     surrounding task before any `result:` line arrives, assert
     `pendingResults` is empty, then re-send the same command name and
     assert it succeeds (does not double-resume / crash). If a small
     `internal` test seam on `PrepHostController` is required, add it
     — keep the production behavior unchanged.

2. Issue #2 (HIGH) — `PrepViewModel.formatSelected` off `@MainActor`.
   - Hop the `driveService.format(...)` call into
     `Task.detached(priority: .userInitiated)` and `await .value`.
     `onOutput` already hops back to `@MainActor` via
     `Task { @MainActor in self?.appendLog(...) }`, so the boundary
     stays clean.
   - Capture `driveService`, identifier, and label by value into the
     detached task. Do not capture `self` strongly across the
     boundary; route UI updates back through the existing
     `@MainActor` log/state methods.

3. Issue #3 (HIGH) — `PrepViewModel.writeEncryptionAndProceed` off `@MainActor`.
   - Same `Task.detached` shape for the
     `encryptedConfigWriter.writeInitialEncryptedConfig(...)` call.
   - Pass `passphrase` and `payload` by value into the detached task.
     After the detached task completes, zeroize the passphrase string
     state on the `@MainActor` side as the existing flow does. Do not
     leave plaintext passphrase material reachable after the call
     returns — match the current zeroization pattern, just on the
     other side of the hop.

4. Issue #4 (MED) — `PrepHostController.shutdown` no longer busy-waits.
   - Make `shutdown(timeout:)` async; replace
     `Thread.sleep(forTimeInterval: 0.05)` with `try? await
     Task.sleep(nanoseconds:)` in both poll loops.
   - Update the two call sites in `PrepViewModel` (finalize/restart)
     to `await hostController.shutdown()`.
   - Keep a synchronous fallback for the `deinit` path — `deinit`
     cannot be `async`, and deinit isn't on the active UI path so
     short busy-wait there is acceptable. Either keep the existing
     sync method renamed (`shutdownSync` for deinit only) or use a
     detached non-blocking terminate from deinit. Choose whichever is
     less code.
   - Test: optional. If easy, add a tiny shell-script fake host that
     sleeps 5s and assert `await shutdown()` returns within ~2.5s.
     Otherwise smoke-only.

5. Issue #6 (MED) — Remove the encryption toggle.
   - Recommended path is (b) from the followup notes: remove the
     `Toggle("Encrypt the drive's configuration store", …)` from
     `EncryptionSetupStepView` (main.swift:222) entirely. Encryption
     is mandatory.
   - Delete `enableEncryption` from `PrepViewModel` and the
     `if !enableEncryption { … failed … return }` branch in
     `writeEncryptionAndProceed`. Audit other call sites — e.g.
     `.disabled(vm.enableEncryption && (vm.passphrase.isEmpty || …))`
     at main.swift:262 should drop the `vm.enableEncryption &&` guard
     since encryption is always on.
   - Keep the explanatory caption text in the step view if there is
     one; just remove the interactive toggle.

6. Issue #7 (MED) — `PrepViewModel.refreshCandidates` off `@MainActor`.
   - Same `Task.detached` shape as #2/#3 around
     `driveService.listExternalCandidates()`. The `await` on
     `.value` then assigns `candidates = list` back on
     `@MainActor`.

Things explicitly NOT to do in this PR:
- Do not replace the hardcoded `macSubdirs` list in
  `PrepViewModel.runStaging` with a sidecar `ensure-structure`
  command. That is MAC17b. Leave the list in place; do not even
  touch the file region around it unless required by an unrelated
  fix.
- Do not modify `mac-prep-host/` C# code. The threading fixes are
  Swift-only.
- Do not change the cross-language fixture
  (`tests/Fixtures/MacEncryptedConfig/swift-prep-encrypted/`) or
  the canonical scheme name (`aes-256-gcm+pbkdf2-sha256-v1`).
- Do not introduce new SwiftUI screens, controls, or theming. Stay
  brand-tinted-native per the 2026-05-06 decision.

Documentation updates:
- Update `agent_docs/project_state.md`: move MAC17a into
  `Recently shipped` after merge; bump `Last updated`; reorder
  `Next up` so MAC17b/MAC18/MAC11 sequence is clear; record the
  branch + PR + CI status.
- Update `agent_docs/mac_project_backlog.md`: mark MAC17a items
  shipped, leave Issue #5 as MAC17b open with file:line refs.
- Append to `agent_docs/project_decisions.md` only if a durable
  architectural choice was made (e.g. the `shutdown()` sync-deinit
  fallback shape). Otherwise leave decisions alone — these are
  bug fixes, not new architecture.
- `agent_docs/mac17_followup_notes.md` can stay as-is for MAC17b
  context; or trim landed sections and leave Issue #5. Author's
  call.

Acceptance criteria:
- `PrepHostController.send` no longer leaks continuations on
  timeout/cancel; new test in `PrepAppTests.swift` proves the slot
  is freed and the command name is re-usable.
- `formatSelected`, `writeEncryptionAndProceed`, `refreshCandidates`,
  and `shutdown` no longer block `@MainActor` for their long phase.
  Spot-check by reading the diff: each long call sits inside a
  `Task.detached { … }.value` (or equivalent off-main hop).
- The encryption toggle is gone from the UI; `enableEncryption` is
  removed from the view model; the `!enableEncryption` failure
  branch is removed from `writeEncryptionAndProceed`.
- `mac-prep-build` CI job stays green: swift unit tests pass,
  mac-prep-host publish + smoke pass, PrepApp.app bundle still
  assembles with prep-host/ in Resources.
- `windows-build` stays green: cross-language encrypted-config
  fixture roundtrip still passes; no C# changes required.
- MAC17 plaintext invariant intact: encrypted-config IO is still
  Swift-authoritative; sidecar still receives PortableConfig over
  stdin only.

Suggested verification:
- Local Swift test (Mac):
  - `cd mac-prep-app && swift test` (or run the standalone
    `Tests/PrepAppTests.swift` runner the existing CI uses — match
    the build.yml mac-prep-build invocation)
- CI (push branch, observe):
  - `windows-build` (full `dotnet test` incl.
    MacEncryptedConfigCrossLanguageTests + DiskutilFormatCommandTests)
  - `mac-runner-build`
  - `mac-prep-build` (swift tests + mac-prep-host publish + stdin
    smoke + PrepApp.app bundle assembly + plutil/exec-bit verify)
  - `package-release` should remain skipped (no release tag).
- Manual smoke (deferred to user on a real Mac + external SSD):
  - Verify the ProgressView animates during format and during
    encryption setup (#2 and #3 fixes).
  - Verify the Refresh button doesn't hitch with multiple USB
    devices plugged (#7 fix).
  - Verify Finish/Restart does not freeze the UI for 2+ seconds
    (#4 fix).
  - Verify the encryption toggle is gone (#6 fix).
  - Cross-platform roundtrip: Mac-prepped SSD unlocks on Windows
    Runner with the Mac-set passphrase.

Branch / PR:
- Branch: `kninetimmy/mac17a-prep-followups`
- PR title: `MAC17a: PrepApp follow-ups from PR #193 review`
- Open the PR, watch CI, report status. Do not merge without
  explicit user confirmation.
- After merge, follow the standard wrap-up shape: docs-only
  follow-up PR (`MAC17a merge wrap-up`) that moves the entry
  into `Recently shipped`, bumps `Last updated`, reorders
  `Next up`, and marks the items done in `mac_project_backlog.md`.
  CI on the wrap-up may be skipped per the user's standing
  preference for docs-only follow-ups.
```
