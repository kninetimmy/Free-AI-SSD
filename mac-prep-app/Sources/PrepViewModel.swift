import Foundation
import SwiftUI
#if canImport(AppKit)
import AppKit
#endif

// MARK: - PrepViewModel
//
// MAC17: orchestrator for the SwiftUI flow. Holds @Published state the views
// observe and coordinates DiskutilDriveService (drive listing + format),
// PrepHostController (sidecar spawn + commands), and EncryptedConfigWriter
// (encrypted-config write via SsdEncryption.swift).
//
// All UI confirmations are routed through native NSAlert per the
// 2026-05-06 Mac UI design language decision (PrepApp leans pure-native
// because it owns destructive disk ops). The destructive-erase
// confirmation specifically uses NSAlert.alertStyle = .critical so the
// user sees the standard system-red destructive button.

@MainActor
final class PrepViewModel: ObservableObject {
    // Flow state
    @Published var currentStep: PrepFlowStep = .welcome

    // Drive selection
    @Published var candidates: [DiskCandidate] = []
    @Published var selectedCandidate: DiskCandidate?
    @Published var prepareForWindowsToo: Bool = true   // default cross-platform
    @Published var volumeLabel: String = "FREEAI"

    // Status / progress / log
    @Published var statusMessage: String = ""
    @Published var logLines: [String] = []
    @Published var isBusy: Bool = false

    // Encryption — mandatory for MAC17 MVP, no toggle. (MAC17a #6)
    @Published var passphrase: String = ""
    @Published var passphraseConfirm: String = ""

    // Starter models — F2: rich catalog matches Windows PrepApp's
    // merged Models grid projection. Populated by discoverCatalog()
    // (bundled JSON via sidecar) and replaced by refreshCatalog() when
    // the user fetches the live ollama.com/library list.
    @Published var starterCatalog: [StarterModelDisplayEntry] = []
    @Published var selectedStarterModels: Set<String> = []
    @Published var catalogStatusText: String = ""
    @Published var isRefreshingCatalog: Bool = false

    // Readiness
    @Published var readinessItems: [ReadinessRow] = []

    // Services (deliberately non-@Published — these are stable references)
    private let driveService = DiskutilDriveService()
    private let hostController = PrepHostController()
    private let encryptedConfigWriter = EncryptedConfigWriter()

    init() {
        // Default starter selection is set after discoverCatalog() runs
        // (the bundled list is loaded via the sidecar once it spawns,
        // not from a hardcoded array — F2).
        hostController.onLogLine = { [weak self] line in
            Task { @MainActor in self?.appendLog(line) }
        }
    }

    var ssdRoot: URL? {
        guard let mount = selectedCandidate?.mountPoint else { return nil }
        return mount
    }

    var availableStarterModels: [StarterModelDisplayEntry] { starterCatalog }

    // MARK: - Step transitions

    func startFlow() {
        currentStep = .driveSelection
        Task { await refreshCandidates() }
    }

    func refreshCandidates() async {
        statusMessage = "Scanning external drives…"
        // MAC17a #7: hop diskutil list off @MainActor — on a system with
        // several USB devices it's several hundred milliseconds and
        // produces a noticeable hitch on Refresh / post-format auto-refresh.
        let driveSvc = self.driveService
        do {
            let list = try await Task.detached(priority: .userInitiated) {
                try driveSvc.listExternalCandidates()
            }.value
            candidates = list
            statusMessage = list.isEmpty
                ? "No external drives found. Plug an SSD in and click Refresh."
                : "Select the drive to prepare."
            // Keep prior selection if still present, otherwise reset.
            if let sel = selectedCandidate, !list.contains(where: { $0.identifier == sel.identifier }) {
                selectedCandidate = nil
            }
        } catch {
            statusMessage = "Drive scan failed: \(error.localizedDescription)"
            candidates = []
            selectedCandidate = nil
        }
    }

    /// Move from drive-selection → erase confirmation. The actual
    /// confirmation prompt fires in `confirmEraseAndProceed()` via
    /// NSAlert; this step exists so the user has to click an explicit
    /// "Continue" before the system prompt appears.
    func proceedToEraseConfirmation() {
        guard selectedCandidate != nil else {
            statusMessage = "Pick a drive first."
            return
        }
        currentStep = .eraseConfirmation
    }

