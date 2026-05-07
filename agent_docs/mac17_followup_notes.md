# MAC17 Follow-up Notes

Captured from Gemini code review on PR #193 (kninetimmy/mac17-mac-prepapp-mvp).
The MAC17 MVP merged with these items knowingly deferred — none are
correctness blockers for the MVP smoke (drive format → stage →
encrypt → readiness), but several materially improve UX on a real
Mac and one is a latent leak/crash if the prep flow is ever cancelled
mid-command.

Sequencing:
1. Land Issue #1 first — it is the only correctness bug in the set
   (continuation leak + double-resume crash on retry).
2. Bundle issues #2/#3/#4/#7 together as a single "background-thread
   the long ops" pass — they all have the same shape (`Task.detached`
   off `@MainActor` for sync work) and reviewing them as a cluster
   makes the threading boundary review tractable.
3. Issue #5 is structural — defer until #2/#3/#4/#7 land so the
   refactor isn't fighting an in-flight threading change.
4. Issue #6 is a one-line UX fix; bundle into whichever PR ships
   first.

## Issue #1 (HIGH) — Continuation leak on cancel/timeout in `PrepHostController.send`

**Path:** `mac-prep-app/Sources/PrepHostController.swift:176`

**Problem.** `send(_ command:timeout:)` uses `withThrowingTaskGroup`
to race a `withCheckedThrowingContinuation` registration against a
`Task.sleep` timeout. When the timeout wins (`group.cancelAll()`
fires) the continuation registered in `pendingResults[commandName]`
is never resumed. Two consequences:

1. **Memory leak** — `pendingResults[commandName]` holds the
   continuation forever (or until the controller deinits).
2. **Crash on retry** — if the same command is sent again, the new
   continuation overwrites the leaked one, but the next stdout
   `result: <command> <json>` line still tries to resume the *first*
   continuation (lookup is by name and the slot is occupied). If the
   leaked continuation was already orphaned by a prior cancel that
   somehow did call `resume`, double-resume traps. Either way the
   slot ownership model is broken.

**Current code (abridged):**

```swift
return try await withThrowingTaskGroup(of: PrepHostResult.self) { group in
    group.addTask {
        try await withCheckedThrowingContinuation { (cont: ...) in
            self.stateQueue.sync { self.pendingResults[commandName] = cont }
            // write command to stdin
        }
    }
    group.addTask {
        try await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
        throw PrepHostError.timedOut(commandName)
    }
    let first = try await group.next()!
    group.cancelAll()
    return first
}
```

**Proposed fix.** Wrap the continuation in `withTaskCancellationHandler`
so cancellation removes the slot and resumes with an error:

```swift
try await withTaskCancellationHandler {
    try await withCheckedThrowingContinuation { cont in
        self.stateQueue.sync { self.pendingResults[commandName] = cont }
        // write command to stdin
    }
} onCancel: {
    self.stateQueue.sync {
        if let cont = self.pendingResults.removeValue(forKey: commandName) {
            cont.resume(throwing: CancellationError())
        }
    }
}
```

**Test.** Extend `mac-prep-app/Tests/PrepAppTests.swift` with a case
that spawns a fake host (or mocks the continuation registry directly),
sends a command, cancels the surrounding task before any result line
arrives, and asserts `pendingResults.isEmpty` afterwards. Re-sending
the same command name must succeed.

---

## Issue #2 (HIGH) — `PrepViewModel.formatSelected` blocks `@MainActor` on disk format

**Path:** `mac-prep-app/Sources/PrepViewModel.swift:165`

**Problem.** `formatSelected` is `@MainActor` (the whole class is) and
calls `driveService.format(...)` synchronously. `format` shells out to
`/usr/sbin/diskutil eraseDisk` and `waitUntilExit()` — for a real
external SSD that's tens of seconds. The UI freezes for the duration:
the `ProgressView` doesn't animate, the log scroll doesn't tick.

**Current code:**

```swift
appendLog("Formatting \(candidate.identifier) as exFAT (label: \(volumeLabel))…")
do {
    try driveService.format(
        diskIdentifier: candidate.identifier,
        label: volumeLabel,
        fileSystem: "ExFAT",
        onOutput: { [weak self] line in
            Task { @MainActor in self?.appendLog(line) }
        })
    appendLog("Format complete.")
    ...
```

