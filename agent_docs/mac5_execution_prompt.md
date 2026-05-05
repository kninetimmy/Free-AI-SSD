# MAC5 Execution Prompt

- Item: `MAC5 - macOS encrypted config unlock/save`
- Status: `approved`
- Saved: `2026-05-05`
- Recommended execution model: `claude-opus-4-7` or `gpt-5.4`

Architectural decision (locked in this prompt, see preamble below for the
rationale): MAC5 ships a **native Swift CryptoKit reimplementation** of the
encrypted-config unlock/save flow. The C# `SsdEncryption` + `ConfigStore`
remain the source of truth for the on-disk format; Swift mirrors them byte-
for-byte and the two implementations are pinned together by cross-language
test vectors. We deliberately accept duplicated security-critical code here
to avoid hosting a .NET runtime on Mac just to read a JSON blob. If MAC6
later replaces the native path with a .NET host, the MAC6 decision entry
will record why.

Use the prompt below to resume in a fresh session.

```text
Implement MAC5 only in /Users/stephenelswick/Free-AI-SSD.

Start by reading:
- agent_docs/project_state.md
- agent_docs/project_arch.md (especially "Security invariants" and the SSD
  runtime layout)
- agent_docs/project_decisions.md (MAC1 baseline + the 2026-05-05
  cross-platform PrepApp parity entry + the MAC4 trust gate entry)
- agent_docs/mac_project_backlog.md (MAC5 entry plus MAC6/MAC7 for
  downstream context)
- agent_docs/mac_platform_dependency_audit.md ("keep the Swift app thin"
  guideline — MAC5 deliberately waives this for the encrypted-config
  format; record the rationale in project_decisions.md)
- shared/SsdEncryption.cs (full file — every constant, JSON shape, and
  two-file commit invariant must be mirrored exactly in Swift)
- shared/Services/ConfigStore.cs and shared/Services/IConfigStore.cs
  (lock/unlock/save/flush contract)
- shared/Services/UnlockMaterial.cs
- shared/PortableConfig.cs (the plaintext shape that gets encrypted; Swift
  must preserve unknown fields on save)
- shared/SsdLayout.cs (Config = "config", EncryptedConfigFileName, etc.)
- runner/MainWindow.xaml.cs around lines 340-390 and ~2172 (the Windows
  unlock dance: TryUnlockPortableConfigWithMaterial → UnlockSession →
  TryMigratePlaintextAsync → LockSession on shutdown)
- mac-runner/Sources/main.swift (current Swift app: refuses encrypted
  drives with "mac unlock not supported yet" — that refusal is what MAC5
  removes)
- tests/SsdEncryptionTests.cs and tests/ConfigStoreTests.cs (existing
  failure-mode coverage to mirror on the Swift side)
- .github/workflows/build.yml (the `mac-runner-build` job: swiftc
  single-file build, arm64-apple-macos11.0, SwiftUI + AppKit; CryptoKit is
  in the macOS SDK and adds no dependencies)

Goal:
The Mac runner can unlock an SSD whose portable-config was encrypted on
Windows, save changes back to the encrypted blob without leaking
plaintext, and roundtrip cross-platform: Windows-prepped drives unlock on
Mac, Mac-saved blobs unlock on Windows. The encrypted-config format
(`aes-256-gcm+pbkdf2-sha256-v1`, 210k iterations, 16-byte salt, 12-byte
nonce, 16-byte tag, base64 fields, two-file atomic commit) is FIXED. Do
not invent a new scheme. Mirror the C# implementation exactly.

Repo context:
- MAC1-MAC4 are merged. MAC4 already mirrors a small JSON-format contract
  (the Ollama trust attestation) in Swift; MAC5 follows the same pattern
  but for a much larger surface (PBKDF2-SHA256 + AES-256-GCM + two-file
  atomic commit + plaintext migration).
- The shared C# SsdEncryption format is locked. Constants:
  - SchemeName = "aes-256-gcm+pbkdf2-sha256-v1"
  - StateFileName = "encryption-state.json"
  - EncryptedConfigFileName = "portable-config.encrypted.json"
  - SaltBytes = 16, KeyBytes = 32, NonceBytes = 12, TagBytes = 16
  - Pbkdf2Iterations = 210_000 (read from the encrypted blob's
    `iterations` field at unlock time, not hardcoded — older blobs may
    use a different count if the constant ever changes)
- C# JSON is serialized with `JsonNamingPolicy.CamelCase` and
  `WriteIndented = true`. Swift output should use lowercase first-letter
  camelCase keys to match (so `salt`, `nonce`, `tag`, `ciphertext`,
  `iterations`, `scheme`, `version`, `createdAtUtc` for the encrypted
  blob; `enabled`, `scheme`, `iterations`, `encryptedConfigFile`,
  `updatedAtUtc` for the state file). Verify the actual on-disk shape by
  having a C# test write a sample blob during this PR and inspecting it,
  rather than guessing — the JSON naming policy applies to the field
  names, not the Pascal/Camel form in source.
- Today the Swift app reads `portable-config.json` directly with
  Foundation `JSONDecoder` into a tiny `PortableConfig` struct (only
  `models[]`). On encrypted drives it short-circuits with
  "Encrypted SSD locked (mac unlock not supported yet)" and never reads
  the encrypted blob. MAC5 removes that short-circuit and replaces the
  config-loading path.
- Two-file atomic commit invariant from `SaveEncryptedConfigAsync`:
  encrypted-blob and state-file must never disagree on scheme/iterations
  or on whether encryption is enabled. The C# implementation writes
  `*.tmp`, replaces with backups, and rolls back the blob if the state
  rename fails. The Swift port must honor the same invariant — a crash
  mid-save must not leave a working blob with a stale or missing state
  file.

Security invariants (non-negotiable):
- AES-256-GCM (authenticated encryption) — do not switch to CBC/CTR or
  drop the tag check.
- PBKDF2-SHA256 with the iteration count read from the encrypted blob.
- Random 12-byte nonce per encrypt call (AES-GCM nonce reuse with the
  same key is catastrophic — never reuse, never derive deterministically
  for "test mode"). Use `SecRandomCopyBytes` or `SystemRandomNumberGenerator`
  in Swift, not `arc4random` or anything seeded.
- Wrong-password / tampered-ciphertext / malformed-base64 paths fail
  closed with descriptive errors that match the Windows error strings
  where possible ("Incorrect password.", "Encrypted drive metadata is
  missing.", "Encrypted drive metadata is unreadable.", etc.).
- Derived-key zeroing on lock: hold the 32-byte key in a `Data` buffer
  that gets `resetBytes(in:)` cleared the moment the session locks (app
  background, app exit, explicit lock action). Do not let the key linger
  in any cached String/Data/URLSession body.
- `PathGuards` discipline applies to Swift too: never join a user-
  supplied path component into the SSD root without validating it stays
  inside the root. For MAC5 the only paths Swift writes to are
  `<ssdRoot>/config/portable-config.encrypted.json`,
  `<ssdRoot>/config/encryption-state.json`, and their `.tmp`/`.bak`
  siblings — keep the path construction obvious and don't let dialog
  state plumb through.
- Plaintext bytes from the decrypted PortableConfig must never touch
  disk on Mac. Swift can hold the parsed `[String: Any]` dictionary in
  memory; it must not write a plaintext `portable-config.json` back to
  the SSD (that's the bug `TryMigratePlaintextAsync` was designed to
  clean up — Mac must not regress it).

Implement:

1. Native Swift encryption module.
   - Add `mac-runner/Sources/SsdEncryption.swift` (or merge into
     main.swift if the swiftc single-file build pattern is too rigid for
     a multi-file layout — confirm by inspecting how the workflow
     currently invokes swiftc, then prefer a multi-file build if it's
     trivially supported; otherwise keep everything in main.swift behind
     `// MARK: -` section markers).
   - Implement using only the macOS SDK: `Foundation`, `CryptoKit`,
     `Security` (for `SecRandomCopyBytes` if needed).
   - Public surface, mirroring C#:
     - `SsdEncryption.tryUnlockPortableConfig(ssdRoot:URL, password:String)
       -> Result<(config: [String: Any], material: UnlockMaterial), UnlockError>`
       — reads `<ssdRoot>/config/encryption-state.json` and
       `<ssdRoot>/config/portable-config.encrypted.json`, derives the
       key via PBKDF2-SHA256 (use `CryptoKit.HKDF`? — no, HKDF is not
       PBKDF2; use `CommonCrypto`'s `CCKeyDerivationPBKDF` via a thin
       bridging header, OR ship a vetted pure-Swift PBKDF2-HMAC-SHA256
       in this PR. Do not add a third-party SwiftPM dependency for
       crypto). Decrypt with `AES.GCM.open` from CryptoKit using the
       extracted nonce/tag/ciphertext. Return the parsed JSON object as
       `[String: Any]` (preserving unknown keys for save) plus the
       `UnlockMaterial` (key + salt + iterations + scheme).
     - `SsdEncryption.saveEncryptedConfig(ssdRoot:URL, config:[String: Any],
       material:UnlockMaterial) throws` — serializes the dictionary back
       to UTF-8 JSON bytes (use `JSONSerialization` with
       `.prettyPrinted, .sortedKeys` so the output is deterministic),
       generates a fresh 12-byte nonce, encrypts with `AES.GCM.seal`,
       writes both files via the two-step staged-tmp + atomic rename
       protocol below.
     - `SsdEncryption.isEncryptionEnabled(ssdRoot:URL) -> Bool` — same
       semantics as the C# helper.
     - `SsdEncryption.isEffectivelyEncryptedForWriteGuard(ssdRoot:URL)
       -> Bool` — same fail-closed semantics as the C# helper.
   - Use a Swift `UnlockError` enum mirroring the C# error strings. The
     localized description must match the Windows messages exactly so
     existing user docs apply on both platforms.

