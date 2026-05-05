import SwiftUI
import Foundation

struct PortableConfig: Codable {
    struct Model: Codable {
        let name: String
        let status: String
    }

    let models: [Model]
}

/// Mirrors `OllamaPackageTrustAttestation` written by the Windows PrepApp's
/// staging path (`ArtifactStagingService.StageMacOllamaAsync`). The fields
/// must stay in lockstep with the C# record's JSON shape.
struct OllamaPackageTrustAttestation: Codable {
    let Version: String
    let Url: String
    let Sha256: String
    let VerifiedAtUtc: String
}

/// Pinned Mac Ollama package metadata. Must match
/// `OllamaPackageTrustPolicy.DefaultMacPackage` in shared/. If you bump one,
/// bump both — the runtime gate compares the on-SSD attestation to these
/// constants and refuses to launch on any mismatch.
enum PinnedMacOllama {
    static let url = "https://github.com/ollama/ollama/releases/download/v0.5.7/ollama-darwin.zip"
    static let sha256 = "09ad6bb2edf7cb78619a0932c93c544c362c6ac738c7d5531b3b1b87ac619971"
    static let attestationFileName = "ollama-package-trust.json"
}

enum TrustGateResult {
    case allowed
    case refused(String)
}

final class RunnerViewModel: ObservableObject {
    @Published var ssdRoot: URL?
    @Published var modelNames: [String] = []
    @Published var selectedModel: String = ""
    @Published var prompt: String = ""
    @Published var response: String = ""
    @Published var status: String = "Idle"
    @Published var isEncryptedLocked: Bool = false

    private var process: Process?
    private var hostPort = 11434

    init() {
        self.ssdRoot = inferSsdRoot()
        if ssdRoot == nil { pickSsdRoot() }
        loadConfig()
    }

    func inferSsdRoot() -> URL? {
        let bundleURL = Bundle.main.bundleURL
        if bundleURL.path.contains("/mac/Runner.app") {
            return bundleURL.deletingLastPathComponent().deletingLastPathComponent()
        }
        return nil
    }

