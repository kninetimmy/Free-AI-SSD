import Foundation
#if canImport(Darwin)
import Darwin
#endif

// MARK: - mac-prep-host sidecar controller
//
// MAC17: spawns the net8.0 sidecar, sends the init handshake on stdin,
// and exposes async command send / response wait methods to the SwiftUI
// flow. Mirrors mac-runner/Sources/MacRunnerHostController.swift but for
// the prep sidecar's stdin command surface (no HTTP API).

enum PrepHostError: Error, LocalizedError {
    case binaryNotFound(String)
    case spawnFailed(String)
    case alreadyRunning
    case notRunning
    case commandFailed(String)
    case timedOut(String)

    var errorDescription: String? {
        switch self {
        case .binaryNotFound(let m): return "Mac prep host not found: \(m)"
        case .spawnFailed(let m):    return "Failed to spawn mac-prep-host: \(m)"
        case .alreadyRunning:        return "Mac prep host is already running."
        case .notRunning:            return "Mac prep host is not running."
        case .commandFailed(let m):  return "Sidecar command failed: \(m)"
        case .timedOut(let m):       return "Sidecar command timed out: \(m)"
        }
    }
}

/// Decoded "result: <command> <json>" line from the sidecar.
struct PrepHostResult {
    let command: String
    let payload: [String: Any]
}

final class PrepHostController: @unchecked Sendable {
    enum Status: Equatable {
        case stopped
        case starting
        case ready
        case crashed(message: String)
    }

    var onStatusChange: ((Status) -> Void)?
    var onLogLine: ((String) -> Void)?

    private(set) var status: Status = .stopped {
        didSet {
            if let cb = onStatusChange {
                DispatchQueue.main.async { cb(self.status) }
            }
        }
    }

    private var process: Process?
    private var stdoutPipe: Pipe?
    private var stderrPipe: Pipe?
    private var stdinPipe: Pipe?
    private var stderrBuffer: String = ""

    /// Per-command continuation registry. Keyed by command name; the next
    /// "result: <command> <json>" line resumes the matching continuation.
    /// We only allow one pending command at a time per name, which is
    /// fine for the linear prep flow (Swift drives, never parallel).
    private var pendingResults: [String: CheckedContinuation<PrepHostResult, Error>] = [:]
    private var pendingReady: CheckedContinuation<Void, Error>?
    private let stateQueue = DispatchQueue(label: "PrepHostController.state")

