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

    // Encryption — opt-in, default OFF (MAC30). When enableEncryption is
    // false, the EncryptionSetupStepView hides the passphrase fields and
    // writeConfigAndProceed() routes through PlaintextConfigWriter.
    @Published var enableEncryption: Bool = false
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

    // F2a: picker filter state. Search filters tag + sizeTier + bestAt
    // (case-insensitive substring). showOnlyMostPopular subsets to the
    // top 15 entries by pullCount; entries without a pull count drop
    // out of that view (the bundled catalog has no pull counts, so
    // toggling it on a bundled list yields zero rows — visible signal
    // to Refresh first, by design).
    @Published var modelSearchText: String = ""
    @Published var showOnlyMostPopular: Bool = false
    /// C26: how many entries the "Most popular" filter exposes.
    /// Mirrors the WPF dropdown — default 15, options 10/15/25/50.
    @Published var mostPopularLimit: Int = 15
    /// C26: choices surfaced in the Most-popular limit dropdown.
    /// 50 is the upper cap; anything higher trends toward "show all"
    /// without the explicit toggle.
    static let mostPopularLimitOptions: [Int] = [10, 15, 25, 50]

    // C3 / C4 / C5 picker filter state. Defaults match the F2a v1.3.22
    // behavior: no parameter cap, no capability requirement, popular
    // sort. Each toggle/dropdown in the picker writes into one of
    // these @Published fields; SwiftUI reactivity recomputes
    // visibleStarterModels and starterRowCountCaption.
    @Published var maxParametersBillion: Double? = nil
    @Published var requiredCapabilities: Set<String> = []
    @Published var sortMode: PickerSortMode = .popular

    // C27 Stage 1: catalog source. `.ollama` covers the bundled
    // catalog + live ollama.com scrape (via the existing discover-/
    // refresh-catalog arms); `.huggingFace` routes through the new
    // discover-hf-catalog / search-hf arms. Filter state survives a
    // source switch — only the underlying catalog rows are replaced.
    @Published var activeSource: ModelSourceKind = .ollama

    // C27 Stage 3: optional Hugging Face access token. Entered inline
    // when activeSource == .huggingFace via a SecureField. Threaded
    // through the IPC envelope (search-hf / discover-hf-catalog /
    // pull-model arms accept an optional "token" field) so the sidecar
    // pushes it into its HuggingFaceCatalogService before the request.
    // Persisted to portable-config.huggingFaceToken at finalize time —
    // sealed under AES-256-GCM when enableEncryption is true.
    @Published var huggingFaceToken: String = ""

    // C27 Stage 4: parent repoIds the user has chosen to expand. Drives
    // both the picker's chevron state and the interleaved-child layout
    // in `visibleStarterModels`. Mirrors the C# `_expandedRepos`.
    @Published var expandedRepoIds: Set<String> = []

    // C27 Stage 4: cache of fetched per-quant children keyed by parent
    // repoId. Re-expand replays the cache without touching the sidecar.
    @Published var huggingFaceQuantChildren: [String: [StarterModelDisplayEntry]] = [:]

    // C27 Stage 4: parent repoIds currently fetching from the sidecar.
    // Drives a "Loading…" affordance on the chevron + guards a
    // double-click from spawning a second `hf-siblings` arm.
    @Published var huggingFaceExpansionInFlight: Set<String> = []

    // C27 Stage 1: debounce token for the HF search box. When
    // activeSource == .huggingFace and modelSearchText changes, we
    // schedule a Task to fire search-hf after 350ms. Cancelling the
    // task aborts the previous schedule when the user keeps typing.
    private var hfSearchDebounceTask: Task<Void, Never>?
    private static let hfSearchDebounceNs: UInt64 = 350_000_000

    /// C4: the four capability chips surfaced in the picker. Lowercase
    /// matches the scraped `x-test-capability` vocabulary.
    static let capabilityTools: String = "tools"
    static let capabilityVision: String = "vision"
    static let capabilityThinking: String = "thinking"
    static let capabilityAudio: String = "audio"

    /// C4: SwiftUI binding helper — toggle a capability chip.
    func toggleCapability(_ capability: String) {
        let key = capability.lowercased()
        if requiredCapabilities.contains(key) {
            requiredCapabilities.remove(key)
        } else {
            requiredCapabilities.insert(key)
        }
    }

    // Readiness
    @Published var readinessItems: [ReadinessRow] = []

    // MAC31: pull UX state. canCancelPull gates the Cancel button on
    // the model-pull step view; pullProgressLine receives sidecar
    // `progress: ...` ticks so the picker shows a single in-place
    // status string rather than scrolling Ollama's TUI rewrite spam.
    @Published var canCancelPull: Bool = false
    @Published var pullProgressLine: String = ""

    // Services (deliberately non-@Published — these are stable references)
    private let driveService = DiskutilDriveService()
    private let hostController = PrepHostController()
    private let encryptedConfigWriter = EncryptedConfigWriter()
    private let plaintextConfigWriter = PlaintextConfigWriter()

    // MAC31: held for the duration of the pull batch so cancelPull()
    // can interrupt the for-loop in pullStarterModels(). Cancelling
    // the Task unblocks any in-flight hostController.send via the
    // withTaskCancellationHandler in awaitCommandResult; we *also*
    // dispatch a `cancel-pull` to the sidecar so the underlying
    // ollama process gets killed (without that, the daemon would
    // keep downloading even though Swift stopped awaiting).
    private var activePullTask: Task<Void, Never>?

    // MAC31a: ordered tags still to pull when the batch is cancelled
    // mid-flight. The first element is the tag that was in flight at
    // the moment of cancel — `resumePull()` re-enters the pull loop
    // with this list so MAC31's resume seed shows "Resuming `<tag>`
    // from NN%…" for partial blobs already on disk.
    private var pendingPullTags: [String] = []

    init() {
        // Default starter selection is set after discoverCatalog() runs
        // (the bundled list is loaded via the sidecar once it spawns,
        // not from a hardcoded array — F2).
        hostController.onLogLine = { [weak self] line in
            Task { @MainActor in self?.appendLog(line) }
        }
        // MAC31: route the sidecar's pull-progress channel to the
        // single in-place label. The closure already hops to main via
        // PrepHostController, so we just assign it.
        hostController.onPullProgress = { [weak self] line in
            Task { @MainActor in self?.pullProgressLine = line }
        }
    }

    var ssdRoot: URL? {
        guard let mount = selectedCandidate?.mountPoint else { return nil }
        return mount
    }

    /// C27 Stage 4: starter catalog with per-quant child rows interleaved
    /// directly below each expanded parent. The picker renders this list
    /// rather than `starterCatalog` so the chevron toggle produces a
    /// visually-nested hierarchy without the sidecar emitting children
    /// upfront. Pre-expansion + Ollama-source listings flow through
    /// unchanged.
    var availableStarterModels: [StarterModelDisplayEntry] {
        guard !expandedRepoIds.isEmpty else { return starterCatalog }
        var output: [StarterModelDisplayEntry] = []
        for parent in starterCatalog {
            output.append(parent)
            // The parent's tag is "hf.co/owner/repo" — strip the prefix to
            // get the repoId we keyed the children under.
            let repoId = stripHuggingFacePrefix(parent.tag)
            if parent.isExpandable && expandedRepoIds.contains(repoId),
               let children = huggingFaceQuantChildren[repoId] {
                output.append(contentsOf: children)
            }
        }
        return output
    }

    /// M11: caption that announces the picker's visible row count + cap
    /// reason. Empty when no filter or search is active — `catalogStatusText`
    /// already reports the total in that case. See
    /// `formatStarterRowCountCaption` for the full branch table.
    var starterRowCountCaption: String {
        let trimmed = modelSearchText.trimmingCharacters(in: .whitespacesAndNewlines)
        return formatStarterRowCountCaption(
            visible: visibleStarterModels.count,
            total: starterCatalog.count,
            showOnlyMostPopular: showOnlyMostPopular,
            hasSearch: !trimmed.isEmpty,
            maxParametersBillion: maxParametersBillion,
            requiredCapabilities: requiredCapabilities,
            sortMode: sortMode)
    }

    /// F2a + C3/C4/C5: the picker renders this list rather than
    /// `starterCatalog` directly. Pure logic lives in
    /// `applyStarterModelFilters` so the test binary can cover the
    /// filter composition order without spinning up a view-model.
    var visibleStarterModels: [StarterModelDisplayEntry] {
        // C27 Stage 4: filter the interleaved-with-children list rather
        // than the bare catalog so per-quant rows appear in the picker
        // when their parent is expanded. Filters skip child rows when
        // the parent isn't expanded by virtue of `availableStarterModels`
        // omitting them in that case.
        applyStarterModelFilters(
            to: availableStarterModels,
            search: modelSearchText,
            showOnlyMostPopular: showOnlyMostPopular,
            popularLimit: mostPopularLimit,
            maxParametersBillion: maxParametersBillion,
            requiredCapabilities: requiredCapabilities,
            sortMode: sortMode)
    }

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

    /// C27 Stage 1: invoked by the SwiftUI Source picker's onChange.
    /// Clears the catalog state and dispatches the appropriate fetch.
    /// Mirrors WPF's OnActiveSourceChanged. The HF refresh path uses
    /// the same debounce-friendly entry point as a search-box change,
    /// just with an empty search (= popular GGUF default page).
    func handleSourceSwitch() async {
        // Drop the prior source's selections — an Ollama-selected
        // `llama3.2:3b` makes no sense under HF, and vice versa.
        selectedStarterModels.removeAll()
        starterCatalog = []
        hfSearchDebounceTask?.cancel()
        hfSearchDebounceTask = nil
        switch activeSource {
        case .huggingFace:
            await refreshHuggingFaceCatalog(search: nil)
        case .ollama:
            // Re-load the bundled list synchronously; the user can
            // click Refresh to hit ollama.com again.
            await discoverCatalog()
        }
    }

    /// C27 Stage 1: schedule (or reschedule) the debounced HF search.
    /// Called by the SwiftUI onChange of `modelSearchText` when the
    /// active source is HF; under Ollama the search is purely local
    /// so no debounce / network roundtrip is required.
    func scheduleHuggingFaceSearch(for text: String) {
        hfSearchDebounceTask?.cancel()
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let needle = trimmed.isEmpty ? nil : trimmed
        hfSearchDebounceTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: PrepViewModel.hfSearchDebounceNs)
            if Task.isCancelled { return }
            await self?.refreshHuggingFaceCatalog(search: needle)
        }
    }

    /// C27 Stage 4: toggle expansion of a Hugging Face repo row. On
    /// first expand, calls the sidecar `hf-siblings` arm and decodes
    /// the per-quant child rows into `huggingFaceQuantChildren`.
    /// Subsequent toggles flip `expandedRepoIds` and rely on the cache.
    /// Sidecar failures log + leave the row collapsed; the user can
    /// retry by clicking the chevron again.
    func toggleRepoExpansion(parent: StarterModelDisplayEntry) async {
        guard parent.isExpandable else { return }
        let repoId = stripHuggingFacePrefix(parent.tag)
        if repoId.isEmpty { return }

        if expandedRepoIds.contains(repoId) {
            expandedRepoIds.remove(repoId)
            return
        }

        if huggingFaceQuantChildren[repoId] != nil {
            expandedRepoIds.insert(repoId)
            return
        }

        if huggingFaceExpansionInFlight.contains(repoId) { return }
        huggingFaceExpansionInFlight.insert(repoId)
        defer { huggingFaceExpansionInFlight.remove(repoId) }

        do {
            // Pass the bare repoId as the payload — `hf-siblings` parses
            // it directly (no JSON wrapping needed).
            let result = try await hostController.send("hf-siblings \(repoId)", timeout: 30)
            let ok = result.payload["ok"] as? Bool ?? false
            if !ok {
                let reason = result.payload["reason"] as? String ?? "unknown"
                let errMsg = result.payload["error"] as? String ?? ""
                appendLog("Could not expand hf.co/\(repoId): \(reason)\(errMsg.isEmpty ? "" : " (\(errMsg))")")
                return
            }
            // Gated / private surfaces with a reason + empty quants array.
            if let reason = result.payload["reason"] as? String,
               reason.hasPrefix("hf-") {
                appendLog("hf.co/\(repoId) is \(reason.dropFirst(3)). Enter your Hugging Face token to access the per-quant list.")
                // Insert an empty children entry so re-expand doesn't re-fetch.
                huggingFaceQuantChildren[repoId] = []
                expandedRepoIds.insert(repoId)
                return
            }
            let quants = (result.payload["quants"] as? [[String: Any]]) ?? []
            let children = quants.compactMap { decodeQuantChild(parent: parent, repoId: repoId, payload: $0) }
            huggingFaceQuantChildren[repoId] = children
            expandedRepoIds.insert(repoId)
        } catch {
            appendLog("Could not expand hf.co/\(repoId): \(error.localizedDescription)")
        }
    }

    /// C27 Stage 4: decode a single quant payload from `hf-siblings`
    /// into a display entry. Inherits the parent's pullCount / lastUpdated
    /// so the child sorts adjacent to the parent under the picker's
    /// existing sort modes; capabilities stay empty (HF doesn't surface
    /// them, and the C25 pass-through marker handles it).
    private func decodeQuantChild(
        parent: StarterModelDisplayEntry,
        repoId: String,
        payload: [String: Any]
    ) -> StarterModelDisplayEntry? {
        guard let tag = payload["tag"] as? String,
              let quantLabel = payload["quantLabel"] as? String
        else { return nil }
        let sizeBytes = (payload["quantSizeBytes"] as? Int64)
            ?? (payload["quantSizeBytes"] as? Int).map(Int64.init)
        let partCount = (payload["partCount"] as? Int) ?? 1
        let bestAt = partCount > 1 ? "\(quantLabel) (\(partCount)-part split)" : quantLabel
        return StarterModelDisplayEntry(
            tag: tag,
            sizeTier: "Custom",
            bestAt: bestAt,
            pullCount: parent.pullCount,
            capabilities: [],
            parametersBillion: nil,
            lastUpdated: parent.lastUpdated,
            sourceKind: .huggingFace,
            isExpandable: false,
            parentRepoId: repoId,
            quantLabel: quantLabel,
            quantSizeBytes: sizeBytes)
    }

    /// C27 Stage 4: mirror the C# `StripHuggingFacePrefix`. Returns the
    /// input unchanged when the prefix is absent.
    func stripHuggingFacePrefix(_ tag: String) -> String {
        let prefix = "hf.co/"
        return tag.lowercased().hasPrefix(prefix)
            ? String(tag.dropFirst(prefix.count))
            : tag
    }

    /// C27 Stage 3: push the current HF token to the sidecar so its
    /// catalog service attaches the Bearer header on subsequent
    /// search-hf / discover-hf-catalog / pull-model arms. Token is
    /// trimmed before send; empty input clears the token on the
    /// sidecar (anonymous mode). Failures are logged but non-fatal —
    /// the user's next attempt to hit a gated repo will surface the
    /// underlying 401/403 naturally.
    func pushHuggingFaceTokenToSidecar(_ token: String) async {
        let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        // Defense-in-depth: never echo the token into the log.
        let escaped = trimmed
            .replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
        let command = trimmed.isEmpty
            ? "set-hf-token {}"
            : "set-hf-token {\"token\":\"\(escaped)\"}"
        do {
            _ = try await hostController.send(command, timeout: 5)
        } catch {
            appendLog("Could not push HF token to sidecar: \(error.localizedDescription)")
        }
    }

    /// C27 Stage 1: HF Search via the sidecar's `discover-hf-catalog`
    /// (empty search = popular GGUF) or `search-hf` (user-typed query).
    /// Soft-failure mirrors `refreshCatalog`: the existing catalog
    /// stays in place if anything goes wrong.
    func refreshHuggingFaceCatalog(search: String?) async {
        if isRefreshingCatalog { return }
        isRefreshingCatalog = true
        defer { isRefreshingCatalog = false }

        do {
            let command: String
            if let needle = search, !needle.isEmpty {
                // search-hf payload: single-line JSON object so we
                // don't collide with the host's space-split parser.
                let escaped = needle
                    .replacingOccurrences(of: "\\", with: "\\\\")
                    .replacingOccurrences(of: "\"", with: "\\\"")
                command = "search-hf {\"search\":\"\(escaped)\"}"
            } else {
                command = "discover-hf-catalog"
            }
            let result = try await hostController.send(command, timeout: 30)
            let ok = result.payload["ok"] as? Bool ?? false
            if ok {
                let entries = decodeStarterEntries(from: result.payload)
                let display = entries.map(StarterModelDisplayEntry.from)
                starterCatalog = display
                let sourceUrl = result.payload["sourceUrl"] as? String ?? "huggingface.co"
                if display.isEmpty {
                    catalogStatusText = (search ?? "").isEmpty
                        ? "No GGUF repos returned by Hugging Face."
                        : "No GGUF repos match '\(search ?? "")'. Try a different search or clear it to see popular GGUF."
                } else {
                    catalogStatusText = (search ?? "").isEmpty
                        ? "Hugging Face: \(display.count) popular GGUF repos from \(sourceUrl)."
                        : "Hugging Face: \(display.count) GGUF repos matching '\(search ?? "")'."
                    appendLog("Refreshed HF catalog with \(display.count) entries.")
                }
                // Selection housekeeping mirrors refreshCatalog: drop
                // selections that fell out of the new result set.
                let liveTags = Set(display.map(\.tag))
                selectedStarterModels.formIntersection(liveTags)
            } else {
                let reason = result.payload["reason"] as? String ?? "unknown"
                let errorMsg = result.payload["error"] as? String ?? "(no detail)"
                let statusCode = result.payload["statusCode"] as? String
                catalogStatusText = (reason == "NonSuccessStatus" && statusCode == "429")
                    ? "Hugging Face is rate-limiting requests. Wait a minute and try again."
                    : "Hugging Face fetch failed (\(reason)): \(errorMsg). Switch back to Ollama to keep going."
                appendLog("HF catalog fetch failed: \(errorMsg)")
            }
        } catch {
            catalogStatusText = "Hugging Face fetch failed: \(error.localizedDescription)."
            appendLog("HF catalog fetch failed: \(error.localizedDescription)")
        }
    }

    func writeConfigAndProceed() async {
        guard let mount = selectedCandidate?.mountPoint else {
            currentStep = .failed(message: "No mount point.")
            return
        }
        isBusy = true
        defer { isBusy = false }

        // MAC30: branch on the encryption toggle. OFF (default) writes a
        // plaintext portable-config.json; ON keeps the MAC17a passphrase
        // flow.
        if enableEncryption {
            await writeEncryptedAndAdvance(mount: mount)
        } else {
            await writePlaintextAndAdvance(mount: mount)
        }
    }

    private func writeEncryptedAndAdvance(mount: URL) async {
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
        // C27 Stage 3: thread the inline HF token into the initial
        // encrypted config write. Empty input becomes nil so JSON omits
        // the field; PortableConfig defaults HuggingFaceToken to null.
        var payload = InitialPortableConfigPayload()
        let trimmedToken = self.huggingFaceToken.trimmingCharacters(in: .whitespacesAndNewlines)
        payload.huggingFaceToken = trimmedToken.isEmpty ? nil : trimmedToken
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

    private func writePlaintextAndAdvance(mount: URL) async {
        let writer = self.plaintextConfigWriter
        // C27 Stage 3: same token threading as the encrypted branch.
        // The plaintext warning banner already nudged the user toward
        // encryption; if they opted out, the token rides plaintext.
        var payload = InitialPortableConfigPayload()
        let trimmedToken = self.huggingFaceToken.trimmingCharacters(in: .whitespacesAndNewlines)
        payload.huggingFaceToken = trimmedToken.isEmpty ? nil : trimmedToken
        do {
            try await Task.detached(priority: .userInitiated) {
                try writer.writeInitialPlaintextConfig(
                    ssdRoot: mount, payload: payload)
            }.value
            appendLog("Plaintext config written (encryption skipped).")

            currentStep = .modelPull
            await pullStarterModels()
        } catch {
            currentStep = .failed(message: "Config write failed: \(error.localizedDescription)")
        }
    }

    private func pullStarterModels() async {
        if selectedStarterModels.isEmpty {
            appendLog("No starter models selected; skipping model pull.")
            pendingPullTags = []
            currentStep = .readiness
            await runReadiness()
            return
        }

        // MAC31a: seed pending list from the user's selection on first
        // entry. resumePull() re-enters with pendingPullTags already
        // populated (cancelled tag at index 0), so we only seed when
        // we're starting from scratch.
        if pendingPullTags.isEmpty {
            pendingPullTags = Array(selectedStarterModels)
        }
        await pullPendingTags()
    }

    /// MAC31a: pull whatever is currently in `pendingPullTags`, in
    /// order. On cancellation, transitions to `.modelPullPaused` with
    /// the still-pending list intact (cancelled tag remains at index 0
    /// so resumePull picks it up first). On clean completion, clears
    /// the list and advances to `.readiness`.
    private func pullPendingTags() async {
        isBusy = true
        defer { isBusy = false }
        canCancelPull = true
        defer { canCancelPull = false }

        // Snapshot the queue so we can compute the remaining list
        // when a specific tag is cancelled mid-pull. The for-loop
        // mutates a local copy via index so pendingPullTags only
        // updates on cancel/completion (cleaner state for tests).
        let queue = pendingPullTags
        var cancelledTag: String?
        var cancelledIndex: Int?

        let task = Task { @MainActor in
            for (index, tag) in queue.enumerated() {
                if Task.isCancelled {
                    cancelledTag = tag
                    cancelledIndex = index
                    break
                }
                self.pullProgressLine = "Pulling \(tag)…"
                self.appendLog("Pulling \(tag)…")
                do {
                    _ = try await self.hostController.send("pull-model \(tag)", timeout: 1800)
                    self.appendLog("Pulled \(tag).")
                } catch is CancellationError {
                    self.appendLog("Pull cancelled for \(tag).")
                    cancelledTag = tag
                    cancelledIndex = index
                    break
                } catch {
                    self.appendLog("Pull failed for \(tag): \(error.localizedDescription)")
                    self.appendLog("(This is non-fatal — you can pull models later from Mac Runner.)")
                }
            }
        }
        activePullTask = task
        await task.value
        activePullTask = nil

        if let tag = cancelledTag, let index = cancelledIndex {
            // Capture the last progress snapshot before clearing it so
            // the paused-step view can display "<tag> — NN% downloaded".
            // Per the 2026-05-08 design call, we use the in-memory
            // snapshot rather than re-querying the sidecar on cancel
            // (avoids a roundtrip while the user is already waiting).
            let snapshot = pullProgressLine.isEmpty ? nil : pullProgressLine
            pendingPullTags = Array(queue[index...])
            pullProgressLine = ""
            currentStep = .modelPullPaused(tag: tag, progressSnapshot: snapshot)
            return
        }

        pendingPullTags = []
        pullProgressLine = ""
        currentStep = .readiness
        await runReadiness()
    }

    /// MAC31a: re-enter the pull loop after cancellation. The first
    /// tag in `pendingPullTags` is the one that was cancelled, and
    /// MAC31's resume seed (in mac-prep-host's pull-model arm) emits
    /// "Resuming `<tag>` from NN%…" automatically when partial blobs
    /// are present on disk.
    func resumePull() async {
        guard !pendingPullTags.isEmpty else {
            currentStep = .readiness
            await runReadiness()
            return
        }
        currentStep = .modelPull
        await pullPendingTags()
    }

    /// MAC31a: skip the remaining pulls and advance to readiness. The
    /// already-downloaded models are still on disk (and in
    /// `selectedStarterModels`), so readiness will report them
    /// correctly via the MAC29 disk-truth path.
    func skipRemainingPulls() async {
        appendLog("Skipping remaining model pulls.")
        pendingPullTags = []
        currentStep = .readiness
        await runReadiness()
    }

    /// MAC31: cancel the active pull batch. Cancels the Swift Task so
    /// the for-loop's `hostController.send` await unblocks (raises
    /// CancellationError via awaitCommandResult's task-cancellation
    /// handler), AND dispatches a `cancel-pull` to the sidecar so the
    /// underlying `ollama pull` process tree is killed — otherwise the
    /// download would continue silently in the daemon even though
    /// Swift stopped awaiting it.
    ///
    /// Idempotent: safe to call when no pull is in flight; the sidecar
    /// arm is a no-op in that case.
    func cancelPull() {
        guard canCancelPull else { return }
        appendLog("Cancelling pull…")
        activePullTask?.cancel()
        let host = self.hostController
        Task {
            // Short timeout — cancel-pull just signals a CTS and
            // returns; long timeouts here would mask a hung sidecar.
            _ = try? await host.send("cancel-pull", timeout: 5)
        }
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

    /// MAC32: Done step's button calls this so prep cleanly exits after
    /// shutdown. Pre-MAC32 the button called only `finalize()`, which
    /// silently shut down the sidecar and appended a log line — the
    /// window stayed open with no visible signal that anything had
    /// happened, which the v1.3.10 mac field test reported as "Finish
    /// is broken." Quit makes the completion explicit.
    func quit() async {
        await finalize()
        #if canImport(AppKit)
        await MainActor.run { NSApplication.shared.terminate(nil) }
        #endif
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
        pendingPullTags = []
        pullProgressLine = ""
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
