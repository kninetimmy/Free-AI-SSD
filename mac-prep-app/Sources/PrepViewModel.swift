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
    @Published var selectedCandidate: DiskCandidate? {
        didSet {
            // C7: zero any cached unlock material BEFORE re-running
            // detection so a green "Unlocked" banner can never render
            // against a different drive's identity. Resetting the dialog
            // state too keeps a half-typed sheet from carrying over.
            resetManageModelsUnlockState()
            manageModelsPullFailureTags = []
            // C6 Stage 3: detection runs on every selection change so the
            // already-configured banner stays in sync. Detection is pure
            // file-presence (no decrypt) — safe even on encrypted drives.
            refreshDriveConfigurationState()
        }
    }
    @Published var prepareForWindowsToo: Bool = true   // default cross-platform
    @Published var volumeLabel: String = "FREEAI"

    /// Opt-in to staging the Tesseract OCR bundle during the staging step. Off
    /// by default — OCR is slow and adds ~10 MB, and most DCS guides are
    /// text-layer. When true, the staging block runs a `stage-tesseract` arm
    /// after the core arms (failure logs but does not block), and the written
    /// config sets `ocrEnabled = true` so OCR is usable without a second toggle
    /// hunt in the Runner. Mirrors the Windows PrepApp PDF-image-OCR expander.
    @Published var installOcr: Bool = false

    // C6 Stage 3: detection snapshot for the selected candidate's drive.
    // Updated by `refreshDriveConfigurationState()` whenever
    // `selectedCandidate` changes. Drives the contextual banner on
    // DriveSelectionStepView and gates the Manage-models / Start-over
    // affordances. Default `.empty` ≡ no drive selected.
    @Published var driveConfiguration: DriveConfigurationSnapshot = .empty

    // C6 Stage 3: disk-truth list of installed models on the configured
    // drive. Populated by `refreshInstalledModels()` via the sidecar's
    // `discover-models` command. Bound to the ManageModelsStepView list.
    @Published var installedModels: [String] = []

    // C6 Stage 3: marker that a pull was initiated from inside the
    // .manageModels step. Branches the `pullPendingTags` tail so the
    // user returns to .manageModels (refreshing installedModels)
    // instead of falling through to .readiness.
    @Published var isAddingModelInManagement: Bool = false
    @Published var manageModelsPullFailureTags: [String] = []

    // C7: passphrase-unlock state for Manage Models on an encrypted drive.
    // `isManageModelsUnlocked` is the gate signal SwiftUI observes; it
    // flips true only after `attemptUnlock` succeeds and is cleared on
    // drive change, Done, or app termination (UnlockMaterial.deinit
    // zeroes the key automatically when the @StateObject is dropped).
    // `unlockMaterial` is the cached PBKDF2 output used by re-encrypts;
    // intentionally not @Published so it never leaks through SwiftUI
    // diagnostic reflection.
    @Published var isManageModelsUnlocked: Bool = false
    @Published var unlockDialogPresented: Bool = false
    @Published var unlockDialogPassword: String = ""
    @Published var unlockDialogError: String? = nil
    @Published var isUnlocking: Bool = false
    private var unlockMaterial: UnlockMaterial?
    // C7: in-memory decrypted config kept alive while a session is
    // unlocked so re-encrypts (`commitHuggingFaceTokenIfNeeded`) preserve
    // unknown fields the Mac PrepApp doesn't model. Cleared via
    // `resetManageModelsUnlockState`. Never @Published — the dict shape
    // changes opaquely as the user types.
    private var unlockedConfig: [String: Any]?

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

    // #338 parity: up-front access-mode choice on the reframed encryption
    // step. LAN forces encryption on and locks the toggle; device-only leaves
    // encryption an optional toggle. Mutated only via selectDeviceOnlyAccess /
    // requestLanAccess (the latter runs an encryption-required confirm first),
    // never a bare setter — so the radio can't clear the precondition the LAN
    // API key storage depends on. Mirrors the Windows PrepViewModel flow.
    @Published private(set) var webUiAccessMode: WebUiAccessMode = .deviceOnly

    // #338 parity: the LAN API key generated + sealed during the successful
    // encrypted-config write. Surfaced on the Done step so a LAN user can copy
    // it onto each device. Nil for device-only / plaintext (the key is cleared
    // on that path). Mirrors the Windows `FinalizedNetworkApiKey`.
    @Published private(set) var finalizedNetworkApiKey: String?

    /// Convenience flags for the view (LAN-only explainer, toggle lock, Done
    /// gate). Delegate to the pure helpers in PrepFlowStep.swift so the rules
    /// have a single, test-covered source of truth.
    var isLanAccess: Bool { webUiAccessMode == .lan }
    var isDeviceOnlyAccess: Bool { webUiAccessMode == .deviceOnly }
    var isEncryptionToggleEnabled: Bool { accessEncryptionToggleEnabled(for: webUiAccessMode) }
    var showFinalizedApiKeyPanel: Bool {
        accessShowFinalizedApiKey(mode: webUiAccessMode, key: finalizedNetworkApiKey)
    }

    /// Select device-only access. Encryption reverts to a user-controlled
    /// optional toggle; the current `enableEncryption` value is preserved.
    func selectDeviceOnlyAccess() {
        let result = resolveAccessMode(
            selecting: .deviceOnly, lanConfirmed: false,
            currentEncryption: enableEncryption)
        webUiAccessMode = result.mode
        enableEncryption = result.enableEncryption
    }

    /// Request LAN access. Because LAN exposure stores a network API key that
    /// is only ever written encrypted, this first confirms (NSAlert) that the
    /// drive must be encrypted. On confirm the mode flips to `.lan` and
    /// encryption is forced on; on cancel nothing changes (the view snaps the
    /// radio back to device-only by re-reading `webUiAccessMode`).
    func requestLanAccess() {
        if webUiAccessMode == .lan { return }
        let confirmed = confirmLanEncryptionRequired()
        let result = resolveAccessMode(
            selecting: .lan, lanConfirmed: confirmed,
            currentEncryption: enableEncryption)
        webUiAccessMode = result.mode
        enableEncryption = result.enableEncryption
    }

    /// #338: encryption-required confirmation for LAN access. Routed through
    /// NSAlert per the Mac UI design language (PrepApp leans pure-native). Copy
    /// adapted from the Windows `RequestLanAccess`. Returns true on confirm.
    private func confirmLanEncryptionRequired() -> Bool {
#if canImport(AppKit)
        let alert = NSAlert()
        alert.messageText = "Encryption required for LAN access"
        alert.informativeText = """
        Letting other devices on your LAN reach the web chat UI stores a \
        network API key on the drive. That key is only ever written encrypted, \
        so the drive's configuration must be encrypted.

        You'll choose an unlock passphrase below, and Mac Runner will ask for \
        it each time it starts.
        """
        alert.alertStyle = .informational
        alert.addButton(withTitle: "Enable encryption & continue")
        alert.addButton(withTitle: "Cancel")
        return alert.runModal() == .alertFirstButtonReturn
#else
        // Headless/test builds have no AppKit. The access-mode rule itself is
        // unit-tested via resolveAccessMode; this path is never driven there.
        return true
#endif
    }

    /// #338: Done-step Copy button. Writes the finalized LAN API key to the
    /// system clipboard. No-op when there's no key.
    func copyApiKeyToClipboard() {
#if canImport(AppKit)
        guard let key = finalizedNetworkApiKey, !key.isEmpty else { return }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(key, forType: .string)
        appendLog("API key copied to clipboard.")
#endif
    }

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

    // MARK: - C6 Stage 3 — DriveConfiguration detection + Manage models

    /// Re-runs the detector against the current `selectedCandidate.mountPoint`
    /// and refreshes `driveConfiguration`. Fired from `selectedCandidate.didSet`
    /// on every selection change. Safe on missing mount points (detector
    /// returns `.empty`).
    func refreshDriveConfigurationState() {
        driveConfiguration = DriveConfigurationDetector.detect(selectedCandidate?.mountPoint)
    }

    /// C6: contextual banner is shown whenever the drive carries our
    /// marker (FullyConfigured OR ConfiguredEmpty).
    var showAlreadyConfiguredBanner: Bool {
        driveConfiguration.state != .unconfigured
    }

    var showManageModelsButton: Bool { showAlreadyConfiguredBanner }
    var showStartOverButton: Bool { showAlreadyConfiguredBanner }

    /// C6: drives `.disabled()` on the volume-label Form + Continue
    /// button in DriveSelectionStepView when the banner is visible.
    /// The destructive path lives inside the banner's Start-over button
    /// in that state, not in the inline fresh-format controls.
    var canInitiateFreshFormat: Bool { !showAlreadyConfiguredBanner }

    /// C6: Add/Remove on the new step are disabled on encrypted drives
    /// until C7's passphrase-unlock lands. C7 update: unlocked sessions
    /// flip both gates true. Unencrypted drives bypass the unlock check
    /// entirely (no banner, no sheet).
    var canManageModelsAdd: Bool {
        !driveConfiguration.isConfigEncrypted || isManageModelsUnlocked
    }
    var canManageModelsRemove: Bool {
        !driveConfiguration.isConfigEncrypted || isManageModelsUnlocked
    }

    /// C6: contextual banner text. FullyConfigured includes a model
    /// count; ConfiguredEmpty calls out the half-prepped state.
    var alreadyConfiguredBannerText: String {
        switch driveConfiguration.state {
        case .fullyConfigured:
            let n = driveConfiguration.modelManifestCount
            return n > 0
                ? "This SSD is already prepared. \(n) model\(n == 1 ? "" : "s") on this drive."
                : "This SSD is already prepared."
        case .configuredEmpty:
            return "This SSD is prepared but has no models yet."
        case .unconfigured:
            return ""
        }
    }

    /// C6: banner's "Manage models" tap entry point. Transitions to
    /// `.manageModels` and runs the light-touch sidecar startup
    /// (decision D9.b — sidecar + ensure-structure + discover-catalog,
    /// no re-staging). On failure routes to `.failed(message:)`.
    func enterManageModels() async {
        currentStep = .manageModels
        await runManageModelsStartup()
    }

    /// C6: banner's "Start over (formats drive)" tap entry point.
    /// Shows a `.warning` NSAlert pre-confirm framing the destructive
    /// nature of wiping an already-prepared SSD, then delegates to
    /// the existing `.eraseConfirmation` step which has its own
    /// `.critical` NSAlert before `formatSelected()` runs (decision D3).
    func startOverFromBanner() {
#if canImport(AppKit)
        guard let candidate = selectedCandidate else { return }
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Erase already-prepared SSD?"
        alert.informativeText = """
        This SSD is already prepared. Starting over will erase \
        \(candidate.identifier) (\(candidate.sizeDisplay)) and re-run \
        the full prep flow.

        All models, configuration, and documents on this drive will be lost.
        """
        alert.addButton(withTitle: "Continue")
        alert.addButton(withTitle: "Cancel")
        let response = alert.runModal()
        guard response == .alertFirstButtonReturn else {
            appendLog("Start over cancelled by user.")
            return
        }
        // Existing destructive gates fire next: .eraseConfirmation step
        // hosts its own `.critical` NSAlert before formatSelected runs.
        proceedToEraseConfirmation()
#endif
    }

    /// C6: light-touch startup for an already-configured drive. Skips
    /// the heavy `stage-runner` / `stage-ollama` / `stage-prereqs` arms
    /// — the staged binaries are already on disk. Still calls
    /// `ensure-structure` (cheap insurance against a manually-deleted
    /// subdirectory) and `discoverCatalog` (so the Add picker has rows
    /// to show). Mirrors `runStaging()` but truncated.
    private func runManageModelsStartup() async {
        guard let mount = selectedCandidate?.mountPoint else {
            currentStep = .failed(message: "Selected drive has no mount point.")
            return
        }
        isBusy = true
        defer { isBusy = false }

        do {
            try FileManager.default.createDirectory(
                at: mount.appendingPathComponent("logs"),
                withIntermediateDirectories: true)
        } catch {
            currentStep = .failed(message: "Failed to create logs directory: \(error.localizedDescription)")
            return
        }

        do {
            try await hostController.startAndWaitReady(ssdRoot: mount)
        } catch {
            currentStep = .failed(message: "Sidecar startup failed: \(error.localizedDescription)")
            return
        }

        do {
            _ = try await hostController.send("ensure-structure")
            appendLog("SSD layout verified.")
        } catch {
            currentStep = .failed(message: "Failed to verify SSD layout: \(error.localizedDescription)")
            return
        }

        // Catalog discovery feeds the Add disclosure's picker. Soft
        // failure: if it fails the picker stays empty; the user can
        // still use what's already installed and the Remove path.
        await discoverCatalog()

        // Disk-truth installed list for the primary signal in the view.
        await refreshInstalledModels()
    }

    /// C6: pulls the canonical installed-models list from the sidecar's
    /// `discover-models` command (delegates to
    /// `ModelOperations.DiscoverModelsOnDisk` in prep-core — single
    /// source of truth across OSes). Sorted case-insensitive to match
    /// the C# DiscoverInstalledModels projection.
    func refreshInstalledModels() async {
        do {
            let result = try await hostController.send("discover-models")
            // Sidecar payload shape: {"models": ["name:tag", …]} per
            // HostLifetime.cs:151-153.
            if let models = result.payload["models"] as? [String] {
                installedModels = models.sorted { $0.lowercased() < $1.lowercased() }
            } else {
                installedModels = []
            }
        } catch {
            appendLog("discover-models failed: \(error.localizedDescription)")
            installedModels = []
        }
    }

    /// C6: per-row Remove button handler. Shows a `.warning` NSAlert
    /// pre-confirm, then routes to the sidecar's `remove-model` arm
    /// (added in Sub-stage 3.3). Refreshes `installedModels` on
    /// success so the row drops off the list. Gated by
    /// `canManageModelsRemove` (false on encrypted drives, per D13).
    func removeModel(tag: String) async {
        guard canManageModelsRemove else {
            appendLog("Remove blocked: drive is encrypted and not yet unlocked.")
            return
        }
#if canImport(AppKit)
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Remove \(tag) from this drive?"
        alert.informativeText = """
        The model blobs will be deleted from \
        \(selectedCandidate?.mountPoint?.lastPathComponent ?? "this SSD"). \
        You can pull it again later from Manage models.
        """
        alert.addButton(withTitle: "Remove")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else {
            appendLog("Remove cancelled for \(tag).")
            return
        }
#endif
        isBusy = true
        defer { isBusy = false }
        do {
            _ = try await hostController.send("remove-model \(tag)", timeout: 120)
            appendLog("Removed \(tag).")
            await refreshInstalledModels()
        } catch {
            appendLog("Remove failed for \(tag): \(error.localizedDescription)")
        }
    }

    /// C6: "Pull selected" button handler on the Add disclosure. Sets
    /// the `isAddingModelInManagement` flag so `pullPendingTags`'s tail
    /// returns to `.manageModels` instead of `.readiness`, then routes
    /// through the existing `.modelPull` view for in-place progress.
    func pullSelectedFromManagement() async {
        guard canManageModelsAdd else {
            appendLog("Add blocked: drive is encrypted and not yet unlocked.")
            return
        }
        guard !selectedStarterModels.isEmpty else { return }
        manageModelsPullFailureTags = []
        pendingPullTags = []
        isAddingModelInManagement = true
        currentStep = .modelPull
        await pullStarterModels()
    }

    /// C6: "Done" button handler on ManageModelsStepView. Tears down
    /// the sidecar so it isn't left running idle while the user is
    /// back on the drive list — re-entering will spin it up again via
    /// `runManageModelsStartup()`. Returns to `.driveSelection` with
    /// the banner still visible.
    func exitManageModels() async {
        // C7: commit any pending HF-token edits BEFORE zeroizing — Done
        // is the user's intent-to-save boundary. No-op if the drive is
        // unencrypted or the token didn't change. Persisting per-keystroke
        // would thrash exFAT over USB; per-pull (in pullPendingTags tail)
        // plus per-Done is enough to make the token re-entry experience
        // good without write amplification.
        await commitHuggingFaceTokenIfNeeded()
        // Now zero the cached unlock material. UnlockMaterial.deinit also
        // zeroes on @StateObject drop (app exit), but exiting the step is
        // the normal "I'm done" boundary.
        resetManageModelsUnlockState()
        await hostController.shutdown()
        installedModels = []
        selectedStarterModels = []
        isAddingModelInManagement = false
        manageModelsPullFailureTags = []
        currentStep = .driveSelection
    }

    // MARK: - C7 — encrypted-drive Manage Models unlock

    /// C7: banner "Unlock" button action. Resets any prior sheet state
    /// (typed-but-not-submitted password from a previous open) and shows
    /// the modal SecureField sheet.
    func presentUnlockSheet() {
        unlockDialogPassword = ""
        unlockDialogError = nil
        unlockDialogPresented = true
    }

    /// C7: sheet's Cancel button action. Mirrors the Runner pattern of
    /// clearing transient state but never touching the cached material —
    /// cancelling an unlock attempt on a re-lock-already path must NOT
    /// invalidate a prior successful unlock.
    func cancelUnlock() {
        unlockDialogPresented = false
        unlockDialogPassword = ""
        unlockDialogError = nil
    }

    /// C7: sheet's "Unlock" button action. Synchronous mirror of
    /// `RunnerViewModel.attemptUnlock` (main.swift:267-310). On success:
    /// caches `UnlockMaterial`, lifts `huggingFaceToken` from the
    /// decrypted config, flips the gate, dismisses the sheet, and runs
    /// `tryMigratePlaintext` so a stale plaintext config can never
    /// accumulate alongside the encrypted blob.
    func attemptUnlock(password: String) {
        guard let mount = selectedCandidate?.mountPoint else {
            unlockDialogError = "No drive selected."
            return
        }
        isUnlocking = true
        defer { isUnlocking = false }

        let result = SsdEncryption.tryUnlockPortableConfig(ssdRoot: mount, password: password)
        switch result {
        case .failure(let err):
            unlockDialogError = err.errorDescription ?? "Unlock failed."
            appendLog("Unlock failed: \(err.errorDescription ?? "unknown")")
        case .success(let unlocked):
            // Zero any prior material before swapping in the new one so a
            // re-unlock on the same drive doesn't leak the previous key.
            unlockMaterial?.zeroize()
            unlockMaterial = unlocked.material
            unlockedConfig = unlocked.config

            // Lift huggingFaceToken from the decrypted config — closes the
            // post-finalize re-entry gap noted in the C7 plan. Finalize
            // already writes the token; this is the read path back.
            if let token = unlocked.config["huggingFaceToken"] as? String,
               !token.isEmpty {
                huggingFaceToken = token
            }

            isManageModelsUnlocked = true
            unlockDialogPresented = false
            unlockDialogPassword = ""
            unlockDialogError = nil
            appendLog("SSD unlocked for this session.")

            // Mirror Runner (main.swift:293-299): absorb-or-discard any
            // stale plaintext config so the drive never accumulates
            // plaintext secrets. Branch A (plaintext newer) re-sets the
            // HF token AND the cached config from the merged dict in case
            // the user edited it pre-encryption-fix.
            let migration = SsdEncryption.tryMigratePlaintext(
                ssdRoot: mount, material: unlocked.material,
                log: { [weak self] line in self?.appendLog(line) })
            if case .mergedFromPlaintext(let merged) = migration {
                unlockedConfig = merged
                if let token = merged["huggingFaceToken"] as? String,
                   !token.isEmpty {
                    huggingFaceToken = token
                }
            }
        }
    }

    /// C7: zero the cached UnlockMaterial and clear every unlock UI flag.
    /// Called from `selectedCandidate.didSet` (drive change) and
    /// `exitManageModels` (Done click). The UnlockMaterial.deinit also
    /// zeroes on object deallocation; this is the explicit-boundary path.
    func resetManageModelsUnlockState() {
        unlockMaterial?.zeroize()
        unlockMaterial = nil
        unlockedConfig = nil
        isManageModelsUnlocked = false
        unlockDialogPresented = false
        unlockDialogPassword = ""
        unlockDialogError = nil
        isUnlocking = false
    }

    /// C7: persist the current `huggingFaceToken` value back to the
    /// encrypted config using the cached `UnlockMaterial`. No-op when the
    /// drive is unencrypted or no session is unlocked. Triggered from the
    /// SecureField's `.onSubmit` / focus-loss and from
    /// `pullPendingTags`'s tail after a successful HF pull — NOT on every
    /// keystroke (per the plan's commit-not-keystroke decision; exFAT
    /// over USB can't sustain per-character AES-GCM + two-file commits).
    func commitHuggingFaceTokenIfNeeded() async {
        guard let mount = selectedCandidate?.mountPoint else { return }
        guard let material = unlockMaterial else { return }
        guard driveConfiguration.isConfigEncrypted else { return }
        // No-op fast path: token unchanged.
        if let prior = unlockedConfig?["huggingFaceToken"] as? String,
           prior == huggingFaceToken {
            return
        }

        // Use the in-memory decrypted config as the round-trip base so
        // unknown fields the Mac PrepApp doesn't model (e.g.
        // networkApiBindAddress) survive the re-seal. Fall back to a
        // single-key dict only if no unlock-time snapshot exists (should
        // not happen — `attemptUnlock` always populates it on success).
        var current = unlockedConfig ?? [:]
        current["huggingFaceToken"] = huggingFaceToken

        do {
            try SsdEncryption.saveEncryptedConfig(
                ssdRoot: mount, config: current, material: material)
            unlockedConfig = current
            appendLog("Hugging Face token re-encrypted to portable config.")
        } catch {
            appendLog("Failed to persist Hugging Face token: \(error.localizedDescription)")
        }
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

            // Optional bundles (Tesseract OCR). Fail-soft: the sidecar catches
            // its own exceptions and returns ok=false, so a bundle download
            // failure lands in the log without blocking finalize — hence `try?`
            // and no per-arm do/catch. The command list is a pure, parity-pinned
            // helper in PrepFlowStep.
            for command in optionalStagingCommands(installOcr: installOcr) {
                _ = try? await hostController.send(command)
            }

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
        // M19: route through CommandPayloadEncoder so control characters
        // (newlines, tabs, …) in pasted tokens emit a valid JSON escape
        // sequence rather than passing through and tripping the sidecar's
        // JsonDocument.Parse. Defense-in-depth: we still never echo the
        // token value into the log.
        let command: String
        if trimmed.isEmpty {
            command = "set-hf-token {}"
        } else {
            guard let payload = CommandPayloadEncoder.encode(["token": trimmed]) else {
                appendLog("Could not encode set-hf-token payload.")
                return
            }
            command = "set-hf-token \(payload)"
        }
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
                // M19: route through CommandPayloadEncoder so control
                // characters in user-typed searches emit valid JSON
                // escape sequences. Single-line output keeps the
                // host's space-split parser happy.
                guard let payload = CommandPayloadEncoder.encode(["search": needle]) else {
                    catalogStatusText = "Hugging Face fetch failed: could not encode search payload."
                    appendLog("Could not encode search-hf payload.")
                    return
                }
                command = "search-hf \(payload)"
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

        // 2026-05-12 field test: HF pulls (even of public GGUFs) fail
        // without a Bearer token in Ollama's `HF_TOKEN` env — HF's
        // public-repo rate-limit refuses the unauth'd request and the
        // pull errors instantly. Block the Continue press if any HF
        // tag is selected and the inline token field is empty so the
        // user understands what's required before we hit `.modelPull`
        // and the readiness screen shows a red "≥1 installed model".
        if huggingFaceSelectionNeedsToken() {
            presentHuggingFaceTokenRequired()
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

    /// True when the user has selected any `hf.co/...` tag (parent
    /// or quant child) but left the inline token field empty.
    func huggingFaceSelectionNeedsToken() -> Bool {
        let trimmed = huggingFaceToken.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.isEmpty else { return false }
        return selectedStarterModels.contains { $0.hasPrefix("hf.co/") }
    }

    /// Explainer modal — fired when the user clicks Continue with HF
    /// tags selected but no token. Walks them through where to get a
    /// free read-only token. Opens the HF token settings page in the
    /// default browser when they pick "Open Hugging Face".
    private func presentHuggingFaceTokenRequired() {
#if canImport(AppKit)
        let alert = NSAlert()
        alert.messageText = "Hugging Face token required"
        alert.informativeText = """
        You picked one or more Hugging Face models. Ollama needs a free \
        read-only token from huggingface.co to pull them — even for \
        public GGUFs (HF rate-limits anonymous downloads).

        1. Sign in or sign up at huggingface.co (free).
        2. Open Settings → Access Tokens, click "Create new token".
        3. Choose "Read" (classic) — or "Fine-grained" with only \
        "Read access to contents of all public repos" checked.
        4. Copy the token (starts with `hf_…`) and paste it into the \
        "HF token" field on this page.
        5. Click Continue again.

        The token is stored on the SSD (sealed with AES-256-GCM when \
        encryption is on; plaintext otherwise — a yellow warning surfaces \
        on this page in that case).
        """
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Open Hugging Face")
        alert.addButton(withTitle: "Cancel")

        let response = alert.runModal()
        if response == .alertFirstButtonReturn,
           let url = URL(string: "https://huggingface.co/settings/tokens") {
            NSWorkspace.shared.open(url)
        }
#endif
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
        // Build via an immediate closure so the captured `payload` is a
        // `let` — Swift's strict-concurrency check on Task.detached
        // refuses to capture a mutated var.
        let payload: InitialPortableConfigPayload = {
            var p = InitialPortableConfigPayload()
            let trimmed = self.huggingFaceToken.trimmingCharacters(in: .whitespacesAndNewlines)
            p.huggingFaceToken = trimmed.isEmpty ? nil : trimmed
            // When OCR was opted into at staging, enable it in the written config
            // so it works in the Runner without a second toggle.
            p.ocrEnabled = self.installOcr
            return p
        }()
        do {
            try await Task.detached(priority: .userInitiated) {
                try writer.writeInitialEncryptedConfig(
                    ssdRoot: mount, payload: payload, passphrase: pass)
            }.value
            appendLog("Encrypted config written.")

            // #338 parity: capture the generated key so the Done step can
            // surface + copy it when LAN access was chosen. The key was sealed
            // into the encrypted config above; it is only ever displayed when
            // showFinalizedApiKeyPanel (LAN-gated) is true, mirroring the
            // Windows FinalizedNetworkApiKey capture-on-every-encrypted-success.
            finalizedNetworkApiKey = payload.networkApiKey

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
        // Closure-init keeps the captured `payload` immutable for
        // Swift's strict-concurrency check on Task.detached.
        let payload: InitialPortableConfigPayload = {
            var p = InitialPortableConfigPayload()
            let trimmed = self.huggingFaceToken.trimmingCharacters(in: .whitespacesAndNewlines)
            p.huggingFaceToken = trimmed.isEmpty ? nil : trimmed
            // When OCR was opted into at staging, enable it in the written config
            // so it works in the Runner without a second toggle.
            p.ocrEnabled = self.installOcr
            return p
        }()
        do {
            try await Task.detached(priority: .userInitiated) {
                try writer.writeInitialPlaintextConfig(
                    ssdRoot: mount, payload: payload)
            }.value
            appendLog("Plaintext config written (encryption skipped).")

            // #338 parity: no key on the plaintext path — PlaintextConfigWriter
            // clears networkApiKey on disk, so there's nothing to surface.
            finalizedNetworkApiKey = nil

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
        var failedTags: [String] = []

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
                    failedTags.append(tag)
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

        pullProgressLine = ""

        if !failedTags.isEmpty {
            pendingPullTags = failedTags

            if isAddingModelInManagement {
                isAddingModelInManagement = false
                manageModelsPullFailureTags = failedTags
                currentStep = .manageModels
                await refreshInstalledModels()
                return
            }

            currentStep = .modelPullFailed(tags: failedTags)
            return
        }

        pendingPullTags = []
        manageModelsPullFailureTags = []

        // C6 Stage 3: when the pull was initiated from inside
        // .manageModels, return there (refreshing installedModels so the
        // new model surfaces in the list) instead of falling through to
        // .readiness — the user is mid-management, not mid-prep.
        if isAddingModelInManagement {
            isAddingModelInManagement = false
            currentStep = .manageModels
            await refreshInstalledModels()
            // C7: a successful HF pull is a natural commit point for the
            // token (the user just proved it works). No-ops when the
            // drive is unencrypted or the token didn't change.
            await commitHuggingFaceTokenIfNeeded()
            return
        }

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

    /// M15: retry only the tags that failed in the last pull batch.
    /// Used from the initial prep failure step.
    func retryFailedPulls() async {
        guard !pendingPullTags.isEmpty else {
            currentStep = .readiness
            await runReadiness()
            return
        }
        currentStep = .modelPull
        await pullPendingTags()
    }

    /// M15: retry failed tags from Manage Models, then return to
    /// `.manageModels` on success rather than advancing to readiness.
    func retryFailedPullsFromManagement() async {
        guard !pendingPullTags.isEmpty else { return }
        manageModelsPullFailureTags = []
        isAddingModelInManagement = true
        currentStep = .modelPull
        await pullPendingTags()
    }

    /// M15: keep pull failures non-fatal. The user has seen the inline
    /// error and can choose to finish prep with whatever did land.
    func continueAfterPullFailures() async {
        if !pendingPullTags.isEmpty {
            appendLog("Continuing to readiness with \(pendingPullTags.count) failed model pull(s).")
        }
        pendingPullTags = []
        pullProgressLine = ""
        currentStep = .readiness
        await runReadiness()
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
        webUiAccessMode = .deviceOnly
        finalizedNetworkApiKey = nil
        pendingPullTags = []
        pullProgressLine = ""
        manageModelsPullFailureTags = []
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