**Proposed fix.** Hop off `@MainActor` for the format call:

```swift
let identifier = candidate.identifier
let label = volumeLabel
do {
    try await Task.detached(priority: .userInitiated) { [driveService] in
        try driveService.format(
            diskIdentifier: identifier,
            label: label,
            fileSystem: "ExFAT",
            onOutput: { line in
                Task { @MainActor in self?.appendLog(line) }
            })
    }.value
    ...
```

`onOutput` already hops back to `@MainActor` for log appends, so the
boundary is clean.

**Test.** Hard to test format in CI (no real disk). The fix is
review-and-smoke: visual confirmation on a real Mac that the
`ProgressView` ticks during format.

---

## Issue #3 (HIGH) — `PrepViewModel.writeEncryptionAndProceed` blocks `@MainActor` on PBKDF2

**Path:** `mac-prep-app/Sources/PrepViewModel.swift:258`

**Problem.** `EncryptedConfigWriter.writeInitialEncryptedConfig` runs
PBKDF2-HMAC-SHA256 with 210,000 iterations + AES-GCM seal + atomic
two-file commit. Total time on Apple Silicon is on the order of
~500ms–1s. Calling it synchronously on `@MainActor` freezes the UI
for that window.

**Current code:**

```swift
do {
    let payload = InitialPortableConfigPayload()
    try encryptedConfigWriter.writeInitialEncryptedConfig(
        ssdRoot: mount, payload: payload, passphrase: passphrase)
    appendLog("Encrypted config written.")
    ...
```

**Proposed fix.** Same `Task.detached` shape as Issue #2. Pass
`passphrase` and `payload` by value into the detached task; zeroize
the passphrase strings on the `@MainActor` side after the detached
task completes.

**Test.** Add an `EncryptedConfigWriter` unit test (link the writer
into `PrepAppTests.swift` if not already linked — it is for the
fixture-write subcommand) that times the call and asserts it
completes under a reasonable bound. Plus a smoke confirmation that
the spinner ticks during encryption setup.

---

## Issue #4 (MEDIUM) — `PrepHostController.shutdown` busy-waits on `@MainActor`

**Path:** `mac-prep-app/Sources/PrepHostController.swift:210`

**Problem.** `shutdown(timeout:)` uses `Thread.sleep(forTimeInterval:)`
in two `while proc.isRunning` loops totaling up to 2.5 seconds. It's
called from `PrepViewModel.finalize()` and `PrepViewModel.restart()`
which are both `@MainActor` — so the UI thread blocks for up to 2.5s
on the "Finish" or "Restart" button.

**Current code:**

```swift
func shutdown(timeout: TimeInterval = 2.0) {
    ...
    let deadline = Date().addingTimeInterval(timeout)
    while proc.isRunning && Date() < deadline {
        Thread.sleep(forTimeInterval: 0.05)
    }
    if proc.isRunning {
        proc.terminate()
        let killDeadline = Date().addingTimeInterval(0.5)
        while proc.isRunning && Date() < killDeadline {
            Thread.sleep(forTimeInterval: 0.05)
        }
        if proc.isRunning {
            kill(proc.processIdentifier, SIGKILL)
        }
    }
    ...
```

**Proposed fix.** Make `shutdown` async, use `Task.sleep` for the
poll wait. Update both call sites in `PrepViewModel` to `await
hostController.shutdown()`.

`deinit { shutdown() }` is awkward because `deinit` cannot be `async`.
For deinit specifically, the existing busy-wait is acceptable —
deinit runs at object teardown which isn't on the active UI path.
Keep a synchronous `deinit`-only fallback or accept that the deinit
case will block briefly off the main path.

**Test.** Construct a fake mac-prep-host (a tiny shell script that
sleeps 5s) and assert `shutdown()` returns within ~2.5s on the calling
queue. The async signature lets the test assert on `Task.now`-style
timing without a busy wait of its own.

---

## Issue #5 (MEDIUM) — SSD layout hardcoded in `PrepViewModel.runStaging`

**Path:** `mac-prep-app/Sources/PrepViewModel.swift:198`