2. PBKDF2-SHA256 implementation choice.
   - Preferred: import `CommonCrypto` via a Swift bridging-header import
     (`import CommonCrypto`) and call `CCKeyDerivationPBKDF` with
     `kCCPRFHmacAlgSHA256`. CommonCrypto is part of the macOS SDK and
     adds no dependencies.
   - If the swiftc single-file build can't easily import CommonCrypto
     because of the lack of a module map, write a small pure-Swift
     PBKDF2-HMAC-SHA256 using `CryptoKit.HMAC<SHA256>`. Add a unit test
     that verifies it against published RFC 7914 / IETF test vectors
     before relying on it.
   - Either way, the iteration count comes from the encrypted blob, not
     a hardcoded constant — older blobs with a different count must
     unlock cleanly.

3. Two-file atomic commit in Swift.
   - Mirror `SsdEncryption.SaveEncryptedConfigAsync`:
     - Write encrypted-blob to `portable-config.encrypted.json.tmp`.
     - Write state to `encryption-state.json.tmp`.
     - Clean stale `.bak` files from prior crashed saves.
     - Rename blob.tmp -> blob (use `FileManager.replaceItemAt` when the
       destination exists so we get the backup-and-replace atomicity;
       fall back to `FileManager.moveItem` for first-time creation).
     - Rename state.tmp -> state, same pattern.
     - On state-rename failure after blob succeeded: restore blob from
       its `.bak`. If first-time save (no prior blob), delete the
       half-written blob so we don't end up with blob without state.
     - On all-success: delete `.bak` files.
   - The blob and state files must agree on `scheme` and `iterations`.
     A successful save must update `state.updatedAtUtc` and bump the
     blob's `createdAtUtc` (the C# field is named `createdAtUtc` even
     though it really records the latest write — keep the same name).

