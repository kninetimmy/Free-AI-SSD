import Foundation
#if canImport(Darwin)
import Darwin
#endif

// MARK: - Mac sidecar host controller (MAC6)
//
// Spawns the net8.0 sidecar bundled inside Runner.app/Contents/Resources/
// runner-host/ (or, in dev builds, the repo's published output) and pipes
// the unlocked PortableConfig + the resolved Ollama host URL on stdin.
//
// Plaintext-config invariant from MAC5: the Swift app must never write the
// in-memory PortableConfig dictionary to disk to hand it to the sidecar. It
// flows over stdin only. If the user locks the drive or exits the app, the
// host shuts down and the unlock material is gone.

enum MacRunnerHostError: Error, LocalizedError {
    case binaryNotFound(String)
    case spawnFailed(String)
    case alreadyRunning

    var errorDescription: String? {
        switch self {
        case .binaryNotFound(let m): return "Mac runner host not found: \(m)"
        case .spawnFailed(let m): return "Failed to spawn mac-runner-host: \(m)"
        case .alreadyRunning: return "Mac runner host is already running."
        }
    }
}

final class MacRunnerHostController {
    enum Status {
        case stopped
        case starting
        case running(baseUrl: String)
        case crashed(message: String)
    }

    /// Single-line callbacks the SwiftUI view model can subscribe to. All
    /// callbacks fire on the main queue so observers can mutate `@Published`
    /// state without dispatching themselves.
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