    /// Show a native NSAlert with destructive styling. If the user
    /// confirms, format the disk; otherwise return to drive selection.
    func confirmEraseAndProceed() {
#if canImport(AppKit)
        guard let candidate = selectedCandidate else { return }

        let alert = NSAlert()
        alert.messageText = "Erase \(candidate.displayName)?"
        let mountInfo = candidate.mountPoint?.path ?? "(unmounted)"
        alert.informativeText = """
        This will destroy ALL data on \(candidate.identifier) (\(candidate.sizeDisplay)).
        Mount: \(mountInfo)
        Format: exFAT (Windows + macOS compatible).

        This cannot be undone.
        """
        alert.alertStyle = .critical
        alert.addButton(withTitle: "Erase")
        alert.addButton(withTitle: "Cancel")

        let response = alert.runModal()
        if response == .alertFirstButtonReturn {
            Task { await formatSelected() }
        } else {
            currentStep = .driveSelection
        }
#else
        // Non-AppKit builds (test harness) auto-proceed — the destructive
        // call requires a real macOS environment anyway.
        Task { await formatSelected() }
#endif
    }

    private func formatSelected() async {
        guard let candidate = selectedCandidate else { return }
        currentStep = .formatting
        isBusy = true
        defer { isBusy = false }

        appendLog("Formatting \(candidate.identifier) as exFAT (label: \(volumeLabel))…")
        // MAC17a #2: hop diskutil eraseDisk off @MainActor. For a real
        // external SSD that's tens of seconds; without this hop the
        // SwiftUI ProgressView freezes and the log scroll doesn't tick.
        let driveSvc = self.driveService
        let identifier = candidate.identifier
        let label = volumeLabel
        // Bind to a local let so the inner Task captures a clean
        // constant — Swift 6 strict-concurrency treats the implicit
        // recapture of `[weak self]` inside a nested concurrent
        // closure as a captured-var error.
        let logSink: @Sendable (String) -> Void = { [weak self] line in
            let weakSelf = self
            Task { @MainActor in weakSelf?.appendLog(line) }
        }
        do {
            try await Task.detached(priority: .userInitiated) {
                try driveSvc.format(
                    diskIdentifier: identifier,
                    label: label,
                    fileSystem: "ExFAT",
                    onOutput: logSink)
            }.value
            appendLog("Format complete.")
            // diskutil mounts the new volume automatically; refresh so
            // we pick up the new mount path on the same identifier.
            await refreshCandidates()
            if let updated = candidates.first(where: { $0.identifier == candidate.identifier }) {
                selectedCandidate = updated
            }
            currentStep = .staging
            await runStaging()
        } catch {
            appendLog("Format failed: \(error.localizedDescription)")
            currentStep = .failed(message: "Format failed: \(error.localizedDescription)")
        }
    }

    private func runStaging() async {
        guard let mount = selectedCandidate?.mountPoint else {
            currentStep = .failed(message: "Selected drive has no mount point after format.")
            return
        }
        isBusy = true
        defer { isBusy = false }

        // logs/ has to exist before the sidecar starts so its SsdLogger
        // can open a log file at construction time; everything else in
        // the SSD layout is laid down by the sidecar's ensure-structure
        // command (which delegates to shared/SsdLayout.cs's
        // EnsureStructure — single source of truth across C# and Swift).
        do {
            try FileManager.default.createDirectory(
                at: mount.appendingPathComponent("logs"),
                withIntermediateDirectories: true)
        } catch {
            currentStep = .failed(message: "Failed to create logs directory: \(error.localizedDescription)")
            return
        }

        // Spawn the sidecar.
        do {
            try await hostController.startAndWaitReady(ssdRoot: mount)
        } catch {
            currentStep = .failed(message: "Sidecar startup failed: \(error.localizedDescription)")
            return
        }

        // Lay down the rest of the SSD layout via the sidecar.
        do {
            _ = try await hostController.send("ensure-structure")
            appendLog("SSD layout created.")
        } catch {
            currentStep = .failed(message: "Failed to create SSD layout: \(error.localizedDescription)")
            return
        }

        // Stage runner + ollama + prereqs sequentially. Each command
        // emits log lines via onLogLine which we already forward.
        do {
            _ = try await hostController.send("stage-runner")
            _ = try await hostController.send("stage-ollama")
            _ = try await hostController.send("stage-prereqs")
            appendLog("Staging complete.")

            // F2: pull bundled catalog before showing the picker so
            // the user sees rich entries (tag + tier + best-at) — same
            // shape Windows' merged grid renders. Soft-failure: if
            // the bundled load fails the picker stays empty and the
            // status text explains; user can still type a custom tag
            // once the model-pull step exposes free-form entry.
            await discoverCatalog()

            currentStep = .encryptionSetup
        } catch {
            currentStep = .failed(message: "Staging failed: \(error.localizedDescription)")
        }
    }