    func pickSsdRoot() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = false
        if panel.runModal() == .OK, let url = panel.url,
           FileManager.default.fileExists(atPath: url.appendingPathComponent("config").path),
           FileManager.default.fileExists(atPath: url.appendingPathComponent("models").path) {
            ssdRoot = url
            loadConfig()
        }
    }

    func loadConfig() {
        guard let root = ssdRoot else { return }
        let encryptionStateURL = root.appendingPathComponent("config/encryption-state.json")
        if FileManager.default.fileExists(atPath: encryptionStateURL.path) {
            isEncryptedLocked = true
            modelNames = []
            selectedModel = ""
            status = "Encrypted SSD locked (mac unlock not supported yet)"
            return
        }

        isEncryptedLocked = false
        let configURL = root.appendingPathComponent("config/portable-config.json")
        guard let data = try? Data(contentsOf: configURL),
              let config = try? JSONDecoder().decode(PortableConfig.self, from: data) else {
            return
        }

        modelNames = config.models.filter { $0.status.lowercased() == "installed" }.map { $0.name }
        selectedModel = modelNames.first ?? ""
    }

    func startOllama() {
        guard process == nil, let root = ssdRoot else { return }
        if isEncryptedLocked {
            status = "Unlock encrypted SSD first (use Windows runner)"
            return
        }

        let ollama = root.appendingPathComponent("mac/tools/ollama/ollama")
        guard FileManager.default.fileExists(atPath: ollama.path) else { status = "Missing mac/tools/ollama/ollama"; return }

        // MAC4: refuse to launch the staged binary unless the on-SSD trust
        // attestation matches the pinned URL + SHA-256. PrepApp writes this
        // file after hash-verifying the upstream payload and confirming the
        // arm64 slice is present; the Swift app re-checks at every launch
        // so a tampered or missing attestation fails closed here too.
        switch evaluateTrustGate(ssdRoot: root) {
        case .refused(let message):
            status = message
            log("Trust gate refused launch: \(message)")
            return
        case .allowed:
            break
        }

        let p = Process()
        p.executableURL = ollama
        p.arguments = ["serve"]
        var env = ProcessInfo.processInfo.environment
        env["OLLAMA_MODELS"] = root.appendingPathComponent("models").path
        env["OLLAMA_HOST"] = "127.0.0.1:\(hostPort)"
        env["OLLAMA_ORIGINS"] = "http://127.0.0.1,http://localhost"
        p.environment = env
        p.terminationHandler = { [weak self] proc in
            DispatchQueue.main.async {
                guard let self else { return }
                self.process = nil
                self.status = "Stopped"
                self.log("Ollama exited with code \(proc.terminationStatus)")
            }
        }
        do {
            try p.run()
            process = p
            status = "Running on 127.0.0.1:\(hostPort)"
            log("Started ollama")
        } catch {
            status = "Failed to start ollama: \(error.localizedDescription)"
            log(status)
        }
    }

    func stopOllama() {
        process?.terminate()
        process = nil
        status = "Stopped"
        log("Stopped ollama")
    }

    func sendPrompt() {
        guard !selectedModel.isEmpty else { return }
        guard let url = URL(string: "http://127.0.0.1:\(hostPort)/api/generate") else { return }
        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.addValue("application/json", forHTTPHeaderField: "Content-Type")
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["model": selectedModel, "prompt": prompt, "stream": false])

        URLSession.shared.dataTask(with: req) { data, _, _ in
            guard let data else { return }
            if let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let text = obj["response"] as? String {
                DispatchQueue.main.async { self.response = text }
            }
        }.resume()
    }

    /// Reads `<ssdRoot>/mac/tools/ollama/ollama-package-trust.json` and
    /// compares it to the pinned Mac Ollama metadata. Refuses launch when
    /// the file is missing, malformed, or disagrees on URL / SHA-256.
    /// Re-hashing the 180MB binary on every launch is too slow; we
    /// cross-check the attestation against the embedded constants instead.
    /// PrepApp staging is responsible for the actual SHA-256 verification
    /// before this attestation is ever written.
    func evaluateTrustGate(ssdRoot: URL) -> TrustGateResult {
        let attestationURL = ssdRoot
            .appendingPathComponent("mac/tools/ollama")
            .appendingPathComponent(PinnedMacOllama.attestationFileName)

        guard FileManager.default.fileExists(atPath: attestationURL.path) else {
            return .refused("Missing trust attestation. Re-stage the macOS Ollama bundle from PrepApp.")
        }

        guard let data = try? Data(contentsOf: attestationURL) else {
            return .refused("Trust attestation unreadable. Re-stage the macOS Ollama bundle.")
        }

        guard let attestation = try? JSONDecoder().decode(OllamaPackageTrustAttestation.self, from: data) else {
            return .refused("Trust attestation malformed. Re-stage the macOS Ollama bundle.")
        }

        if attestation.Url != PinnedMacOllama.url {
            return .refused("Trust attestation URL does not match the pinned Mac Ollama source. Re-stage from PrepApp.")
        }

        if attestation.Sha256.lowercased() != PinnedMacOllama.sha256.lowercased() {
            return .refused("Trust attestation digest does not match the pinned Mac Ollama SHA-256. Re-stage from PrepApp.")
        }

        return .allowed
    }

    private func log(_ message: String) {
        guard let root = ssdRoot else { return }
        let path = root.appendingPathComponent("logs/macos-runner.log").path
        let line = "[\(ISO8601DateFormatter().string(from: Date()))] \(message)\n"
        if let data = line.data(using: .utf8) {
            if FileManager.default.fileExists(atPath: path), let h = FileHandle(forWritingAtPath: path) {
                h.seekToEndOfFile(); h.write(data); h.closeFile()
            } else {
                try? data.write(to: URL(fileURLWithPath: path), options: .atomic)
            }
        }
    }
}

struct ContentView: View {
    @StateObject var vm = RunnerViewModel()

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Free AI SSD macOS Runner").font(.title2)
            Text(vm.status)
            HStack {
                Button("Select SSD") { vm.pickSsdRoot() }
                Button("Start") { vm.startOllama() }
                Button("Stop") { vm.stopOllama() }
            }
            Picker("Model", selection: $vm.selectedModel) {
                ForEach(vm.modelNames, id: \.self) { Text($0).tag($0) }
            }
            TextEditor(text: $vm.prompt).frame(height: 120)
            Button("Send") { vm.sendPrompt() }
            TextEditor(text: $vm.response).frame(height: 200)
        }
        .padding(16)
        .frame(minWidth: 720, minHeight: 560)
    }
}

@main
struct RunnerMacApp: App {
    var body: some Scene {
        WindowGroup { ContentView() }
    }
}