4. Plaintext migration on first unlock.
   - Mirror `TryMigratePlaintextAsync`:
     - After a successful unlock, check whether
       `<ssdRoot>/config/portable-config.json` exists alongside the
       encrypted blob.
     - If plaintext mtime > encrypted mtime: load the plaintext as
       JSON, save it through the encrypted save path with the cached
       UnlockMaterial, then delete the plaintext. Log
       "Plaintext config was newer — merged into encrypted blob,
       plaintext deleted." to `logs/macos-runner.log`.
     - Otherwise: delete the stale plaintext silently and log
       "Stale plaintext removed — encrypted is authoritative."
     - On any save failure during merge: keep the plaintext intact,
       log the warning, and surface a non-fatal error to the UI (the
       drive is still unlocked and usable read-only).

5. Mac runner UI: unlock dialog + lock-on-exit.
   - Replace the current "Encrypted SSD locked (mac unlock not
     supported yet)" path in `RunnerViewModel.loadConfig()` with:
     - Detect encryption-state.json and set `isEncryptedLocked = true`.
     - Show an unlock prompt sheet/dialog: a `SecureField` for password,
       Unlock and Cancel buttons. Match the Windows UnlockDriveDialog UX
       in spirit (single password entry, masked input, descriptive
       failure messages).
     - On Unlock: call `SsdEncryption.tryUnlockPortableConfig`. On
       success, store the `UnlockMaterial` on the view model
       (`private var unlockMaterial: UnlockMaterial?`), populate
       `modelNames`/`selectedModel` from the decrypted config, run the
       plaintext-migration step, set `isEncryptedLocked = false`,
       update status. On failure show the error and keep the dialog
       open.
     - On Cancel: stay locked; the existing
       "Encrypted SSD locked" text is fine for the locked state, but
       drop the "(mac unlock not supported yet)" suffix.
   - Add a `LockSession` action: zeroes `unlockMaterial.derivedKey`,
     drops `modelNames`, sets `isEncryptedLocked = true` again.
   - Wire `LockSession` to:
     - App backgrounding (NSApplication will-resign-active or
       scenePhase change to `.inactive`/`.background`).
     - App termination (NSApplication will-terminate /
       `applicationWillTerminate`).
     - An explicit "Lock" button next to "Select SSD" in the main UI.
   - Saving: when the Swift UI changes anything that lives in
     PortableConfig (currently only the selected model isn't a
     PortableConfig field, so this PR may not have a user-visible
     trigger — that's fine; cover save via the integration test
     instead). Expose
     `RunnerViewModel.saveConfig(mutate: ([String: Any]) ->
     [String: Any])` that takes a mutation closure, applies it to the
     in-memory config dictionary, and calls
     `SsdEncryption.saveEncryptedConfig` under the cached
     `UnlockMaterial`. Refuse to save when no material is held (the
     Windows ConfigStore throws InvalidOperationException in the same
     situation).