**Problem.** `runStaging` builds the SSD directory tree by hardcoding
the subdirectory list:

```swift
let macSubdirs = [
    "windows", "windows/tools", "windows/tools/ollama",
    "windows/tools/prereqs", "windows/runner",
    "mac", "mac/tools", "mac/tools/ollama",
    "models", "models/blobs", "models/whisper",
    "config", "logs", "cache", "docs", "docs/libraries",
]
```

This duplicates `shared/SsdLayout.cs`'s `EnsureStructure(...)`. If
the C# side adds a directory (which has happened — see git log on
`shared/SsdLayout.cs`), the Mac PrepApp silently ships drives missing
that directory and downstream operations fail in subtle ways.

**Proposed fix.** Add a sidecar command `ensure-structure` that
delegates to `SsdLayout.EnsureStructure(_ssdRoot)` in C#. The Swift
flow calls `host.send("ensure-structure")` instead of the hardcoded
list. The sidecar already has prep-core / shared as ProjectReferences,
so `SsdLayout.EnsureStructure` is one method call away.

**Test.** Extend `MacPrepHostConstructionTests` with a test that
sends `ensure-structure` against a fresh temp dir and asserts every
directory `SsdLayout` declares actually exists.

**Sequencing note.** Land *after* issues #2/#3/#4/#7 so this refactor
isn't fighting an in-flight threading change.

---

## Issue #6 (MEDIUM) — Encryption toggle UX mismatch

**Path:** `mac-prep-app/Sources/main.swift:222`

**Problem.** `EncryptionSetupStepView` exposes a `Toggle` for
"Encrypt the drive's configuration store" that the user can flip
off, but `PrepViewModel.writeEncryptionAndProceed` rejects that path
with a `currentStep = .failed` transition. So an interactive control
appears available but causes a hard failure.

**Current code (main.swift:222):**

```swift
Toggle("Encrypt the drive's configuration store", isOn: $vm.enableEncryption)
```

**Current behavior (PrepViewModel:248):**

```swift
if !enableEncryption {
    currentStep = .failed(message: "Plaintext-mode prep is out of scope for MAC17 MVP. ...")
    return
}
```

**Proposed fix.** Either:
- (a) Disable the toggle visually (Gemini's literal suggestion):
  `.disabled(true)` and add explanatory caption text.
- (b) Remove the toggle entirely from the MVP flow — encryption is
  always on, no user choice. Simpler UI, removes the `enableEncryption`
  flag's only call site.

**Recommendation:** (b) — encryption is mandatory for MAC17 MVP per
the existing logic; the toggle was wishful UI for a future plaintext
mode that doesn't exist yet. Removing it cleanly is cleaner than
graying it out and having to explain why.

---

## Issue #7 (MEDIUM) — `PrepViewModel.refreshCandidates` blocks `@MainActor` on diskutil list

**Path:** `mac-prep-app/Sources/PrepViewModel.swift:90`

**Problem.** `driveService.listExternalCandidates()` shells out to
`/usr/sbin/diskutil list -plist external` plus a `diskutil info` per
candidate. On a system with several USB devices that's several
hundred milliseconds. Synchronous on `@MainActor` produces a
noticeable hitch when the user clicks Refresh or auto-refresh fires
after format.

**Current code:**

```swift
func refreshCandidates() async {
    statusMessage = "Scanning external drives…"
    do {
        let list = try driveService.listExternalCandidates()
        candidates = list
        ...
```

**Proposed fix.** Same `Task.detached` shape:

```swift
do {
    let list = try await Task.detached(priority: .userInitiated) { [driveService] in
        try driveService.listExternalCandidates()
    }.value
    candidates = list
    ...
```

**Test.** Hard to unit-test the detached hop without real `diskutil`
output. Visual smoke on a real Mac with several USB devices plugged.

---

## Bundling recommendation

- **MAC17a (this followup):** ship Issue #1 + Issue #6 + the
  background-threading cluster (#2/#3/#4/#7) as one PR. Keep
  Issue #5 as MAC17b since it's structural and benefits from
  landing after the threading changes settle.
- All MAC17 invariants stay intact: encrypted-config format,
  destructive NSAlert, MAC5 plaintext invariant, cross-language
  fixture roundtrip.