    /// Load the bundled starter-models.json via the sidecar's
    /// discover-catalog command. Called once after staging completes —
    /// the sidecar is up by then and StarterModelCatalogLoader inside
    /// it reads the prep-core embedded resource without any network.
    func discoverCatalog() async {
        do {
            let result = try await hostController.send("discover-catalog")
            let entries = decodeStarterEntries(from: result.payload)
            starterCatalog = entries.map(StarterModelDisplayEntry.from)
            if let warning = result.payload["warning"] as? String, !warning.isEmpty {
                catalogStatusText = "Bundled catalog warning: \(warning)"
                appendLog("Catalog warning: \(warning)")
            } else if !starterCatalog.isEmpty {
                catalogStatusText = "Bundled catalog: \(starterCatalog.count) models. Click Refresh to fetch the latest from Ollama."
            }
            // Default-pick the first entry so the picker isn't completely
            // empty for a new user (matches the prior MVP behavior of
            // pre-selecting the first hardcoded model).
            if selectedStarterModels.isEmpty, let first = starterCatalog.first {
                selectedStarterModels.insert(first.tag)
            }
        } catch {
            appendLog("Failed to load bundled catalog: \(error.localizedDescription)")
            catalogStatusText = "Bundled catalog unavailable; click Refresh to fetch from Ollama."
        }
    }

    /// F2: fetch the live catalog from ollama.com/library via the
    /// sidecar's refresh-catalog arm. Soft-failure mirrors Windows
    /// behavior — the existing in-memory catalog stays in place and
    /// the status text surfaces the failure reason.
    func refreshCatalog() async {
        if isRefreshingCatalog { return }
        isRefreshingCatalog = true
        defer { isRefreshingCatalog = false }

        do {
            // 30s budget — the prep-core service has its own 10s
            // timeout for the HTTP fetch; this outer budget covers
            // sidecar dispatch overhead.
            let result = try await hostController.send("refresh-catalog", timeout: 30)
            let ok = result.payload["ok"] as? Bool ?? false
            if ok {
                let entries = decodeStarterEntries(from: result.payload)
                if !entries.isEmpty {
                    starterCatalog = entries.map(StarterModelDisplayEntry.from)
                    let sourceUrl = result.payload["sourceUrl"] as? String ?? "live"
                    catalogStatusText = "Live catalog: \(starterCatalog.count) models from \(sourceUrl)."
                    appendLog("Refreshed catalog with \(starterCatalog.count) models.")

                    // Drop selections that no longer exist in the
                    // live catalog; default-pick the first if the
                    // user is left with nothing selected.
                    let liveTags = Set(starterCatalog.map(\.tag))
                    selectedStarterModels.formIntersection(liveTags)
                    if selectedStarterModels.isEmpty, let first = starterCatalog.first {
                        selectedStarterModels.insert(first.tag)
                    }
                } else {
                    catalogStatusText = "Refresh returned no entries; existing list kept."
                }
            } else {
                let reason = result.payload["reason"] as? String ?? "unknown"
                let errorMsg = result.payload["error"] as? String ?? "(no detail)"
                catalogStatusText = "Refresh failed (\(reason)): \(errorMsg). Existing list kept."
                appendLog("Catalog refresh failed: \(errorMsg)")
            }
        } catch {
            catalogStatusText = "Refresh failed: \(error.localizedDescription). Existing list kept."
            appendLog("Catalog refresh failed: \(error.localizedDescription)")
        }
    }