6. Cross-language format pin (test vectors).
   - Add `tests/MacEncryptedConfigCrossLanguageTests.cs`:
     - C# encrypts a known PortableConfig with a known password, writes
       the resulting `portable-config.encrypted.json` +
       `encryption-state.json` to a temp dir, and asserts the JSON
       structure (field names, base64 lengths, scheme name).
     - Then re-deserializes its own output to prove roundtrip parity.
     - Optionally writes a fixture blob to
       `tests/Fixtures/MacEncryptedConfig/` for the Swift test to read.
       Keep the password short and the config minimal — this is a
       format pin, not a real workload.
   - Add a Swift test entry point. The mac-runner-build job currently
     compiles a single `main.swift` into `Runner.app`. Either:
     - Add a second swiftc invocation in CI that builds
       `mac-runner/Tests/SsdEncryptionTests.swift` as a command-line
       binary linking the same encryption sources, runs it, and fails
       the job on non-zero exit. This is the lowest-friction path
       since SwiftPM isn't currently in the repo.
     - Or introduce a minimal `Package.swift` and switch the runner
       build to SwiftPM. Only do this if it doesn't disrupt the
       existing `Runner.app` packaging — the trust-gate-bearing
       single-binary layout from MAC4 must keep working.
   - The Swift test should at minimum:
     - Decrypt the C# fixture and assert plaintext matches a known
       canonical JSON.
     - Encrypt the same canonical JSON with a fresh nonce and a fixed
       password, then call back into the Swift unlock path and assert
       roundtrip.
     - Refuse a wrong password with the correct error.
     - Refuse a tampered ciphertext byte (flip one bit in the base64
       payload) with the auth-tag-failure error.

7. Documentation + audit waiver.
   - Update README and docs/QUICKSTART.txt to drop the "Mac cannot
     unlock encrypted drives" caveat (replace with: "encrypted drives
     prepped on Windows now unlock on Mac; ditto for Mac-prepped
     drives once MAC17 ships").
   - Update `agent_docs/mac_platform_dependency_audit.md` to record the
     deliberate exception: encryption format is reimplemented in
     Swift/CryptoKit because the alternative (.NET-on-Mac for config
     IO) is heavier than the duplication and Apple Silicon prefers
     native paths. Reference the dated decision.

Likely files to touch:
- mac-runner/Sources/main.swift (unlock UI, lock-on-exit, save action)
- mac-runner/Sources/SsdEncryption.swift (new — or section in main.swift
  if multi-file builds are a hassle)
- mac-runner/Tests/SsdEncryptionTests.swift (new — Swift cross-format
  test)
- tests/MacEncryptedConfigCrossLanguageTests.cs (new)
- tests/Fixtures/MacEncryptedConfig/ (new fixtures)
- .github/workflows/build.yml (extend mac-runner-build to compile and
  run the Swift test binary)
- README.md, docs/QUICKSTART.txt (drop the "no Mac unlock" caveat)
- agent_docs/mac_platform_dependency_audit.md (record the waiver)
- agent_docs/project_decisions.md (new dated entry: see "After merge"
  below)
- agent_docs/mac_project_backlog.md (mark MAC5 done)
- agent_docs/project_state.md (Recently shipped / In flight / Last
  session)

Likely tests to add or update:
- Swift: unlock happy path with a C#-produced fixture; wrong password;
  malformed JSON; tampered ciphertext; missing state file; missing
  encrypted blob; PBKDF2 RFC test vectors (if shipping pure-Swift PBKDF2);
  save roundtrip; two-file atomic commit (mid-save crash simulation by
  short-circuiting between blob and state writes); plaintext migration
  branch A (plaintext newer) and branch B (plaintext stale).
