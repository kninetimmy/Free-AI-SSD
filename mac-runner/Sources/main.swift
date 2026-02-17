import SwiftUI
import Foundation

struct PortableConfig: Codable {
    struct Model: Codable {
        let name: String
        let status: String
    }

    let models: [Model]
}

final class RunnerViewModel: ObservableObject {
    @Published var ssdRoot: URL?
    @Published var modelNames: [String] = []
    @Published var selectedModel: String = ""
    @Published var prompt: String = ""
    @Published var response: String = ""
    @Published var status: String = "Idle"

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
        let ollama = root.appendingPathComponent("mac/tools/ollama/ollama")
        guard FileManager.default.fileExists(atPath: ollama.path) else { status = "Missing mac/tools/ollama/ollama"; return }

        let p = Process()
        p.executableURL = ollama
        p.arguments = ["serve"]
        p.environment = [
            "OLLAMA_MODELS": root.appendingPathComponent("models").path,
            "OLLAMA_HOST": "127.0.0.1:\(hostPort)",
            "OLLAMA_ORIGINS": "http://127.0.0.1,http://localhost"
        ]
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