    func writeEncryptionAndProceed() async {
        guard let mount = selectedCandidate?.mountPoint else {
            currentStep = .failed(message: "No mount point.")
            return
        }
        isBusy = true
        defer { isBusy = false }

        if passphrase.isEmpty || passphrase != passphraseConfirm {
            statusMessage = "Passphrases must match and not be empty."
            return
        }

        // MAC17a #3: hop PBKDF2 + AES-GCM seal off @MainActor. PBKDF2
        // at 210k iterations + AES-GCM seal totals ~500ms–1s on Apple
        // Silicon — long enough to freeze the SwiftUI spinner without
        // this hop.
        let writer = self.encryptedConfigWriter
        let pass = self.passphrase
        let payload = InitialPortableConfigPayload()
        do {
            try await Task.detached(priority: .userInitiated) {
                try writer.writeInitialEncryptedConfig(
                    ssdRoot: mount, payload: payload, passphrase: pass)
            }.value
            appendLog("Encrypted config written.")

            // Zeroize the in-memory passphrase strings. Swift Strings
            // aren't backed by a fixed buffer the way Data is, but
            // overwriting the storage minimizes lifetime and clarifies
            // intent.
            passphrase = ""
            passphraseConfirm = ""

            currentStep = .modelPull
            await pullStarterModels()
        } catch {
            currentStep = .failed(message: "Encryption write failed: \(error.localizedDescription)")
        }
    }

    private func pullStarterModels() async {
        if selectedStarterModels.isEmpty {
            appendLog("No starter models selected; skipping model pull.")
            currentStep = .readiness
            await runReadiness()
            return
        }

        isBusy = true
        defer { isBusy = false }

        // Mac Ollama lifecycle for MVP: the user starts Mac Runner
        // separately and the model pull happens against that running
        // instance. For now, attempt the pull; on failure surface a
        // friendly message rather than a hard fail — model pull can be
        // re-attempted from the Mac Runner.
        for tag in selectedStarterModels {
            appendLog("Pulling \(tag)…")
            do {
                _ = try await hostController.send("pull-model \(tag)", timeout: 1800)
                appendLog("Pulled \(tag).")
            } catch {
                appendLog("Pull failed for \(tag): \(error.localizedDescription)")
                appendLog("(This is non-fatal — you can pull models later from Mac Runner.)")
            }
        }
        currentStep = .readiness
        await runReadiness()
    }

    private func runReadiness() async {
        isBusy = true
        defer { isBusy = false }
        do {
            let result = try await hostController.send("readiness")
            if let items = result.payload["items"] as? [[String: Any]] {
                readinessItems = items.compactMap { item in
                    guard
                        let name = item["name"] as? String,
                        let status = item["status"] as? String
                    else { return nil }
                    let detail = item["detail"] as? String ?? ""
                    return ReadinessRow(name: name, status: status, detail: detail)
                }
            }
            currentStep = .done
        } catch {
            currentStep = .failed(message: "Readiness check failed: \(error.localizedDescription)")
        }
    }

    func finalize() async {
        // MAC17a #4: graceful sidecar shutdown is now async so the
        // Finish button doesn't freeze the UI for up to 2.5s while the
        // child winds down.
        await hostController.shutdown()
        appendLog("Drive ready. Launch Free AI SSD Runner from the SSD's mac/ folder.")
    }

    /// Reset to the welcome step after a failure so the user can retry
    /// from a clean slate. (More targeted re-entry — e.g. retry just
    /// staging — is a future MAC17 follow-up.)
    func restart() async {
        // MAC17a #4: async shutdown so the Restart button doesn't
        // freeze the UI for up to 2.5s.
        await hostController.shutdown()
        readinessItems = []
        logLines = []
        statusMessage = ""
        passphrase = ""
        passphraseConfirm = ""
        currentStep = .welcome
    }

    private func appendLog(_ line: String) {
        // Cap the log buffer at 500 lines to avoid unbounded growth.
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        logLines.append(trimmed)
        if logLines.count > 500 {
            logLines.removeFirst(logLines.count - 500)
        }
    }
}

struct ReadinessRow: Identifiable, Hashable {
    let name: String
    let status: String
    let detail: String
    var id: String { name }

    var statusColor: Color {
        switch status.lowercased() {
        case "pass", "ok":      return .brandStatusSuccess
        case "warn", "warning": return .brandStatusWarning
        case "fail", "error":   return .brandStatusDanger
        default:                return .brandStatusInfo
        }
    }
}