- C#: format pin asserts (JSON field shape, base64 lengths, scheme
  string); existing SsdEncryption + ConfigStore tests must continue to
  pass; MacPlatformBoundaryTests must continue to pass (no new
  Windows-only packages introduced anywhere).
- Manual smoke (call out as gap if the agent can't run Mac locally):
  encrypt a drive on Windows PrepApp, mount it on a real Mac, unlock
  via the Swift UI, change the selected model, lock, re-unlock, verify
  the change persisted; mount the same drive back on Windows and
  verify the Windows runner still unlocks it cleanly.

Constraints:
- Do not start MAC6 (Mac LAN API host + Companion compatibility + X4
  web UI). Save endpoints, streaming chat, RAG over HTTP, and Companion
  handshake are explicitly out of scope.
- Do not modify the C# SsdEncryption format. New fields, new schemes,
  new iteration defaults all require their own dated decision.
- Do not touch the Windows Runner unlock flow — the C# code path stays
  identical.
- Do not pull in a third-party Swift crypto package. Use only macOS SDK
  frameworks (`CryptoKit`, `CommonCrypto`, `Foundation`, `Security`).
- Do not write plaintext `portable-config.json` to disk from the Mac
  runner under any condition. The plaintext-migration branch A reads
  plaintext that was already there; it never creates one.
- Do not weaken the loopback-only Ollama bind from MAC4. The Mac trust
  gate keeps working unchanged.
- Keep `runner-core/` / `shared/` C# unchanged unless a small
  read-only test helper is genuinely needed (e.g., to expose a
  deterministic encrypt overload for fixtures). If a helper is added,
  scope it to `internal` + `[InternalsVisibleTo]` for the test project.

Acceptance criteria:
- Swift `SsdEncryption` module unlocks a Windows-prepped encrypted SSD
  with the correct password and refuses with descriptive errors on
  wrong-password / missing-files / malformed-JSON / tampered-ciphertext.
- Swift saves through the same two-file atomic commit; a Windows
  runner can unlock the resulting blob without changes.
- Plaintext migration mirrors the Windows behavior: newer plaintext is
  merged into the encrypted blob then deleted; stale plaintext is
  silently removed.
- The derived 32-byte key is zeroed on lock (app background, app exit,
  explicit lock action).
- Cross-language test pins the format: a C#-produced fixture decrypts
  in Swift, a Swift-produced blob decrypts in C#, both use the same
  scheme name and base64 field layout.
- mac-runner UI presents an unlock dialog instead of refusing
  encrypted drives, and lets the user lock the session manually.
- All existing tests pass. New tests cover the Swift failure modes and
  the cross-format roundtrip.
- `MacPlatformBoundaryTests` still asserts shared/runner-core remain
  inside their dependency budgets.

Validation:
- `dotnet build FreeAiSsd.sln -c Release`
- `dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal`
- For the Swift side: build `Runner.app` and the new test binary via
  the extended `mac-runner-build` CI job; run the test binary as part
  of the job. If the agent cannot run macOS locally, lean on CI for
  validation and explicitly call out manual-Mac smoke as a gap.
- Manual-smoke gaps to call out in the PR description: real-Mac unlock
  of a Windows-prepped drive; Mac-saved blob re-unlocked on Windows;
  forced mid-save crash leaves the drive unlockable on next launch.

GitHub workflow:
- Never push directly to main.
- Branch: `mac5-mac-encrypted-config`.
- Open a PR titled "[codex] MAC5 macOS encrypted config unlock/save".
- Watch CI; on failure, push fixes to the same branch.
- Wait for explicit confirmation before merging.

After merge:
- Update agent_docs/mac_project_backlog.md MAC5 status to "done <date>"
  with a brief outcome paragraph mirroring the MAC1-MAC4 entries.
- Append a dated decision to agent_docs/project_decisions.md titled
  "MAC5 native Swift encryption: deliberate format duplication".
  Capture: the architectural choice (Swift CryptoKit reimplementation
  rather than .NET-on-Mac IPC), the rationale (Apple Silicon prefers
  native; cross-arch hosting is heavier than the duplication; format
  is locked by cross-language tests so drift is observable), and the
  exit ramp (MAC6 may host the .NET API on Mac and could optionally
  consolidate config IO back into ConfigStore — that's a MAC6
  decision, not a regret).
- Update agent_docs/mac_platform_dependency_audit.md with the same
  exception cross-referenced.
- Update agent_docs/project_state.md "In flight" / "Recently shipped"
  / "Last session" sections.
- README and QUICKSTART updates that drop the "no Mac unlock" caveat
  ship in the same PR, not a follow-up.
```
