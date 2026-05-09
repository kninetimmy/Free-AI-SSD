# MAC36 — Mac Runner UX bundle (auto-lock-on-blur + streaming + send spinner)

Branch: `mac36-runner-ux` off `main`. Target version v1.3.18.

## Context
- Backlog entry: `agent_docs/mac_project_backlog.md` lines 3169-3198 (`MAC36`).
- Driver: v1.3.17 mac field test (2026-05-09). Three sub-bugs share one PR.
- Project state: `agent_docs/project_state.md`. MAC30 made encryption opt-in (default OFF), which is why blur-lock has to go.
- Cross-OS parity rule (saved feedback memory): Windows already streams + has Send busy state; Windows has no blur-lock observer. Mac-only PR.
- **Hard constraint:** macOS 11 deployment target (`.github/workflows/build.yml:277` pins `arm64-apple-macos11.0`). `URLSession.bytes(for:)` is macOS 12+ and won't compile. Use `URLSessionDataDelegate.urlSession(_:dataTask:didReceive:)` for chunk delivery — same approach the existing buffered NDJSON parser at `mac-runner/Sources/main.swift:1066` calls out.

## Server contract (read-only, do not change)
`runner-core/Services/RunnerLocalApiService.cs:209-263` defines `POST /chat/stream`:
- ContentType `application/x-ndjson`
- Frames (one JSON object per `\n`-terminated line):
  - `{type: "start", model, usedRagContext, sources}`
  - `{type: "token", token}`
  - `{type: "rag-warning", message}`  (precedes a `complete` frame)
  - `{type: "complete", usedRagContext, sources, responseText}`
  - `{type: "error", message}`
- Auth: Bearer header same as `/chat` (reuse `apiKeyForLocalApiRequest()`).

## Changes

### 1. Drop auto-lock-on-blur — `mac-runner/Sources/main.swift`
- In `registerLifecycleHooks()` (~line 155), delete the `willResignActiveNotification` observer registration (3 lines). Keep `willTerminateNotification`.
- Update the doc comment above `registerLifecycleHooks()` to: lock on app quit only; with MAC30 making encryption opt-in, blur-lock is removed because plaintext SSDs have no key to zeroize and the teardown was annoying.

### 2. Streaming chat — `mac-runner/Sources/main.swift`
- New `@Published var isSending: Bool = false` on `RunnerViewModel` (siblings live around line 64-103).
- Add a private property to hold the in-flight task: `private var activeChatTask: URLSessionDataTask?`.
- Add a `URLSessionDataDelegate` conformance — either on `RunnerViewModel` directly or via a small private inner class. If you make `RunnerViewModel` itself the delegate, remember the URLSession retains its delegate strongly; instantiate a per-call `URLSession(configuration: .default, delegate: self, delegateQueue: nil)` and `finishTasksAndInvalidate()` on completion to break the cycle.
- Rewrite `sendPrompt()` (currently `main.swift:665`):
  - Early-return on empty model / prompt as today.
  - Cancel any prior `activeChatTask`.
  - Set `vm.response = ""`, `clearRagState()`, `isSending = true`, `status = "Sending..."`.
  - Build POST to `\(baseUrl)/api/chat/stream` with the same JSON body and Bearer header as today.
  - Maintain an `NdjsonFrameBuffer` (see #3) on the delegate. On each `didReceive data:`, append, decode each completed line as JSON via `JSONSerialization`, dispatch to main:
    - `start` → ignore (already cleared state).
    - `token` → `self.response.append(token)`.
    - `rag-warning` → `self.ragWarning = message`.
    - `complete` → set `responseSources`, `usedRagContext`, status `"Answered with sources"` if rag else `"Answered"`. **Do not** overwrite `self.response` with `responseText` — tokens already produced the same string and overwriting causes a visible flicker. (If a token frame is ever dropped, `responseText` is the authoritative fallback; consider only assigning when `self.response.isEmpty`.)
    - `error` → status `"Chat failed: \(message)"`, clear rag state.
  - On `didCompleteWithError`: if error and not cancelled, set status accordingly. Always set `isSending = false` and clear `activeChatTask` on main.
- `lockSession()` (line 282): cancel `activeChatTask?.cancel()` before `hostController.shutdown()`. After zeroize, set `isSending = false`.

### 3. NDJSON frame buffer — new file `mac-runner/Sources/NdjsonFrameBuffer.swift`
Pure helper, no UI/Foundation-URL dependencies. Public API:
```swift
struct NdjsonFrameBuffer {
    private var tail = Data()
    mutating func append(_ chunk: Data) -> [Data]   // returns complete lines (without trailing \n)
    mutating func flush() -> Data?                  // returns any pending tail at stream end
}
```
Behavior:
- Split on `0x0A` (`\n`). Lines may be empty (skip them when decoding). `\r\n` should also work — strip a trailing `\r` from each emitted line.
- Bytes after the last `\n` go into `tail` for the next call.

### 4. Tests — new file `mac-runner/Tests/NdjsonFrameBufferTests.swift`
Mirror the test style of `mac-runner/Tests/SsdEncryptionTests.swift` (XCTest-free, `@main` test runner pattern — check what's there). Cover:
- Single complete line in one chunk.
- Two complete lines in one chunk.
- One line split across two chunks.
- Trailing partial line stays in tail.
- Empty chunk is a no-op.
- `\r\n` line ending handled.
- `flush()` returns the final unterminated tail.

### 5. UI — `mac-runner/Sources/main.swift` ContentView (~line 1207)
Replace `Button("Send") { vm.sendPrompt() }` with:
```swift
Button(action: { vm.sendPrompt() }) {
    HStack {
        if vm.isSending { ProgressView().controlSize(.small) }
        Text("Send")
    }
}
.disabled(vm.isSending
          || vm.selectedModel.isEmpty
          || vm.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
```

### 6. CI — `.github/workflows/build.yml`
- Runner build (line ~277): add `mac-runner/Sources/NdjsonFrameBuffer.swift` to the `swiftc` source list.
- Tests: the existing ssd-encryption-tests target at line 168 only includes SsdEncryption files. Add a new `swiftc` step that compiles `NdjsonFrameBuffer.swift` + `NdjsonFrameBufferTests.swift` (`-target arm64-apple-macos11.0 -parse-as-library -O`) and runs the binary, mirroring the ssd-encryption-tests step. Or extend the existing tests step to include both — your call, take whichever keeps the YAML cleaner.

## Local validation before pushing
- `swiftc` Runner.app source list per the CI command — must compile clean.
- `swiftc` the new tests binary; run it, expect all pins green.
- `swiftc` mac-prep-tests (unchanged) — confirm it still compiles, parity smoke.
- `dotnet build FreeAiSsd.sln -c Release` — should be unaffected; quick sanity run.

## CI + ship
- Push, watch all four jobs (mac-runner-build, mac-prep-build, windows-build, packaging if it triggers). Per CLAUDE.md: never push to main; PR + watch CI + report + wait for explicit confirmation before merging.
- After merge, dispatch v1.3.18 via `gh workflow run` (`version=1.3.18 include_macos=true`), watch the four release jobs, confirm both ZIPs published.

## Out of scope
- F4 "Auto-lock on idle" preference (filed as MAC36 follow-up note).
- MAC37 finalize observability — separate ticket.
- Any Windows-side change (parity audit confirms no-op).

## Wrap-up
After CI green + merge + v1.3.18 ship, run the `wrap-up` skill to roll PR + version into `agent_docs/`.
