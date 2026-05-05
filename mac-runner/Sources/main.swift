import SwiftUI
import Foundation

// MARK: - PortableConfig parsing
//
// The full PortableConfig schema lives in shared/PortableConfig.cs. The Mac
// runner only inspects a few fields (the models array for picker population),
// so we read the JSON dynamically as `[String: Any]`. This preserves any
// fields the Mac runner doesn't know about across a save round-trip — a
// regression here would silently drop user data on encrypted-config save.

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
    @Published var unlockDialogPresented: Bool = false
    @Published var unlockDialogPassword: String = ""
    @Published var unlockDialogError: String? = nil

    private var process: Process?
    private var hostPort = 11434

    /// Cached PBKDF2 output held while the session is unlocked. Kept private
    /// and zeroized on every lock path (manual lock, app background, app
    /// terminate, deinit). Never surface the raw key to anything outside this
    /// view model.
    private var unlockMaterial: UnlockMaterial?

    /// In-memory plaintext config preserved across saves so unknown fields
    /// (anything Swift doesn't model directly) survive a Mac-side mutation.
    /// Mirrors the C# PortableConfig round-trip behavior. nil while locked.
    private var portableConfig: [String: Any]?

    /// Notification observers retained so they can be removed on deinit.
    private var notificationObservers: [NSObjectProtocol] = []

    init() {
        self.ssdRoot = inferSsdRoot()
        if ssdRoot == nil { pickSsdRoot() }
        loadConfig()
        registerLifecycleHooks()
    }

    deinit {
        for o in notificationObservers { NotificationCenter.default.removeObserver(o) }
        unlockMaterial?.zeroize()
    }

    /// Lock-on-background and lock-on-terminate so the derived AES key never
    /// outlives the user's active session. Manual lock is wired through
    /// `lockSession()`.
    private func registerLifecycleHooks() {
        let nc = NotificationCenter.default
        notificationObservers.append(nc.addObserver(
            forName: NSApplication.willResignActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in self?.lockSession(reason: "App backgrounded") })
        notificationObservers.append(nc.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: .main
        ) { [weak self] _ in self?.lockSession(reason: "App terminating") })
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

        if SsdEncryption.isEffectivelyEncryptedForWriteGuard(ssdRoot: root) {
            // Encrypted drive: present the unlock dialog. Don't read or
            // decode anything else until the user completes unlock.
            isEncryptedLocked = true
            modelNames = []
            selectedModel = ""
            portableConfig = nil
            unlockMaterial?.zeroize()
            unlockMaterial = nil
            status = "Encrypted SSD locked"
            unlockDialogError = nil
            unlockDialogPassword = ""
            unlockDialogPresented = true
            return
        }

        isEncryptedLocked = false
        let configURL = root.appendingPathComponent("config/portable-config.json")
        guard let data = try? Data(contentsOf: configURL),
              let parsed = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            portableConfig = nil
            modelNames = []
            selectedModel = ""
            return
        }
        portableConfig = parsed
        applyConfigToUi(parsed)
    }

    /// Called from the unlock dialog. Decrypts the on-SSD blob with the
    /// provided password, populates UI state, and runs the plaintext
    /// migration so a stale plaintext file from a pre-encryption session is
    /// either merged in or removed.
    func attemptUnlock(password: String) {
        guard let root = ssdRoot else {
            unlockDialogError = "Select an SSD root first."
            return
        }

        let result = SsdEncryption.tryUnlockPortableConfig(ssdRoot: root, password: password)
        switch result {
        case .failure(let err):
            unlockDialogError = err.errorDescription ?? "Unlock failed."
            log("Unlock failed: \(err.errorDescription ?? "unknown")")
            return
        case .success(let unlocked):
            unlockMaterial?.zeroize()
            unlockMaterial = unlocked.material
            portableConfig = unlocked.config
            isEncryptedLocked = false
            unlockDialogPresented = false
            unlockDialogPassword = ""
            unlockDialogError = nil
            applyConfigToUi(unlocked.config)
            status = "Unlocked"
            log("SSD unlocked successfully.")

            // Mirror Windows: absorb or discard a stale plaintext file so
            // the drive can never accumulate plaintext secrets.
            let migration = SsdEncryption.tryMigratePlaintext(
                ssdRoot: root, material: unlocked.material,
                log: { [weak self] line in self?.log(line) })
            if case .mergedFromPlaintext(let merged) = migration {
                portableConfig = merged
                applyConfigToUi(merged)
            }
        }
    }

    /// Zeroes the cached UnlockMaterial and drops the in-memory plaintext.
    /// Safe to call when no session is unlocked.
    func lockSession(reason: String = "Locked") {
        guard unlockMaterial != nil || portableConfig != nil else { return }
        unlockMaterial?.zeroize()
        unlockMaterial = nil
        portableConfig = nil
        modelNames = []
        selectedModel = ""
        if let root = ssdRoot, SsdEncryption.isEffectivelyEncryptedForWriteGuard(ssdRoot: root) {
            isEncryptedLocked = true
            status = "Encrypted SSD locked"
        }
        log(reason)
    }

    /// Mutates the in-memory plaintext config and writes it back through the
    /// encrypted save path. Mirrors `IConfigStore.SaveAsync`'s contract: refuses
    /// to save when the drive is encrypted but no session is unlocked, so the
    /// caller can prompt for re-unlock instead of silently dropping the change.
    func saveConfig(mutate: ([String: Any]) -> [String: Any]) {
        guard let root = ssdRoot else {
            log("Save refused: no SSD root selected.")
            return
        }
        let isEncrypted = SsdEncryption.isEffectivelyEncryptedForWriteGuard(ssdRoot: root)
        guard var current = portableConfig else {
            if isEncrypted {
                log("Save refused: drive is encrypted but session is locked.")
            } else {
                log("Save refused: portable-config not loaded.")
            }
            return
        }
        current = mutate(current)
        portableConfig = current

        if isEncrypted {
            guard let material = unlockMaterial else {
                log("Save refused: drive is encrypted but session is locked.")
                return
            }
            do {
                try SsdEncryption.saveEncryptedConfig(
                    ssdRoot: root, config: current, material: material)
                log("Saved encrypted portable-config.")
            } catch {
                log("Encrypted save failed: \(error.localizedDescription)")
            }
        } else {
            // Plaintext drives still get plaintext writes — same as today.
            // The encrypted drive code path above is the new MAC5 surface.
            let configURL = root.appendingPathComponent("config/portable-config.json")
            do {
                let data = try JSONSerialization.data(
                    withJSONObject: current,
                    options: [.prettyPrinted, .sortedKeys])
                try data.write(to: configURL, options: [.atomic])
                log("Saved plaintext portable-config.")
            } catch {
                log("Plaintext save failed: \(error.localizedDescription)")
            }
        }
    }

    /// Pulls the `installed` model names from the parsed config dictionary.
    /// Tolerates either string status values ("Installed") or numeric ones
    /// (the C# enum serializer flips between them depending on settings).
    private func applyConfigToUi(_ config: [String: Any]) {
        let models = (config["models"] as? [[String: Any]]) ?? []
        let installed = models.compactMap { entry -> String? in
            guard let name = entry["name"] as? String, !name.isEmpty else { return nil }
            if let status = entry["status"] as? String {
                return status.lowercased() == "installed" ? name : nil
            }
            // Numeric enum form: 1 == Installed in ModelInstallStatus.
            if let status = entry["status"] as? Int {
                return status == 1 ? name : nil
            }
            return nil
        }
        modelNames = installed
        selectedModel = installed.first ?? ""
    }

    func startOllama() {
        guard process == nil, let root = ssdRoot else { return }
        if isEncryptedLocked {
            status = "Unlock encrypted SSD first"
            unlockDialogPresented = true
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
                if !vm.isEncryptedLocked {
                    Button("Lock") { vm.lockSession(reason: "Manual lock") }
                }
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
        .sheet(isPresented: $vm.unlockDialogPresented) { UnlockSheet(vm: vm) }
    }
}

struct UnlockSheet: View {
    @ObservedObject var vm: RunnerViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Unlock encrypted SSD").font(.title3)
            Text("Enter the password set during PrepApp finalization.")
                .font(.callout)
                .foregroundColor(.secondary)
            SecureField("Password", text: $vm.unlockDialogPassword)
                .textFieldStyle(.roundedBorder)
            if let err = vm.unlockDialogError {
                Text(err).foregroundColor(.red).font(.callout)
            }
            HStack {
                Spacer()
                Button("Cancel") {
                    vm.unlockDialogPresented = false
                    vm.unlockDialogPassword = ""
                    vm.unlockDialogError = nil
                }
                Button("Unlock") {
                    vm.attemptUnlock(password: vm.unlockDialogPassword)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(vm.unlockDialogPassword.isEmpty)
            }
        }
        .padding(20)
        .frame(minWidth: 360)
    }
}

@main
struct RunnerMacApp: App {
    var body: some Scene {
        WindowGroup { ContentView() }
    }
}