    /// Spawn the sidecar with the given handshake. Throws if the binary cannot
    /// be located or the process refuses to start. The host writes
    /// "ready: <baseUrl>" on stdout once Kestrel is listening.
    func start(ssdRoot: URL, ollamaHost: String, config: [String: Any]) throws {
        guard process == nil else { throw MacRunnerHostError.alreadyRunning }

        let binary = try Self.resolveHostBinary(ssdRoot: ssdRoot)

        status = .starting

        let proc = Process()
        proc.executableURL = binary
        proc.arguments = []

        let stdin = Pipe()
        let stdout = Pipe()
        let stderr = Pipe()
        proc.standardInput = stdin
        proc.standardOutput = stdout
        proc.standardError = stderr

        // Hand the parent's environment through so SsdLogger can resolve paths,
        // but do NOT inject the unlocked PortableConfig anywhere — it travels
        // over stdin only. (MAC5 plaintext invariant.)
        proc.environment = ProcessInfo.processInfo.environment

        proc.terminationHandler = { [weak self] terminated in
            guard let self else { return }
            DispatchQueue.main.async {
                if terminated.terminationStatus == 0 {
                    self.status = .stopped
                } else {
                    let trimmed = self.stderrBuffer.trimmingCharacters(in: .whitespacesAndNewlines)
                    let message = trimmed.isEmpty
                        ? "Mac runner host exited with code \(terminated.terminationStatus)."
                        : trimmed
                    self.status = .crashed(message: message)
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
            throw MacRunnerHostError.spawnFailed(error.localizedDescription)
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

        // Init handshake: { ssdRoot, ollamaHost, config }
        let payload: [String: Any] = [
            "ssdRoot": ssdRoot.path,
            "ollamaHost": ollamaHost,
            "config": config
        ]

        do {
            let handshakeData = try JSONSerialization.data(withJSONObject: payload, options: [])
            stdin.fileHandleForWriting.write(handshakeData)
            stdin.fileHandleForWriting.write("\n".data(using: .utf8)!)
        } catch {
            proc.terminate()
            throw MacRunnerHostError.spawnFailed("Failed to encode handshake JSON: \(error.localizedDescription)")
        }
    }

    /// Graceful shutdown. Writes "shutdown\n" to stdin, waits up to 2s, then
    /// SIGTERMs / SIGKILLs the child if it is still running. Safe to call
    /// when the host is already stopped.
    func shutdown(timeout: TimeInterval = 2.0) {
        guard let proc = process else {
            status = .stopped
            return
        }

        if let stdin = stdinPipe {
            // Best-effort write — if the child is already gone the write
            // raises SIGPIPE inside the FileHandle layer; FileHandle catches
            // it and we just proceed to terminate. We use the macOS 11–safe
            // Data write API rather than the macOS 13+ throwing variant.
            stdin.fileHandleForWriting.writeabilityHandler = nil
            if let payload = "shutdown\n".data(using: .utf8) {
                stdin.fileHandleForWriting.write(payload)
            }
            stdin.fileHandleForWriting.closeFile()
        }

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

        process = nil
        stdinPipe = nil
        stdoutPipe = nil
        stderrPipe = nil
        status = .stopped
    }

    deinit {
        shutdown()
    }

    // MARK: - Stream handling

    private func handleStdoutLine(_ line: String) {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }

        if trimmed.hasPrefix("ready: ") {
            let baseUrl = String(trimmed.dropFirst("ready: ".count))
            DispatchQueue.main.async {
                self.status = .running(baseUrl: baseUrl)
            }
        } else if trimmed.hasPrefix("log: ") {
            let logBody = String(trimmed.dropFirst("log: ".count))
            if let cb = onLogLine {
                DispatchQueue.main.async { cb(logBody) }
            }
        } else {
            if let cb = onLogLine {
                DispatchQueue.main.async { cb(trimmed) }
            }
        }
    }

    private func handleStderrLine(_ line: String) {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }

        DispatchQueue.main.async {
            self.stderrBuffer.append(trimmed + "\n")
        }

        if let cb = onLogLine {
            DispatchQueue.main.async { cb("[stderr] \(trimmed)") }
        }
    }

    private static func beginReading(pipe: Pipe, onLine: @escaping (String) -> Void) {
        let handle = pipe.fileHandleForReading
        var buffer = Data()
        handle.readabilityHandler = { fileHandle in
            let chunk = fileHandle.availableData
            if chunk.isEmpty {
                if !buffer.isEmpty, let line = String(data: buffer, encoding: .utf8) {
                    onLine(line)
                }
                fileHandle.readabilityHandler = nil
                return
            }
            buffer.append(chunk)
            while let nl = buffer.firstIndex(of: 0x0A) {
                let lineData = buffer.subdata(in: 0..<nl)
                buffer.removeSubrange(0...nl)
                if let line = String(data: lineData, encoding: .utf8) {
                    onLine(line)
                }
            }
        }
    }

    // MARK: - Path resolution

    /// Resolves the host binary path. Production (post-restructure): the
    /// sidecar ships INSIDE Runner.app/Contents/Resources/runner-host/, and
    /// Runner.app sits at the SSD root — so the bundle Resources path is the
    /// production source. A legacy <ssdRoot>/mac/runner-host/ probe is kept
    /// first as a harmless fast-path for any drive that still has a
    /// separately-staged host. Finally falls back to the dev build output
    /// when running directly from the repo. Throws if no candidate resolves.
    static func resolveHostBinary(ssdRoot: URL) throws -> URL {
        let fm = FileManager.default
        var candidates: [URL] = []

        // 1. Legacy: separately staged on the SSD (pre-restructure layout).
        //    Not produced anymore; harmless if absent.
        candidates.append(
            ssdRoot.appendingPathComponent("mac/runner-host/FreeAiSsd.MacRunnerHost")
        )

        // 2. Production: bundled inside Runner.app/Contents/Resources/runner-host/.
        if let resourceURL = Bundle.main.resourceURL {
            candidates.append(
                resourceURL.appendingPathComponent("runner-host/FreeAiSsd.MacRunnerHost")
            )
        }

        // 3. Dev build output. Walk up from the bundle in case we're running
        //    via `swift run` or the swiftc-built dev binary in out/Runner.app.
        let bundleDir = Bundle.main.bundleURL.deletingLastPathComponent()
        var devCursor: URL? = bundleDir
        for _ in 0..<6 {
            guard let cur = devCursor else { break }
            candidates.append(
                cur.appendingPathComponent(
                    "mac-runner-host/bin/Release/net8.0/osx-arm64/publish/FreeAiSsd.MacRunnerHost"
                )
            )
            candidates.append(
                cur.appendingPathComponent(
                    "mac-runner-host/bin/Release/net8.0/publish/FreeAiSsd.MacRunnerHost"
                )
            )
            devCursor = cur.deletingLastPathComponent()
        }

        for url in candidates {
            if fm.isExecutableFile(atPath: url.path) {
                return url
            }
            if fm.fileExists(atPath: url.path) {
                // Best-effort chmod +x for copies that lost executable bits
                // during ZIP extraction; only return after executability is
                // confirmed so later bundled/dev candidates can still work.
                if fm.isReadableFile(atPath: url.path) {
                    _ = try? fm.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
                }
                if fm.isExecutableFile(atPath: url.path) {
                    return url
                }
            }
        }

        let tried = candidates.map { $0.path }.joined(separator: "\n  ")
        throw MacRunnerHostError.binaryNotFound("Searched paths:\n  \(tried)")
    }
}