    /// Spawn the sidecar with the given handshake and wait for "ready".
    /// Throws if the binary cannot be located, the process refuses to
    /// start, or the host emits an error before the ready line.
    func startAndWaitReady(ssdRoot: URL, ollamaHost: String = "http://127.0.0.1:11434", testMode: Bool = false) async throws {
        guard process == nil else { throw PrepHostError.alreadyRunning }

        let binary = try Self.resolveHostBinary(ssdRoot: ssdRoot)
        status = .starting

        let proc = Process()
        proc.executableURL = binary
        proc.arguments = testMode ? ["--test-mode"] : []

        let stdin = Pipe()
        let stdout = Pipe()
        let stderr = Pipe()
        proc.standardInput = stdin
        proc.standardOutput = stdout
        proc.standardError = stderr
        proc.environment = ProcessInfo.processInfo.environment

        proc.terminationHandler = { [weak self] terminated in
            guard let self else { return }
            DispatchQueue.main.async {
                if terminated.terminationStatus == 0 {
                    self.status = .stopped
                } else {
                    let trimmed = self.stderrBuffer.trimmingCharacters(in: .whitespacesAndNewlines)
                    let msg = trimmed.isEmpty
                        ? "mac-prep-host exited with code \(terminated.terminationStatus)."
                        : trimmed
                    self.status = .crashed(message: msg)
                    self.failAllPending(PrepHostError.commandFailed(msg))
                }
                self.process = nil
                self.stdinPipe = nil
                self.stdoutPipe = nil
                self.stderrPipe = nil
            }
        }

        do {
            try proc.run()
        } catch {
            throw PrepHostError.spawnFailed(error.localizedDescription)
        }

        process = proc
        stdinPipe = stdin
        stdoutPipe = stdout
        stderrPipe = stderr
        stderrBuffer = ""

        Self.beginReading(pipe: stdout) { [weak self] line in
            self?.handleStdoutLine(line)
        }
        Self.beginReading(pipe: stderr) { [weak self] line in
            self?.handleStderrLine(line)
        }

        // Init handshake: { ssdRoot, ollamaHost }
        let payload: [String: Any] = [
            "ssdRoot": ssdRoot.path,
            "ollamaHost": ollamaHost,
        ]
        do {
            let data = try JSONSerialization.data(withJSONObject: payload, options: [])
            stdin.fileHandleForWriting.write(data)
            stdin.fileHandleForWriting.write("\n".data(using: .utf8)!)
        } catch {
            proc.terminate()
            throw PrepHostError.spawnFailed("Failed to encode handshake JSON: \(error.localizedDescription)")
        }

        // Wait for the "ready" line.
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            stateQueue.sync { self.pendingReady = cont }
        }
    }

    /// Send a command and await its `result: <command> <json>` response.
    /// Throws on sidecar crash, timeout, or stderr-reported failure.
    func send(_ command: String, timeout: TimeInterval = 600) async throws -> PrepHostResult {
        guard let stdin = stdinPipe else { throw PrepHostError.notRunning }

        let commandName = command.split(separator: " ", maxSplits: 1).first.map(String.init) ?? command
        let line = command + "\n"

        return try await awaitCommandResult(commandName: commandName, timeout: timeout) {
            if let payload = line.data(using: .utf8) {
                stdin.fileHandleForWriting.write(payload)
            }
        }
    }

    /// Internal seam: register a continuation for `commandName`, run
    /// `dispatch` to fire the side-effect that should eventually
    /// produce a matching `result:` line (e.g. write to stdin), and
    /// race the wait against a `Task.sleep` timeout. On cancellation
    /// or timeout the slot is removed and the continuation resumes
    /// with `CancellationError()` — fixes the leak/double-resume
    /// identified in the PR #193 Gemini review by making the
    /// pendingResults bookkeeping cancel-safe.
    ///
    /// Race-handling: registration and cancellation contend for the
    /// same `pendingResults` slot. Both run inside `stateQueue.sync`,
    /// and the registration path also checks `Task.isCancelled` inside
    /// the critical section so a cancellation that fires *before* the
    /// registration body runs cannot leak the continuation.
    internal func awaitCommandResult(
        commandName: String,
        timeout: TimeInterval,
        dispatch: @escaping @Sendable () -> Void
    ) async throws -> PrepHostResult {
        let queue = self.stateQueue
        return try await withThrowingTaskGroup(of: PrepHostResult.self) { group in
            group.addTask { [weak self] in
                // Bind to a local let so the nested closures capture a
                // clean constant — Swift 6 strict-concurrency treats
                // the implicit recapture of `[weak self]` inside a
                // concurrent onCancel/continuation closure as a
                // captured-var error.
                let weakSelf = self
                return try await withTaskCancellationHandler {
                    try await withCheckedThrowingContinuation { (cont: CheckedContinuation<PrepHostResult, Error>) in
                        var didRegister = false
                        queue.sync {
                            if Task.isCancelled {
                                cont.resume(throwing: CancellationError())
                            } else if let strong = weakSelf {
                                strong.pendingResults[commandName] = cont
                                didRegister = true
                            } else {
                                // Controller deallocated between addTask
                                // capture and registration — without this
                                // branch the continuation would never be
                                // resumed and CheckedContinuation would
                                // trap on dealloc.
                                cont.resume(throwing: PrepHostError.notRunning)
                            }
                        }
                        if didRegister {
                            dispatch()
                        }
                    }
                } onCancel: {
                    queue.sync {
                        if let cont = weakSelf?.pendingResults.removeValue(forKey: commandName) {
                            cont.resume(throwing: CancellationError())
                        }
                    }
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
    }

    /// Test-only seam: number of outstanding pending result
    /// continuations. Used by PrepAppTests to assert the cancel-path
    /// fix for Issue #1 actually frees the slot.
    internal var pendingResultsCountForTest: Int {
        stateQueue.sync { pendingResults.count }
    }

    /// Test-only seam: drives the same drain that deinit performs so
    /// PrepAppTests can pin the Fix 4 (PR #195 review) behavior
    /// without having to actually deallocate a controller mid-flight.
    internal func failAllPendingForTest(_ error: Error) {
        failAllPending(error)
    }

    /// Async graceful shutdown. Writes "shutdown\n", waits up to
    /// `timeout` for the child to exit, then SIGTERM, then SIGKILL.
    /// Async so the @MainActor caller (PrepViewModel.finalize /
    /// .restart) does not freeze the UI while the child winds down.
    /// Safe to call when already stopped.
    func shutdown(timeout: TimeInterval = 2.0) async {
        guard let proc = process else {
            status = .stopped
            return
        }
        sendShutdownAndCloseStdin()

        // Cancellation note: `try?` swallows Task.sleep's
        // CancellationError, so without an explicit isCancelled
        // check a cancelled task would spin this loop until the
        // deadline. Break early instead — the SIGTERM/SIGKILL
        // escalation below still runs so the child is reaped.
        let deadline = Date().addingTimeInterval(timeout)
        while proc.isRunning && Date() < deadline {
            if Task.isCancelled { break }
            try? await Task.sleep(nanoseconds: 50_000_000) // 50ms
        }
        if proc.isRunning {
            proc.terminate()
            let killDeadline = Date().addingTimeInterval(0.5)
            while proc.isRunning && Date() < killDeadline {
                if Task.isCancelled { break }
                try? await Task.sleep(nanoseconds: 50_000_000)
            }
            if proc.isRunning {
                kill(proc.processIdentifier, SIGKILL)
            }
        }
        clearProcessRefs()
    }

    deinit {
        // deinit cannot be async. Best-effort: SIGTERM the child if
        // still running and let the OS reap it. terminationHandler
        // captures [weak self] so it no-ops once self is gone — which
        // means it won't drain pendingResults, so we have to do it
        // here ourselves. Otherwise CheckedContinuation traps on its
        // own dealloc when a command is in flight at teardown.
        process?.terminate()
        failAllPending(PrepHostError.notRunning)
    }

    private func sendShutdownAndCloseStdin() {
        if let stdin = stdinPipe {
            if let payload = "shutdown\n".data(using: .utf8) {
                stdin.fileHandleForWriting.write(payload)
            }
            stdin.fileHandleForWriting.closeFile()
        }
    }

    private func clearProcessRefs() {
        process = nil
        stdinPipe = nil
        stdoutPipe = nil
        stderrPipe = nil
        status = .stopped
    }

    // MARK: - Stream handling

    private func handleStdoutLine(_ line: String) {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }

        if trimmed == "ready" {
            DispatchQueue.main.async { self.status = .ready }
            stateQueue.sync {
                if let cont = self.pendingReady {
                    self.pendingReady = nil
                    cont.resume()
                }
            }
            return
        }

        if trimmed.hasPrefix("log: ") {
            let msg = String(trimmed.dropFirst("log: ".count))
            if let cb = onLogLine { DispatchQueue.main.async { cb(msg) } }
            return
        }

        if trimmed.hasPrefix("result: ") {
            // "result: <command> <json>"
            let body = String(trimmed.dropFirst("result: ".count))
            let parts = body.split(separator: " ", maxSplits: 1, omittingEmptySubsequences: true)
            guard parts.count == 2 else { return }
            let commandName = String(parts[0])
            let jsonStr = String(parts[1])
            guard
                let data = jsonStr.data(using: .utf8),
                let payload = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
            else {
                return
            }
            let result = PrepHostResult(command: commandName, payload: payload)
            stateQueue.sync {
                if let cont = self.pendingResults.removeValue(forKey: commandName) {
                    cont.resume(returning: result)
                }
            }
            return
        }
    }

    private func handleStderrLine(_ line: String) {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        stderrBuffer.append(trimmed + "\n")
        if let cb = onLogLine {
            DispatchQueue.main.async { cb("[stderr] " + trimmed) }
        }
    }

    private func failAllPending(_ error: Error) {
        stateQueue.sync {
            for (_, cont) in self.pendingResults { cont.resume(throwing: error) }
            self.pendingResults.removeAll()
            if let cont = self.pendingReady {
                self.pendingReady = nil
                cont.resume(throwing: error)
            }
        }
    }

    // MARK: - Static helpers

    /// Resolve the published mac-prep-host binary. Search order:
    ///   1. The bundle's Resources/prep-host/ (production layout —
    ///      CI bundles the published sidecar there).
    ///   2. <ssdRoot>/mac/prep-host/FreeAiSsd.MacPrepHost (future
    ///      runtime updater layout — not used in MVP).
    ///   3. Repo's published output (developer launches).
    static func resolveHostBinary(ssdRoot: URL) throws -> URL {
        let bundleResources = Bundle.main.resourceURL?
            .appendingPathComponent("prep-host")
            .appendingPathComponent("FreeAiSsd.MacPrepHost")
        if let url = bundleResources, FileManager.default.isExecutableFile(atPath: url.path) {
            return url
        }

        let ssdHosted = ssdRoot.appendingPathComponent("mac/prep-host/FreeAiSsd.MacPrepHost")
        if FileManager.default.isExecutableFile(atPath: ssdHosted.path) {
            return ssdHosted
        }

        // Developer fallback: <repo-root>/mac-prep-host/bin/Release/net8.0/osx-arm64/publish/...
        if let envRepoRoot = ProcessInfo.processInfo.environment["FREEAI_REPO_ROOT"] {
            let dev = URL(fileURLWithPath: envRepoRoot)
                .appendingPathComponent("mac-prep-host/bin/Release/net8.0/osx-arm64/publish/FreeAiSsd.MacPrepHost")
            if FileManager.default.isExecutableFile(atPath: dev.path) {
                return dev
            }
        }

        throw PrepHostError.binaryNotFound(
            "Searched bundle Resources/prep-host/, \(ssdRoot.path)/mac/prep-host/, and FREEAI_REPO_ROOT publish dir.")
    }

    private static func beginReading(pipe: Pipe, onLine: @escaping (String) -> Void) {
        var buffer = ""
        pipe.fileHandleForReading.readabilityHandler = { handle in
            let data = handle.availableData
            if data.isEmpty { return }
            if let s = String(data: data, encoding: .utf8) {
                buffer.append(s)
                while let nl = buffer.firstIndex(of: "\n") {
                    let line = String(buffer[..<nl])
                    buffer.removeSubrange(...nl)
                    onLine(line)
                }
            }
        }
    }
}
