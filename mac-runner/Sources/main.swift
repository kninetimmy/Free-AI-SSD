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

/// MAC38: trust anchor for the Mac Ollama runtime is now the on-SSD
/// attestation file, not a hardcoded URL+SHA pair. PrepApp staging only
/// writes the attestation after verifying the bundled archive against the
/// upstream release's vendor-published `sha256sum.txt`. At launch we
/// re-validate the attestation's URL is HTTPS to an allowlisted github.com
/// host and that the recorded SHA-256 is well-formed. Re-hashing the
/// 180MB+ binary on every launch is too slow; we trust the attestation as
/// the staging gate's signed receipt.
enum MacOllamaTrust {
    static let attestationFileName = "ollama-package-trust.json"
    static let allowlistedHosts: Set<String> = ["github.com", "objects.githubusercontent.com"]
}

enum TrustGateResult {
    case allowed
    case refused(String)
}

/// MAC8: Mac-side mirror of `RunnerLocalApiService.LibrarySummary`.
struct LibrarySummary: Codable, Hashable {
    let id: String
    let name: String
}

/// MAC8: Mac-side mirror of `RunnerLocalApiService.LibraryFileSummary`.
struct LibraryFileSummary: Codable, Hashable {
    let fileName: String
    let storedRelativePath: String
    let sizeBytes: Int
    let importedAtUtc: String
}

/// MAC8: Mac-side mirror of `RunnerLocalApiService.LibraryDetail`.
struct LibraryDetail: Codable {
    let id: String
    let name: String
    let fileCount: Int
    let files: [LibraryFileSummary]
    let watchedFolders: [String]
    let lastEmbeddingModel: String?
    let lastEmbeddingDimension: Int?
    let lastIndexedUtc: String?
}

/// Workstream D1: file-list metadata formatting, parity with the WPF runner's
/// FileSizeConverter + `yyyy-MM-dd` date.
enum LibraryFileFormatting {
    static func size(_ bytes: Int) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(max(bytes, 0)), countStyle: .file)
    }

    /// The API emits an ISO 8601 UTC timestamp; show just the date portion to
    /// match WPF (no time, no timezone conversion). Falls back to the raw value.
    static func shortDate(_ iso: String) -> String {
        let datePart = iso.prefix(10)
        return datePart.count == 10 ? String(datePart) : iso
    }
}

final class RunnerViewModel: ObservableObject {
    @Published var ssdRoot: URL?
    @Published var modelNames: [String] = []
    @Published var selectedModel: String = ""
    @Published var prompt: String = ""
    @Published var response: String = ""
    @Published var responseSources: [String] = []
    @Published var usedRagContext: Bool = false
    @Published var ragWarning: String? = nil
    /// M12: structured chat-failure surface. Populated from the three X13
    /// failure paths (mid-stream `error` NDJSON frame, transport error,
    /// non-2xx HTTP body). Reset at the top of `sendPrompt` so a retry
    /// clears the prior banner. Distinct from `ragWarning` (orange — model
    /// answered without sources) so the user sees one signal per outcome.
    @Published var chatError: String? = nil
    @Published var status: String = "Idle"
    /// MAC36c: drives the Send button's spinner + disabled state while a
    /// streaming chat request is in flight.
    @Published var isSending: Bool = false
    @Published var isEncryptedLocked: Bool = false
    /// MAC39: surfaces "is the on-disk SSD config encrypted at all?" so the
    /// Lock/Start/Stop button picker in `ContentView` can do the right thing.
    /// Encrypted SSD: button stays "Lock" (current behavior — lockSession
    /// tears down the chat host AND zeroizes unlock material). Plaintext SSD:
    /// button toggles Start/Stop on the chat host explicitly because there's
    /// no key to zeroize and "Lock" was actually just a confusing way to
    /// stop chat. Refreshed in `loadConfig`/`attemptUnlock`/`pickSsdRoot`.
    @Published var isEncryptedSsd: Bool = false
    @Published var unlockDialogPresented: Bool = false
    @Published var unlockDialogPassword: String = ""
    @Published var unlockDialogError: String? = nil

    /// MAC6: Network Mode mirrors the Windows Runner toggle. When on, we spawn
    /// the net8.0 sidecar host and surface its base URL so RunnerCli /
    /// Companion clients can connect.
    ///
    /// MAC34: the sidecar is now always running after unlock — chat doesn't
    /// depend on this toggle. The flag now controls bind address only:
    /// OFF (default) → host bound to 127.0.0.1 (loopback chat works);
    /// ON → host bound to `networkBindAddress` from config (LAN exposure).
    /// The flag is still persisted as `networkModeEnabled` in PortableConfig
    /// for cross-OS schema compatibility; only the runtime semantics shifted.
    @Published var networkModeEnabled: Bool = false
    @Published var networkApiStatus: String = "API stopped"
    @Published var networkApiBaseUrl: String? = nil

    /// Opt-in PDF-image OCR at ingest. Persisted as `ocrEnabled` in
    /// PortableConfig (mirrors the Windows runner toggle in the Reference
    /// Documents card). Only meaningful when the Tesseract bundle is staged on
    /// the SSD (see `ocrBundleStaged`); the UI disables the toggle with a hint
    /// otherwise, mirroring the Windows `IsAvailable` gate.
    @Published var ocrEnabled: Bool = false

    // ── Voice (task #94) ─────────────────────────────────────────────────
    // Native macOS voice per decision #166: AVSpeechSynthesizer for TTS,
    // SFSpeechRecognizer (on-device) for STT — no hosted Piper/Whisper, no
    // sidecar round-trip. Fields persist under the same cross-OS PortableConfig
    // keys the Windows runner uses (ttsEnabled/ttsVoiceName/ttsRate/ttsVolume,
    // autoSendVoiceInput) so a drive's intent reads the same on both platforms.
    /// Speak streamed chat responses aloud. Persisted as `ttsEnabled`.
    @Published var ttsEnabled: Bool = false
    /// AVSpeechSynthesisVoice identifier; "" = system default. Persisted as
    /// `ttsVoiceName` (the field name is shared with Windows SAPI voice names —
    /// the value is platform-specific, falling back to default on mismatch).
    @Published var ttsVoiceIdentifier: String = ""
    /// Speech rate, cross-OS encoding -10…10 (0 = neutral). Persisted as `ttsRate`.
    @Published var ttsRate: Double = 0
    /// Speech volume 0…100. Persisted as `ttsVolume`.
    @Published var ttsVolume: Double = 100
    /// Auto-send the prompt after a voice transcription completes. Persisted as
    /// `autoSendVoiceInput` (parity with the Windows STT toggle).
    @Published var autoSendVoiceInput: Bool = true
    /// Installed system voices for the picker, refreshed on config load.
    @Published var availableTtsVoices: [TtsVoiceOption] = []
    /// True while the mic is capturing for a voice query.
    @Published var isListening: Bool = false
    /// Surfaces TTS/STT failures (permission denied, on-device unsupported, no
    /// speech) on a distinct line so it doesn't collide with chat/RAG banners.
    @Published var voiceError: String? = nil

    /// Native voice engines. Construction is side-effect-free (no mic/audio
    /// touch until `start`/`speak`), so these are eager stored properties the
    /// lock/deinit paths can always stop without forcing lazy instantiation.
    private let tts = MacTextToSpeech()
    private let stt = MacSpeechToText()

    /// MAC8: Document library state, populated from /api/library when the
    /// Network Mode sidecar is running. The active library + its files/watched
    /// folders surface in the Documents UI; mutations round-trip through the
    /// sidecar's HTTP API and (for activeLibraryId) Swift persists via
    /// `SsdEncryption` because the sidecar's IConfigStore is a no-op on Mac.
    @Published var libraries: [LibrarySummary] = []
    @Published var activeLibraryId: String? = nil
    @Published var activeLibrary: LibraryDetail? = nil
    @Published var libraryStatus: String = ""
    @Published var libraryBusy: Bool = false
    /// Task #99: live ingest/rebuild progress. `libraryProgress` is the current
    /// file's chunk-embed fraction (0...1) for a determinate bar, or nil for an
    /// indeterminate spinner (busy but no chunk total yet — e.g. OCR running
    /// before embedding). `libraryProgressDetail` is the "1,847 / 3,273 chunks
    /// · ~2m" line. Both clear when the operation finishes.
    @Published var libraryProgress: Double? = nil
    @Published var libraryProgressDetail: String = ""
    /// Wall-clock start of the current file's embed phase, used to estimate the
    /// ETA. Reset per file; nil until the first progress frame arrives.
    private var ingestFileStart: Date? = nil
    /// Throttles progress paints to ~10/sec so a multi-thousand-chunk file
    /// doesn't flood the main thread with SwiftUI invalidations.
    private var lastProgressPaint: Date = .distantPast
    /// Last file the progress bar was painting, so a sweep/rebuild that walks
    /// many files restarts the ETA clock when it moves to the next one.
    private var lastProgressFile: String = ""
    @Published var createLibraryDialogPresented: Bool = false
    @Published var createLibraryName: String = ""
    // D2: rename input sheet + delete confirmation, parity with the WPF runner's
    // rename dialog and delete-confirm MessageBox.
    @Published var renameLibraryDialogPresented: Bool = false
    @Published var renameLibraryName: String = ""
    @Published var deleteLibraryConfirmPresented: Bool = false

    /// Workstream C: proactive embedding-model readiness, mirroring the WPF runner's
    /// inline hint. Sourced from `GET /api/models` (`embeddingModelInstalled`) so the
    /// readiness/tag-normalization logic stays server-side in ModelManagementService.
    /// Defaults to `true` so the Documents UI doesn't flash a "missing" hint before the
    /// first fetch completes.
    @Published var embeddingModelInstalled: Bool = true

    // ── Model parameters (task #35) ──────────────────────────────────────
    // Slider-facing doubles mirror the WPF runner: the far-left position is a
    // "use model default" sentinel zone. `commitModelParams` converts these to
    // the config's -1/0 sentinels before persisting under the camelCase keys
    // the C# PortableConfig uses (modelContextWindow, modelTemperature,
    // modelTopP, modelMaxOutputTokens, modelThinkMode). The Mac sidecar honors
    // these the same as the Windows in-process runner.
    @Published var modelContextWindow: Double = 0                              // 0 = model default
    @Published var modelTemperature: Double = RunnerViewModel.temperatureSliderMin  // <0 = default
    @Published var modelTopP: Double = RunnerViewModel.topPSliderMin                // <0 = default
    @Published var modelMaxOutputTokens: Double = RunnerViewModel.maxOutputSliderMin // <0 = unlimited
    @Published var modelThinkMode: String = ""                                 // "" = model default

    static let temperatureSliderMin = -0.05
    static let topPSliderMin = -0.05
    static let maxOutputSliderMin = -128.0

    private var process: Process?
    private var hostPort = 11434

    /// Lazily constructed so the host controller's deinit shutdown only runs
    /// when the user actually started Network Mode at least once.
    private lazy var hostController: MacRunnerHostController = {
        let ctl = MacRunnerHostController()
        ctl.onStatusChange = { [weak self] s in
            self?.handleHostStatusChange(s)
        }
        ctl.onLogLine = { [weak self] line in
            self?.log("[api] \(line)")
        }
        return ctl
    }()

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

    /// MAC36b: in-flight `/api/chat/stream` task. Held so `lockSession`
    /// can tear it down before the unlock material is zeroized — a
    /// streaming response must not outlive the unlocked session.
    private var activeChatTask: URLSessionDataTask?
    /// Per-call session paired with `activeChatTask`. Invalidated on
    /// completion to break the strong delegate-retention cycle.
    private var activeChatSession: URLSession?

    /// Task #99: in-flight document ingest/sweep/rebuild streaming task and its
    /// session, held so `lockSession` can tear them down before the unlock
    /// material is zeroized and the sidecar is shut down.
    private var activeIngestTask: URLSessionTask?
    private var activeIngestSession: URLSession?

    init() {
        self.ssdRoot = inferSsdRoot()
        loadConfig()
        registerLifecycleHooks()
        // Present the SSD picker only once init returns. Calling pickSsdRoot()
        // synchronously here runs NSOpenPanel.runModal() inside SwiftUI's
        // @StateObject initialization — i.e. mid view-graph update — and the
        // panel's nested run loop re-enters the render, which aborts in
        // AttributeGraph on macOS 26. Hopping to the next main-loop turn lets
        // the first render finish before the modal opens.
        if ssdRoot == nil {
            DispatchQueue.main.async { [weak self] in self?.pickSsdRoot() }
        }
    }

    deinit {
        for o in notificationObservers { NotificationCenter.default.removeObserver(o) }
        // MAC36b: drop the in-flight chat stream before the host process
        // is torn down so the delegate doesn't fire callbacks against a
        // half-released view-model.
        activeChatTask?.cancel()
        activeChatSession?.invalidateAndCancel()
        activeIngestTask?.cancel()
        activeIngestSession?.invalidateAndCancel()
        // Task #94: stop voice engines before teardown.
        tts.stop()
        stt.cancel()
        // MAC6: shut the sidecar before the unlock material is zeroized so the
        // host process never outlives an unlocked session.
        hostController.shutdown()
        unlockMaterial?.zeroize()
    }

    /// Lock on app quit so the derived AES key never outlives the
    /// process. Manual lock is wired through `lockSession()`.
    ///
    /// MAC36a: the previous `willResignActiveNotification` (lock-on-blur)
    /// observer was removed. With MAC30 making encryption opt-in, the
    /// default-plaintext SSD has no key to zeroize and the auto-teardown
    /// of the chat host on every alt-tab was pure friction — users had
    /// to re-select the SSD to get back into a chat. Users who do enable
    /// encryption and want lock-on-idle can request it as an explicit
    /// FTUE preference (F4 follow-up).
    private func registerLifecycleHooks() {
        let nc = NotificationCenter.default
        notificationObservers.append(nc.addObserver(
            forName: NSApplication.willTerminateNotification, object: nil, queue: .main
        ) { [weak self] _ in self?.lockSession(reason: "App terminating") })
    }

    func inferSsdRoot() -> URL? {
        // Post-restructure, Runner.app sits at the SSD ROOT (no longer under
        // mac/), so the SSD root is the bundle's immediate parent. Validate
        // with the same config/ + models/ sibling check pickSsdRoot() uses:
        // it's stricter than the old path-substring test and prevents a dev
        // build run from out/Runner.app from false-positiving.
        let candidate = Bundle.main.bundleURL.deletingLastPathComponent()
        let fm = FileManager.default
        if fm.fileExists(atPath: candidate.appendingPathComponent("config").path),
           fm.fileExists(atPath: candidate.appendingPathComponent("models").path) {
            return candidate
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

        let encrypted = SsdEncryption.isEffectivelyEncryptedForWriteGuard(ssdRoot: root)
        isEncryptedSsd = encrypted

        if encrypted {
            // Encrypted drive: present the unlock dialog. Don't read or
            // decode anything else until the user completes unlock.
            isEncryptedLocked = true
            modelNames = []
            selectedModel = ""
            clearRagState()
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
            clearRagState()
            return
        }
        portableConfig = parsed
        applyConfigToUi(parsed)
        // MAC34: hydrate the LAN-exposure toggle from persisted config and
        // bring up the local chat stack (ollama + sidecar bound to 127.0.0.1
        // by default). Plaintext drives still hit this path during dev.
        networkModeEnabled = (parsed["networkModeEnabled"] as? Bool) ?? false
        ensureLocalChatStackRunning()
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

            // MAC34: hydrate LAN-exposure intent from the unlocked config and
            // bring up the local chat stack. Pre-MAC34 the user had to click
            // Start (started ollama, did nothing for chat) and toggle Network
            // Mode (broken because the API key was empty) for chat to work.
            // Now both happen automatically and chat just works on loopback.
            networkModeEnabled =
                (portableConfig?["networkModeEnabled"] as? Bool) ?? false
            ensureLocalChatStackRunning()
        }
    }

    /// Zeroes the cached UnlockMaterial and drops the in-memory plaintext.
    /// Safe to call when no session is unlocked.
    ///
    /// MAC34: also tears down ollama. Pre-MAC34 ollama lifecycle was tied
    /// to the manual Start/Stop buttons, so a Lock could leave ollama
    /// running on 127.0.0.1 with no SSD-backed model directory after the
    /// next unlock. With auto-spawn, both ollama and the sidecar belong to
    /// the unlocked session and must be torn down together.
    func lockSession(reason: String = "Locked") {
        // MAC36b: cancel any in-flight chat stream first so its delegate
        // can't push tokens into a view-model whose unlock material is
        // about to be zeroized. The session is invalidated in the task's
        // didCompleteWithError handler.
        activeChatTask?.cancel()
        activeChatTask = nil
        isSending = false

        // Task #94: stop any spoken response and abandon a live mic capture —
        // voice must not outlive the unlocked session.
        tts.stop()
        stt.cancel()
        isListening = false
        voiceError = nil

        // Task #99: a streaming ingest must not outlive the unlocked session
        // either — drop it before the sidecar is shut down below.
        activeIngestTask?.cancel()
        activeIngestTask = nil
        libraryBusy = false
        libraryProgress = nil
        libraryProgressDetail = ""

        // MAC6: tear the LAN API down BEFORE we zero the unlock material so
        // the sidecar can never serve requests using a config whose API key
        // has been wiped from this side.
        hostController.shutdown()
        networkApiBaseUrl = nil
        networkApiStatus = "API stopped"
        networkModeEnabled = false

        if process != nil {
            stopOllama()
        }

        guard unlockMaterial != nil || portableConfig != nil else { return }
        unlockMaterial?.zeroize()
        unlockMaterial = nil
        portableConfig = nil
        modelNames = []
        selectedModel = ""
        clearRagState()
        if let root = ssdRoot, SsdEncryption.isEffectivelyEncryptedForWriteGuard(ssdRoot: root) {
            isEncryptedLocked = true
            status = "Encrypted SSD locked"
        }
        log(reason)
    }

    /// MAC39: explicit Stop for the chat host on plaintext SSDs. Tears down
    /// the sidecar + ollama but keeps `portableConfig`, `modelNames`, and
    /// the in-memory state — this is "stop the chat stack" not "lock the
    /// drive". The corresponding Start (`startChatHost`) flips the same
    /// stack back on. `lockSession` stays the right verb for encrypted
    /// drives where there's unlock material to zeroize.
    func stopChatHost(reason: String = "Chat host stopped") {
        activeChatTask?.cancel()
        activeChatTask = nil
        isSending = false

        // Task #94: stop voice alongside the chat stack.
        tts.stop()
        stt.cancel()
        isListening = false

        hostController.shutdown()
        networkApiBaseUrl = nil
        networkApiStatus = "Chat host stopped"

        if process != nil {
            stopOllama()
        }
        log(reason)
    }

    /// MAC39: explicit Start for the chat host on plaintext SSDs. Mirrors
    /// the auto-spawn path that runs at unlock — kept as a separate method
    /// so the UI's intent is legible at the call site.
    func startChatHost() {
        ensureLocalChatStackRunning()
    }

    /// MAC34: UI toggle for "Expose API on LAN". The sidecar always runs
    /// after unlock; this only flips the bind address between 127.0.0.1
    /// (toggle off) and the configured `networkBindAddress` (toggle on).
    func setExposeApiOnLan(_ exposed: Bool) {
        networkModeEnabled = exposed
        if isEncryptedLocked || ssdRoot == nil || portableConfig == nil { return }
        restartHostSidecar()
    }

    /// MAC34: bring up the local chat stack on unlock. Idempotent — safe to
    /// call from both `attemptUnlock` (encrypted path) and `loadConfig`
    /// (plaintext path). Refuses to do anything while the session is locked.
    private func ensureLocalChatStackRunning() {
        guard !isEncryptedLocked,
              ssdRoot != nil,
              portableConfig != nil
        else { return }
        startOllamaIfNotRunning()
        restartHostSidecar()
    }

    /// MAC34: start ollama only when no process is currently tracked. Called
    /// from the unlock-time auto-spawn path; safe to call repeatedly.
    private func startOllamaIfNotRunning() {
        if process != nil { return }
        startOllama()
    }

    /// MAC34: stop and restart the sidecar so its bind address matches the
    /// current `networkModeEnabled` flag. When the toggle is OFF, force
    /// loopback regardless of `networkBindAddress` in config — that field
    /// only takes effect when the user explicitly opts into LAN exposure.
    ///
    /// Also backfills `networkApiKey` if empty: legacy field-test SSDs were
    /// prepped before the PrepApp generated a key, so their config has
    /// `networkApiKey == ""` with `networkRequireApiKey == true`. Pre-MAC34
    /// the sidecar would then 503 every chat request. We generate one for
    /// this session so both the sidecar and `apiKeyForLocalApiRequest()`
    /// agree on a non-empty key. The key is also persisted via
    /// `saveConfig` so subsequent unlocks reuse it (and Windows Runner
    /// would see the same key on the same SSD).
    private func restartHostSidecar() {
        guard let root = ssdRoot, var config = portableConfig else { return }

        hostController.shutdown()

        let storedKey = (config["networkApiKey"] as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if storedKey.isEmpty {
            let generated = Self.generateRandomApiKeyHex()
            config["networkApiKey"] = generated
            // Mirror to in-memory so apiKeyForLocalApiRequest agrees, and
            // queue a config persist so the key survives Lock/Unlock.
            portableConfig?["networkApiKey"] = generated
            saveConfig { current in
                var next = current
                next["networkApiKey"] = generated
                return next
            }
            log("Generated missing networkApiKey for this session.")
        }

        let exposed = networkModeEnabled
        let configuredBind = (config["networkBindAddress"] as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? "127.0.0.1"
        let effectiveBind: String
        if exposed && !configuredBind.isEmpty {
            effectiveBind = configuredBind
        } else {
            effectiveBind = "127.0.0.1"
        }
        // #85: the shared RunnerLocalApiService now always starts the host and
        // treats networkModeEnabled as "expose on LAN" — it forces a loopback
        // bind and skips API-key enforcement when the flag is off. So we pass
        // the real toggle value (no more MAC34a hardcoded-true workaround); the
        // sidecar still auto-spawns at unlock because ensureLocalChatStackRunning
        // calls this regardless of the toggle, and loopback chat just works.
        config["networkModeEnabled"] = exposed
        config["networkBindAddress"] = effectiveBind

        // MAC39: defensive cleanup before re-bind. Mirror of the MAC34b
        // pattern for ollama on port 11434. If a prior sidecar process
        // crashed without releasing its listener, the new bind would fail
        // with "address already in use" and the user would need to quit the
        // app to recover. Kill any straggler bound to the configured
        // network port. Sidecar-side port-shifting (RunnerLocalApiService.
        // ResolveAvailablePort) handles the residual TIME_WAIT case where
        // the kernel hasn't released the port yet — together they cover the
        // observed "Lock + re-unlock fails" flow.
        if let configuredPort = config["networkPort"] as? Int {
            Self.terminateProcessesListening(onPort: configuredPort, log: { [weak self] in self?.log($0) })
        }

        networkApiStatus = "Starting API…"
        do {
            try hostController.start(
                ssdRoot: root,
                ollamaHost: "127.0.0.1:\(hostPort)",
                config: config)
            log("API host bound to \(effectiveBind).")
        } catch {
            networkApiBaseUrl = nil
            networkApiStatus = "API failed: \(error.localizedDescription)"
            log("API host start failed: \(error.localizedDescription)")
        }
    }

    /// MAC34: 32 bytes from `SecRandomCopyBytes`, hex-encoded. Mirrors the
    /// Mac PrepApp's `EncryptedConfigWriter.generateRandomApiKey` helper —
    /// kept inline here so the Runner has no compile-time dependency on
    /// the PrepApp module.
    private static func generateRandomApiKeyHex() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        let status = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        if status == errSecSuccess {
            return bytes.map { String(format: "%02x", $0) }.joined()
        }
        let fallback = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        return fallback + fallback
    }

    /// MAC34b: enumerate PIDs holding a TCP listen on `port` via `lsof`, then
    /// SIGTERM → wait → SIGKILL anything still alive. Empty no-op when
    /// nothing is bound. The user reported v1.3.13 chat failures because
    /// Ollama.app (and a stray CLI ollama) were holding 11434 in the
    /// background, which made the staged binary exit immediately and the
    /// only diagnostic was a single "Ollama exited with code 1" log line.
    static func terminateProcessesListening(onPort port: Int, log: ((String) -> Void)? = nil) {
        let pids = pidsListening(onPort: port)
        guard !pids.isEmpty else { return }
        log?("Found \(pids.count) existing process(es) on port \(port) (PIDs \(pids)). Terminating before starting bundled ollama.")
        for pid in pids { _ = Darwin.kill(pid, SIGTERM) }
        let termDeadline = Date().addingTimeInterval(0.6)
        while Date() < termDeadline {
            if pids.allSatisfy({ Darwin.kill($0, 0) != 0 }) { break }
            Thread.sleep(forTimeInterval: 0.05)
        }
        let stillAlive = pids.filter { Darwin.kill($0, 0) == 0 }
        if !stillAlive.isEmpty {
            log?("PIDs \(stillAlive) ignored SIGTERM; sending SIGKILL.")
            for pid in stillAlive { _ = Darwin.kill(pid, SIGKILL) }
            Thread.sleep(forTimeInterval: 0.2)
        }
    }

    /// `lsof -nP -t -iTCP:<port> -sTCP:LISTEN` returns one PID per line for
    /// every process listening on the given TCP port. We resolve from PID
    /// rather than a name match (`pgrep ollama`) so a sibling ollama serving
    /// a different port (or a non-ollama process that happens to hold 11434)
    /// is handled correctly: the former is left alone, the latter still gets
    /// freed up.
    private static func pidsListening(onPort port: Int) -> [pid_t] {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/sbin/lsof")
        proc.arguments = ["-nP", "-t", "-iTCP:\(port)", "-sTCP:LISTEN"]
        let stdout = Pipe()
        proc.standardOutput = stdout
        proc.standardError = Pipe()
        do {
            try proc.run()
            proc.waitUntilExit()
        } catch {
            return []
        }
        let data = stdout.fileHandleForReading.readDataToEndOfFile()
        guard let text = String(data: data, encoding: .utf8) else { return [] }
        return text
            .split(whereSeparator: { $0.isNewline })
            .compactMap { pid_t($0.trimmingCharacters(in: .whitespaces)) }
    }

    private func handleHostStatusChange(_ status: MacRunnerHostController.Status) {
        switch status {
        case .stopped:
            networkApiBaseUrl = nil
            networkApiStatus = "API stopped"
            // M13: don't clear networkModeEnabled — `.stopped` fires on every
            // deliberate restart (shutdown→status=.stopped is async). `.crashed`
            // owns involuntary state changes.
            libraries = []
            activeLibrary = nil
            activeLibraryId = nil
            libraryStatus = ""
        case .starting:
            networkApiStatus = "Starting API…"
        case .running(let baseUrl):
            networkApiBaseUrl = baseUrl
            networkApiStatus = "API: \(baseUrl)"
            // MAC8: pull the library list as soon as the sidecar is up so the
            // Documents section shows the active library without manual refresh.
            refreshLibraries()
            // Workstream C: surface embedding-model readiness proactively at unlock.
            refreshEmbeddingModelStatus()
        case .crashed(let message):
            networkApiBaseUrl = nil
            networkModeEnabled = false
            networkApiStatus = "API host crashed: \(message)"
            log("Mac runner host crashed: \(message)")
        }
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

    /// MAC33: Picker source is on-disk truth, not config-pinned status.
    /// The Mac sidecar can't write back to the encrypted config after pulls
    /// (the passphrase is zeroized before pulls run), so config["models"]
    /// is empty on a Mac-prepped SSD even when the models are present on
    /// disk. Mirrors runner-core's GetInstalledModelNames swap so the Mac
    /// SwiftUI picker, the Windows WPF picker, and the LAN /models endpoint
    /// all agree on what's installed.
    private func applyConfigToUi(_ config: [String: Any]) {
        let installed = discoverInstalledModelsOnDisk()
        modelNames = installed
        selectedModel = installed.first ?? ""
        hydrateModelParams(config)
        // Hydrate the OCR opt-in from persisted config (default false). Single
        // place both the plaintext-load and unlock paths route through.
        ocrEnabled = (config["ocrEnabled"] as? Bool) ?? false
        hydrateVoiceConfig(config)
    }

    /// Mirror the persisted voice knobs into the published state and apply them
    /// to the native TTS engine. Voice list is refreshed here so the picker has
    /// options as soon as a config loads. Defaults match Windows PortableConfig.
    private func hydrateVoiceConfig(_ config: [String: Any]) {
        ttsEnabled = (config["ttsEnabled"] as? Bool) ?? false
        // A present key (incl. "" = an explicit "System default" pick) is the
        // user's choice and is respected. Only when the key is absent — a fresh
        // config — do we land on the best installed neural voice so TTS sounds
        // good out of the box instead of the robotic compact default.
        if let savedVoice = config["ttsVoiceName"] as? String {
            ttsVoiceIdentifier = savedVoice
        } else {
            ttsVoiceIdentifier = MacTextToSpeech.bestDefaultVoiceIdentifier() ?? ""
        }
        ttsRate = Double((config["ttsRate"] as? NSNumber)?.intValue ?? 0)
        ttsVolume = Double((config["ttsVolume"] as? NSNumber)?.intValue ?? 100)
        autoSendVoiceInput = (config["autoSendVoiceInput"] as? Bool) ?? true
        availableTtsVoices = MacTextToSpeech.availableVoices()
        applyTtsConfig()
    }

    private func applyTtsConfig() {
        tts.configure(voiceIdentifier: ttsVoiceIdentifier, rate: Int(ttsRate), volume: Int(ttsVolume))
    }

    /// True when the Tesseract OCR bundle is staged on the SSD — i.e. the
    /// relocated `tesseract` binary exists at `mac/tools/tesseract/`. Mirrors
    /// the Windows runner's `IsAvailable` gate (which checks the staged exe);
    /// the OCR toggle is disabled-with-hint until this holds.
    var ocrBundleStaged: Bool {
        guard let root = ssdRoot else { return false }
        let exe = root
            .appendingPathComponent("mac/tools/tesseract/tesseract")
        return FileManager.default.fileExists(atPath: exe.path)
    }

    /// UI setter for the OCR toggle. Updates the published flag and persists it
    /// via `saveConfig` so the next ingest (and the Windows/LAN readers) see the
    /// same intent. `saveConfig` refuses while the drive is locked.
    func setOcrEnabled(_ enabled: Bool) {
        ocrEnabled = enabled
        saveConfig { current in
            var config = current
            config["ocrEnabled"] = enabled
            return config
        }
    }

    // MARK: - Voice setters & STT capture (task #94)

    func setTtsEnabled(_ enabled: Bool) {
        ttsEnabled = enabled
        if !enabled { tts.stop() }
        saveConfig { current in var c = current; c["ttsEnabled"] = enabled; return c }
    }

    func setTtsVoice(_ identifier: String) {
        ttsVoiceIdentifier = identifier
        applyTtsConfig()
        saveConfig { current in var c = current; c["ttsVoiceName"] = identifier; return c }
    }

    /// Slider commit (on drag end) — snap to whole steps, apply, persist. Matches
    /// the model-param sliders' "commit on editing end" cadence.
    func commitTtsRate() {
        let snapped = Int(ttsRate.rounded())
        ttsRate = Double(snapped)
        applyTtsConfig()
        saveConfig { current in var c = current; c["ttsRate"] = snapped; return c }
    }

    func commitTtsVolume() {
        let snapped = Int(ttsVolume.rounded())
        ttsVolume = Double(snapped)
        applyTtsConfig()
        saveConfig { current in var c = current; c["ttsVolume"] = snapped; return c }
    }

    func setAutoSendVoiceInput(_ enabled: Bool) {
        autoSendVoiceInput = enabled
        saveConfig { current in var c = current; c["autoSendVoiceInput"] = enabled; return c }
    }

    /// Settings "Test voice" button — speak a fixed line with the current voice.
    func testTtsVoice() {
        voiceError = nil
        applyTtsConfig()
        tts.speak("This is a test of the assistant voice.")
    }

    /// Mic button: start capture if idle, finalize + transcribe if recording.
    func toggleVoiceInput() {
        if stt.isRecording {
            finishVoiceInput()
        } else {
            startVoiceInput()
        }
    }

    private func startVoiceInput() {
        voiceError = nil
        // Don't talk over the user while they dictate.
        tts.stop()
        MacSpeechToText.requestAuthorization { [weak self] granted in
            guard let self else { return }
            guard granted else {
                self.voiceError = SpeechToTextError.notAuthorized.errorDescription
                return
            }
            do {
                try self.stt.start(onPartial: { [weak self] partial in
                    guard let self, self.isListening else { return }
                    self.status = partial.isEmpty ? "Listening…" : "Listening… \(partial)"
                })
                self.isListening = true
                self.status = "Listening…"
            } catch {
                self.voiceError = (error as? SpeechToTextError)?.errorDescription
                    ?? error.localizedDescription
            }
        }
    }

    private func finishVoiceInput() {
        stt.stop { [weak self] result in
            guard let self else { return }
            self.isListening = false
            switch result {
            case .success(let text):
                self.insertVoiceTranscript(text)
            case .failure(let err):
                self.status = "Idle"
                self.voiceError = (err as? SpeechToTextError)?.errorDescription
                    ?? err.localizedDescription
            }
        }
    }

    private func insertVoiceTranscript(_ text: String) {
        prompt = prompt.isEmpty ? text : prompt + " " + text
        status = "Idle"
        // Auto-send mirrors the Windows STT default: dictate and go. Suppressed
        // while a chat or ingest is in flight (sendPrompt re-checks anyway).
        if autoSendVoiceInput { sendPrompt() }
    }

    /// Mirror the persisted model knobs into the slider/picker state. Setting
    /// the `@Published` values directly (rather than through the Picker/Slider
    /// bindings) intentionally does NOT trigger a commit — only user interaction
    /// does, via `commitModelParams` / `setThinkMode`.
    private func hydrateModelParams(_ config: [String: Any]) {
        modelContextWindow = Double((config["modelContextWindow"] as? NSNumber)?.intValue ?? 0)
        let t = (config["modelTemperature"] as? NSNumber)?.doubleValue ?? -1
        modelTemperature = t >= 0 ? t : RunnerViewModel.temperatureSliderMin
        let p = (config["modelTopP"] as? NSNumber)?.doubleValue ?? -1
        modelTopP = p >= 0 ? p : RunnerViewModel.topPSliderMin
        let m = (config["modelMaxOutputTokens"] as? NSNumber)?.intValue ?? -1
        modelMaxOutputTokens = m >= 0 ? Double(m) : RunnerViewModel.maxOutputSliderMin
        modelThinkMode = (config["modelThinkMode"] as? String) ?? ""
    }

    /// Picker setter — discrete, so it commits immediately on selection.
    func setThinkMode(_ mode: String) {
        modelThinkMode = mode
        commitModelParams()
    }

    func resetModelParams() {
        modelContextWindow = 0
        modelTemperature = RunnerViewModel.temperatureSliderMin
        modelTopP = RunnerViewModel.topPSliderMin
        modelMaxOutputTokens = RunnerViewModel.maxOutputSliderMin
        modelThinkMode = ""
        commitModelParams()
    }

    /// Persist the five model knobs (converting slider positions to the config's
    /// sentinel encoding, matching the WPF ValueChanged handlers) and push them
    /// to a running sidecar. The sidecar captures config at StartAsync, so it
    /// needs a restart to pick up new params; we skip the restart mid-stream so
    /// a live chat isn't torn down — the change still persists and lands on the
    /// next host start. `saveConfig` itself refuses to write while locked.
    func commitModelParams() {
        let ctx = max(0, Int((modelContextWindow / 512).rounded()) * 512)
        let temp = modelTemperature < 0 ? -1.0 : (modelTemperature * 20).rounded() / 20
        let topP = modelTopP < 0 ? -1.0 : (modelTopP * 20).rounded() / 20
        let snappedMax = Int((modelMaxOutputTokens / 128).rounded()) * 128
        let maxOut = (modelMaxOutputTokens < 0 || snappedMax <= 0) ? -1 : snappedMax
        let think = modelThinkMode

        saveConfig { current in
            var next = current
            next["modelContextWindow"] = ctx
            next["modelTemperature"] = temp
            next["modelTopP"] = topP
            next["modelMaxOutputTokens"] = maxOut
            next["modelThinkMode"] = think
            return next
        }

        if networkApiBaseUrl != nil && !isSending {
            restartHostSidecar()
        }
    }

    /// MAC33: Walks `<ssdRoot>/models/manifests/` and reconstructs model tags.
    /// Mirrors prep-core's `ModelOperations.DiscoverModelsOnDisk`, including
    /// the 2026-05-11 HF fix: HF models live under `hf.co/<owner>/<repo>/<tag>`
    /// and must be referenced as `hf.co/<owner>/<repo>:<tag>` when calling
    /// Ollama's API. Without the full prefix Ollama returns 404 because it looks
    /// in `registry.ollama.ai/library/` instead of the `hf.co/` subtree.
    private func discoverInstalledModelsOnDisk() -> [String] {
        guard let root = ssdRoot else { return [] }
        let manifestsRoot = root.appendingPathComponent("models/manifests")
        let fm = FileManager.default
        guard fm.fileExists(atPath: manifestsRoot.path),
              let enumerator = fm.enumerator(
                at: manifestsRoot,
                includingPropertiesForKeys: [.isRegularFileKey],
                options: [.skipsHiddenFiles]) else {
            return []
        }

        let manifestsComponents = manifestsRoot.pathComponents
        var discovered = Set<String>()
        for case let url as URL in enumerator {
            let values = try? url.resourceValues(forKeys: [.isRegularFileKey])
            guard values?.isRegularFile == true else { continue }
            let parts = url.pathComponents
            guard parts.count > manifestsComponents.count else { continue }
            let rel = Array(parts[manifestsComponents.count...])
            guard rel.count >= 2 else { continue }

            let modelId: String
            if rel.count >= 4 && rel[0].caseInsensitiveCompare("hf.co") == .orderedSame {
                // hf.co/<owner>/<repo>/<tag>  →  hf.co/<owner>/<repo>:<tag>
                modelId = "hf.co/\(rel[rel.count - 3])/\(rel[rel.count - 2]):\(rel[rel.count - 1])"
            } else {
                // registry.ollama.ai/library/<model>/<tag>  →  <model>:<tag>
                modelId = "\(rel[rel.count - 2]):\(rel[rel.count - 1])"
            }
            discovered.insert(modelId)
        }

        return discovered.sorted { $0.lowercased() < $1.lowercased() }
    }

    func startOllama() {
        guard process == nil, let root = ssdRoot else { return }
        if isEncryptedLocked {
            status = "Unlock encrypted SSD first"
            unlockDialogPresented = true
            return
        }

        // MAC26: the macOS Ollama distribution is a GUI app bundle. The real
        // self-contained CLI server lives inside Ollama.app's Resources; the
        // top-level LaunchServices shim is deleted at staging time so this is
        // the only ollama binary on the SSD.
        let ollama = root.appendingPathComponent("mac/tools/ollama/Ollama.app/Contents/Resources/ollama")
        guard FileManager.default.fileExists(atPath: ollama.path) else { status = "Missing mac/tools/ollama/Ollama.app/Contents/Resources/ollama"; return }

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

        // MAC34b: free port 11434 before launching the staged ollama. Field
        // test surfaced silent "Ollama exited with code 1" because Ollama.app
        // (or a leftover CLI server) was already bound to 127.0.0.1:11434, so
        // the staged binary never got to serve. Windows side-steps this with
        // port-shifting (OllamaLifecycleService.ResolvePort scans preferred+20);
        // we don't have that on Mac because the C# sidecar handshake hardcodes
        // the host URL. Kill-by-PID via `lsof` so we only hit processes
        // actually holding 11434 — a sibling ollama serving a different port
        // for another use case stays untouched.
        Self.terminateProcessesListening(onPort: hostPort, log: { [weak self] in self?.log($0) })

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

    /// MAC36b: stream `/api/chat/stream` (NDJSON) instead of waiting for
    /// the full `/api/chat` response so tokens land in the UI as they're
    /// produced. The contract is documented at
    /// `runner-core/Services/RunnerLocalApiService.cs:209-263`.
    /// macOS 11 baseline rules out `URLSession.bytes(for:)` (12+); the
    /// `URLSessionDataDelegate` chunk callback is the equivalent that
    /// works on the pinned target.
    func sendPrompt() {
        guard !selectedModel.isEmpty else { return }
        guard !prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }

        // Task #99: don't query a half-built index. While documents are
        // indexing, retrieval returns few/zero chunks and the model answers
        // ungrounded — which reads as a bug. Hold the send until ready.
        guard !libraryBusy else {
            status = "Documents are indexing — chat is paused until ready."
            return
        }

        guard let baseUrl = networkApiBaseUrl,
              let url = URL(string: "\(baseUrl)/api/chat/stream") else {
            // MAC34: post-auto-spawn this should be unreachable from the
            // golden path — the chat host comes up at unlock. Surfacing it
            // anyway so a sidecar crash mid-session shows up in the UI.
            status = "Chat host not running. Lock and unlock to restart."
            networkApiStatus = "API down"
            log("Chat refused: mac-runner-host is not running.")
            return
        }

        // Tear any prior in-flight chat down before starting a new one.
        activeChatTask?.cancel()
        activeChatSession?.invalidateAndCancel()
        activeChatTask = nil
        activeChatSession = nil

        // Reset the response box so streamed tokens don't append onto a
        // stale answer.
        response = ""
        clearRagState()
        chatError = nil
        voiceError = nil
        isSending = true
        status = "Sending..."

        // Task #94: start a fresh spoken response. beginStreaming stops any
        // prior in-flight speech so a new query doesn't talk over the last one.
        if ttsEnabled {
            applyTtsConfig()
            tts.beginStreaming()
        } else {
            tts.stop()
        }

        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.addValue("application/json", forHTTPHeaderField: "Content-Type")
        req.addValue("application/x-ndjson", forHTTPHeaderField: "Accept")
        if let apiKey = apiKeyForLocalApiRequest() {
            req.addValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        }
        req.httpBody = try? JSONSerialization.data(
            withJSONObject: ["model": selectedModel, "prompt": prompt])

        let delegate = ChatStreamDelegate(owner: self)
        // MAC39: bump timeoutIntervalForRequest from the default 60s to 180s
        // for the chat stream. The server emits a `start` frame immediately
        // (see RunnerLocalApiService.cs:222), so the per-request timer
        // resets as soon as the request lands — but cold-loading a 5GB+
        // model on a USB SSD can take 30-90s before Ollama emits the first
        // token, and 60s isn't enough headroom on every machine. 180s gives
        // the cold-load path comfortable room while still bounding a truly
        // wedged request.
        let sessionConfig = URLSessionConfiguration.default
        sessionConfig.timeoutIntervalForRequest = 180
        let session = URLSession(configuration: sessionConfig,
                                 delegate: delegate,
                                 delegateQueue: nil)
        let task = session.dataTask(with: req)
        activeChatSession = session
        activeChatTask = task
        task.resume()
    }

    // MARK: - MAC36b: chat stream callbacks (invoked from delegate)

    fileprivate func chatStreamDidReceiveLine(_ line: Data) {
        guard let obj = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              let type = obj["type"] as? String else { return }
        switch type {
        case "start":
            // Sources/usedRagContext on the start frame are placeholders
            // (server emits authoritative values on `complete`). Nothing
            // to surface here yet.
            break
        case "token":
            if let token = obj["token"] as? String, !token.isEmpty {
                DispatchQueue.main.async { [weak self] in
                    guard let self else { return }
                    self.response.append(token)
                    // Task #94: stream the token into the sentence chunker so TTS
                    // speaks each sentence as it completes (no-op when TTS off).
                    if self.ttsEnabled { self.tts.feed(token) }
                    // C1: clear the "Loading <model>… NNs" status once
                    // tokens start flowing. hasPrefix gate fires once per
                    // request — subsequent token writes find status =
                    // "Generating…" and skip the update.
                    if self.status.hasPrefix("Loading ") || self.status == "Sending..." {
                        self.status = "Generating\u{2026}"
                    }
                }
            }
        case "loading":
            // C1: server-side heartbeat frame emitted every 20s while
            // ChatService awaits Ollama's first token. Two purposes: (1)
            // bytes flowing keep URLSession's 180s per-packet timeout from
            // firing across cold-loads of large models (qwen3:14b on USB
            // SSD can take 60-300s); (2) the elapsed-seconds payload lets
            // us paint a live "Loading <model>… NNs" status so the user
            // knows the system is alive. Stops once a `token` frame
            // arrives.
            let elapsed = (obj["elapsedSeconds"] as? Int) ?? 0
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.status = "Loading \(self.selectedModel)\u{2026} \(elapsed)s"
            }
        case "rag-warning":
            let message = (obj["message"] as? String) ?? "RAG retrieval failed."
            DispatchQueue.main.async { [weak self] in
                self?.ragWarning = message
            }
        case "indexing-warning":
            // Task #99: the host emits this when a library ingest/rebuild is
            // mid-flight, so retrieval may be incomplete. Surface it on the
            // same orange notice surface as the RAG warning. (On this device a
            // local ingest already pauses Send; this covers a second device
            // querying the host over the LAN.)
            let message = (obj["message"] as? String) ?? "Documents are still indexing — answers may be incomplete."
            DispatchQueue.main.async { [weak self] in
                self?.ragWarning = message
            }
        case "complete":
            let sources = (obj["sources"] as? [String]) ?? []
            let usedRag = (obj["usedRagContext"] as? Bool) ?? false
            // Authoritative final text from the server. Used as a
            // fallback only — overwriting the streamed buffer would
            // cause a visible flicker for the common case where every
            // token frame already arrived.
            let fallbackText = obj["responseText"] as? String
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.responseSources = sources
                self.usedRagContext = usedRag
                if self.response.isEmpty, let fallbackText {
                    self.response = fallbackText
                }
                self.status = usedRag ? "Answered with sources" : "Answered"
                // Task #94: flush the trailing sentence fragment to TTS.
                if self.ttsEnabled { self.tts.finishStreaming() }
            }
        case "error":
            // M12: X13 ChatResult.Failure surfaced as a mid-stream NDJSON
            // frame. Mirror Windows MainWindow.xaml.cs:519-521 — log the
            // backend reason and set chatError so ContentView paints the
            // red banner. Status keeps its concise summary for the title
            // strip.
            let message = (obj["message"] as? String) ?? "stream error"
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.status = "Chat failed: \(message)"
                self.chatError = message
                self.clearRagState()
                // Task #94: a failed response shouldn't keep speaking buffered text.
                if self.ttsEnabled { self.tts.stop() }
                self.log("Chat backend error: \(message)")
            }
        default:
            break
        }
    }

    fileprivate func chatStreamDidComplete(error: Error?) {
        // Always release the per-call session so the delegate retain
        // cycle (URLSession -> delegate -> owner) is broken.
        let session = activeChatSession
        DispatchQueue.main.async { [weak self] in
            guard let self else {
                session?.finishTasksAndInvalidate()
                return
            }
            self.activeChatTask = nil
            self.activeChatSession = nil
            self.isSending = false
            if let error {
                let nsError = error as NSError
                if nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled {
                    // Cancellation is initiated by Lock or by a fresh
                    // sendPrompt — UI state is already where it needs
                    // to be. No status overwrite.
                } else {
                    // M12: transport-layer failure (timeout, connection
                    // dropped, sidecar crash). Surface to the red banner
                    // and the file log so the user has a durable reason
                    // beyond the single-line status strip.
                    let message = error.localizedDescription
                    self.status = "Chat failed: \(message)"
                    self.chatError = message
                    // Task #94: stop speaking a response that won't complete.
                    if self.ttsEnabled { self.tts.stop() }
                    self.log("Chat transport error: \(message)")
                }
            }
            session?.finishTasksAndInvalidate()
        }
    }

    /// M12: always `.allow` so the delegate can read the response body.
    /// `RunnerLocalApiService` returns small JSON `ErrorResponse` /
    /// ProblemDetails bodies on validation 400s and 5xx — pre-M12 we
    /// cancelled before the body landed and surfaced only "API returned
    /// N", losing the structured reason X13 expects callers to render.
    /// The delegate now tracks `httpStatus` and either feeds 200 chunks
    /// to the NDJSON parser or buffers non-2xx bytes for `decodeError`.
    fileprivate func chatStreamDidReceiveResponse(_ response: HTTPURLResponse) -> URLSession.ResponseDisposition {
        return .allow
    }

    /// M12: invoked by `ChatStreamDelegate` when a non-2xx response
    /// completes. Decodes the buffered body and routes through the same
    /// red-banner + file-log surface as the other two failure paths.
    fileprivate func chatStreamDidReceiveErrorBody(statusCode: Int, body: Data?) {
        let message = RunnerChatErrorMessage.decode(statusCode: statusCode, body: body)
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.status = "Chat failed: \(message)"
            self.chatError = message
            self.clearRagState()
            self.log("Chat HTTP \(statusCode): \(message)")
        }
    }

    private func clearRagState() {
        responseSources = []
        usedRagContext = false
        ragWarning = nil
    }

    private func apiKeyForLocalApiRequest() -> String? {
        guard let config = portableConfig else { return nil }
        let requireKey = (config["networkRequireApiKey"] as? Bool) ?? true
        guard requireKey else { return nil }
        let key = (config["networkApiKey"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return key.isEmpty ? nil : key
    }

    // MARK: - MAC8 Document Library Management
    //
    // All library mutations route through the mac-runner-host sidecar's
    // /api/library/* endpoints. The same RunnerLocalApiService that serves
    // /api/chat/* on Windows. The sidecar's IConfigStore is a no-op on Mac
    // (NoOpConfigStore) to preserve the MAC5/MAC6 plaintext-config invariant,
    // so when an endpoint mutates ActiveDocumentLibraryId we apply the change
    // here in Swift via saveConfig() so it persists encrypted on disk.

    func refreshLibraries() {
        guard let baseUrl = networkApiBaseUrl, let url = URL(string: "\(baseUrl)/api/library") else {
            return
        }
        var req = URLRequest(url: url)
        if let key = apiKeyForLocalApiRequest() {
            req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        }
        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self else { return }
            if let error {
                DispatchQueue.main.async { self.libraryStatus = "Library list failed: \(error.localizedDescription)" }
                return
            }
            guard let http = urlResponse as? HTTPURLResponse,
                  (200..<300).contains(http.statusCode), let data else {
                DispatchQueue.main.async { self.libraryStatus = "Library list failed (HTTP \((urlResponse as? HTTPURLResponse)?.statusCode ?? 0))" }
                return
            }
            guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                DispatchQueue.main.async { self.libraryStatus = "Library list: malformed response" }
                return
            }
            let libsRaw = (obj["libraries"] as? [[String: Any]]) ?? []
            let libs: [LibrarySummary] = libsRaw.compactMap { entry in
                guard let id = entry["id"] as? String, let name = entry["name"] as? String else { return nil }
                return LibrarySummary(id: id, name: name)
            }
            let activeId = obj["activeLibraryId"] as? String
            let activeDetail = self.decodeActiveLibrary(obj["activeLibrary"] as? [String: Any])
            DispatchQueue.main.async {
                self.libraries = libs
                self.activeLibraryId = activeId
                self.activeLibrary = activeDetail
                // Zero-state guidance lives in the Documents body (DocumentsSection),
                // not this transient caption — clear it on a successful refresh.
                self.libraryStatus = ""
            }
        }.resume()
    }

    /// Workstream C: fetch embedding-model readiness from the sidecar so the Documents
    /// section can prompt a pull before an ingest fails. Mirrors the auth pattern of
    /// `refreshLibraries()`; on any error it leaves the prior value (no false alarm).
    func refreshEmbeddingModelStatus() {
        guard let baseUrl = networkApiBaseUrl, let url = URL(string: "\(baseUrl)/api/models") else {
            return
        }
        var req = URLRequest(url: url)
        if let key = apiKeyForLocalApiRequest() {
            req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        }
        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self, error == nil,
                  let http = urlResponse as? HTTPURLResponse, (200..<300).contains(http.statusCode),
                  let data,
                  let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let installed = obj["embeddingModelInstalled"] as? Bool else {
                return
            }
            DispatchQueue.main.async { self.embeddingModelInstalled = installed }
        }.resume()
    }

    func createLibrary(name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        guard let baseUrl = networkApiBaseUrl, let url = URL(string: "\(baseUrl)/api/library") else {
            libraryStatus = "Start Network Mode first."
            return
        }

        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.addValue("application/json", forHTTPHeaderField: "Content-Type")
        if let key = apiKeyForLocalApiRequest() { req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["name": trimmed])

        libraryBusy = true
        libraryStatus = "Creating library…"
        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self else { return }
            DispatchQueue.main.async { self.libraryBusy = false }
            if let http = urlResponse as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                let msg = RunnerChatErrorMessage.decode(statusCode: http.statusCode, body: data)
                DispatchQueue.main.async { self.libraryStatus = msg }
                return
            }
            if error != nil {
                DispatchQueue.main.async { self.libraryStatus = "Create failed: \(error!.localizedDescription)" }
                return
            }
            guard let data,
                  let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let activeId = obj["activeLibraryId"] as? String else {
                DispatchQueue.main.async { self.libraryStatus = "Create failed: malformed response" }
                return
            }
            DispatchQueue.main.async {
                self.persistActiveLibraryId(activeId)
                self.refreshLibraries()
            }
        }.resume()
    }

    func setActiveLibrary(libraryId: String?) {
        guard let baseUrl = networkApiBaseUrl, let url = URL(string: "\(baseUrl)/api/library/active") else {
            libraryStatus = "Start Network Mode first."
            return
        }
        var req = URLRequest(url: url)
        req.httpMethod = "PUT"
        req.addValue("application/json", forHTTPHeaderField: "Content-Type")
        if let key = apiKeyForLocalApiRequest() { req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        let body: [String: Any] = ["libraryId": libraryId as Any]
        req.httpBody = try? JSONSerialization.data(withJSONObject: body)

        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self else { return }
            if let http = urlResponse as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                let msg = RunnerChatErrorMessage.decode(statusCode: http.statusCode, body: data)
                DispatchQueue.main.async { self.libraryStatus = msg }
                return
            }
            if error != nil {
                DispatchQueue.main.async { self.libraryStatus = "Set active failed: \(error!.localizedDescription)" }
                return
            }
            DispatchQueue.main.async {
                self.persistActiveLibraryId(libraryId)
                self.refreshLibraries()
            }
        }.resume()
    }

    func pickAndIngestFiles() {
        guard let activeId = activeLibraryId else {
            libraryStatus = "Create or select a library first."
            return
        }
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.allowedFileTypes = ["pdf", "txt", "md", "json", "csv"]
        guard panel.runModal() == .OK else { return }
        let urls = panel.urls
        guard !urls.isEmpty else { return }
        ingestFiles(urls: urls, libraryId: activeId)
    }

    /// Uploads each picked document in its OWN multipart request, sequentially.
    /// The sidecar sizes its Kestrel/FormOptions body limits to the per-file
    /// MaxDocumentSizeMB policy (+ headroom), so batching every file into a
    /// single request would 413 the whole batch before anything ingested (the
    /// bug that left libraries empty and the model hallucinating). One file per
    /// request keeps each body within the per-file limit and yields accurate
    /// per-file rejection messages; outcomes are aggregated into one status line.
    private func ingestFiles(urls: [URL], libraryId: String) {
        guard let baseUrl = networkApiBaseUrl,
              let url = URL(string: "\(baseUrl)/api/library/\(libraryId)/files") else {
            libraryStatus = "Start Network Mode first."
            return
        }
        guard !urls.isEmpty else { return }

        libraryBusy = true

        // Mutated only from the sequential completion chain below (one upload in
        // flight at a time), so no concurrent access.
        var totalRejected = 0
        var totalSkipped = 0
        var totalFailedChunks = 0
        var firstError: String? = nil
        var latestDetail: LibraryDetail? = nil

        func finish() {
            DispatchQueue.main.async {
                self.libraryBusy = false
                self.libraryProgress = nil
                self.libraryProgressDetail = ""
                self.activeIngestSession = nil
                self.activeIngestTask = nil
                if let latestDetail { self.activeLibrary = latestDetail }
                if let firstError {
                    self.libraryStatus = "Ingest failed: \(firstError)"
                } else {
                    let fileCount = latestDetail?.fileCount ?? 0
                    var status = "Ingest complete: \(fileCount) file(s)"
                    if totalSkipped > 0 { status += ", \(totalSkipped) skipped" }
                    if totalFailedChunks > 0 { status += " (\(totalFailedChunks) chunk(s) failed)" }
                    if totalRejected > 0 { status += " (\(totalRejected) rejected — see logs)" }
                    self.libraryStatus = status
                }
                self.refreshLibraries()
            }
        }

        func uploadNext(_ index: Int) {
            if index >= urls.count {
                finish()
                return
            }

            let fileURL = urls[index]
            DispatchQueue.main.async {
                self.libraryStatus = "Uploading \(index + 1) of \(urls.count): \(fileURL.lastPathComponent)…"
                self.libraryProgress = nil
                self.libraryProgressDetail = ""
            }

            guard let fileData = try? Data(contentsOf: fileURL) else {
                self.log("[library] could not read '\(fileURL.lastPathComponent)' — skipping")
                totalRejected += 1
                uploadNext(index + 1)
                return
            }

            var req = URLRequest(url: url)
            req.httpMethod = "POST"
            if let key = self.apiKeyForLocalApiRequest() { req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
            req.addValue("application/x-ndjson", forHTTPHeaderField: "Accept")
            let boundary = "Boundary-\(UUID().uuidString)"
            req.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

            var body = Data()
            body.append("--\(boundary)\r\n".data(using: .utf8)!)
            body.append("Content-Disposition: form-data; name=\"files\"; filename=\"\(fileURL.lastPathComponent)\"\r\n".data(using: .utf8)!)
            body.append("Content-Type: application/octet-stream\r\n\r\n".data(using: .utf8)!)
            body.append(fileData)
            body.append("\r\n".data(using: .utf8)!)
            body.append("--\(boundary)--\r\n".data(using: .utf8)!)

            // Task #99: stream the NDJSON response so per-chunk progress paints a
            // live bar instead of the old buffer-until-done flood. macOS 11
            // rules out URLSession.bytes(for:) (12+); the delegate chunk
            // callback is the equivalent, mirroring the chat-stream path.
            self.ingestFileStart = nil
            let accumulator = IngestProgressAccumulator()
            let delegate = NdjsonStreamDelegate(
                onLine: { [weak self] line in
                    self?.foldIngestFrame(line, into: accumulator, fileIndex: index, fileCount: urls.count)
                },
                onComplete: { [weak self] statusCode, errorBody, transportError in
                    guard let self else { return }
                    let session = self.activeIngestSession
                    self.activeIngestSession = nil
                    self.activeIngestTask = nil
                    defer { session?.finishTasksAndInvalidate() }

                    let nsError = transportError as NSError?
                    if nsError?.domain == NSURLErrorDomain && nsError?.code == NSURLErrorCancelled {
                        // Cancelled by Lock/Stop — UI state is already reset there.
                        finish()
                        return
                    }
                    if statusCode == 0 {
                        let msg = transportError?.localizedDescription ?? "upload failed"
                        if firstError == nil { firstError = msg }
                        self.log("[library] upload '\(fileURL.lastPathComponent)' failed: \(msg)")
                        uploadNext(index + 1)
                        return
                    }
                    if !(200..<300).contains(statusCode) {
                        let msg = RunnerChatErrorMessage.decode(statusCode: statusCode, body: errorBody)
                        if firstError == nil { firstError = msg }
                        self.log("[library] upload '\(fileURL.lastPathComponent)' failed (HTTP \(statusCode)): \(msg)")
                        uploadNext(index + 1)
                        return
                    }
                    let outcome = accumulator.outcome
                    totalRejected += outcome.rejectedCount
                    totalSkipped += outcome.skippedFiles
                    totalFailedChunks += outcome.failedChunks
                    if let detail = outcome.completedDetail { latestDetail = detail }
                    if let message = outcome.errorMessage, firstError == nil { firstError = message }
                    outcome.rejectionLogs.forEach { self.log($0) }
                    uploadNext(index + 1)
                })

            // The server emits an `indexing-heartbeat` every ~15s during quiet
            // stretches (OCR before embed), so the per-packet timer stays alive;
            // 300s gives wide headroom over that interval.
            let sessionConfig = URLSessionConfiguration.default
            sessionConfig.timeoutIntervalForRequest = 300
            let session = URLSession(configuration: sessionConfig, delegate: delegate, delegateQueue: nil)
            let task = session.uploadTask(with: req, from: body)
            self.activeIngestSession = session
            self.activeIngestTask = task
            task.resume()
        }

        uploadNext(0)
    }

    /// Reference accumulator for one file's streamed ingest frames. A class so
    /// the escaping delegate closure can mutate it (escaping closures can't
    /// capture an `inout` value). (Task #99)
    private final class IngestProgressAccumulator {
        var outcome = IngestFileOutcome()
    }

    /// Folds one streamed ingest NDJSON line into `accumulator` and, for
    /// non-terminal `progress` frames, paints the live bar. Same frame handling
    /// as the old buffered `parseIngestNdjson`, applied incrementally. (Task #99)
    private func foldIngestFrame(_ line: Data, into accumulator: IngestProgressAccumulator, fileIndex: Int, fileCount: Int) {
        guard let frame = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              let type = frame["type"] as? String else {
            return
        }
        switch type {
        case "file-rejected":
            accumulator.outcome.rejectedCount += 1
            if let fileName = frame["fileName"] as? String,
               let reason = frame["reason"] as? String {
                accumulator.outcome.rejectionLogs.append("[library] rejected '\(fileName)': \(reason)")
            }
        case "progress":
            if let s = frame["skippedFiles"] as? Int, s > 0 { accumulator.outcome.skippedFiles = s }
            let currentFile = (frame["currentFile"] as? String) ?? ""
            let isTerminalFrame = currentFile.isEmpty
            if isTerminalFrame {
                if let f = frame["failedChunks"] as? Int { accumulator.outcome.failedChunks = f }
            } else {
                let embedded = (frame["embeddedChunks"] as? Int) ?? 0
                let total = (frame["totalChunks"] as? Int) ?? 0
                paintIngestProgress(embedded: embedded, total: total, currentFile: currentFile,
                                    fileIndex: fileIndex, fileCount: fileCount)
            }
        case "complete":
            accumulator.outcome.completedDetail = self.decodeActiveLibrary(frame["library"] as? [String: Any])
        case "error":
            accumulator.outcome.errorMessage = frame["message"] as? String
        case "indexing-heartbeat":
            break // keepalive only — keeps the per-packet timer alive
        default:
            break
        }
    }

    /// Pushes a throttled progress paint to the UI. Invoked from the delegate
    /// queue; marshals @Published writes to the main thread. (Task #99)
    private func paintIngestProgress(embedded: Int, total: Int, currentFile: String, fileIndex: Int, fileCount: Int) {
        let now = Date()
        if ingestFileStart == nil { ingestFileStart = now }
        let isLast = total > 0 && embedded >= total
        if !isLast && now.timeIntervalSince(lastProgressPaint) < 0.1 { return }
        lastProgressPaint = now

        let elapsed = now.timeIntervalSince(ingestFileStart ?? now)
        let eta = IngestProgressFormatter.etaSeconds(embeddedChunks: embedded, totalChunks: total, elapsedSeconds: elapsed)
        let state = IngestProgressFormatter.state(embeddedChunks: embedded, totalChunks: total, etaSeconds: eta)
        let label = currentFile
        DispatchQueue.main.async {
            self.libraryProgress = state.fraction
            self.libraryProgressDetail = state.detail
            self.libraryStatus = fileCount > 1
                ? "Indexing \(fileIndex + 1) of \(fileCount): \(label)"
                : "Indexing \(label)"
        }
    }

    func pickAndAddWatchedFolder() {
        guard let activeId = activeLibraryId else {
            libraryStatus = "Create or select a library first."
            return
        }
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let folder = panel.url else { return }

        guard let baseUrl = networkApiBaseUrl,
              let url = URL(string: "\(baseUrl)/api/library/\(activeId)/folders") else {
            libraryStatus = "Start Network Mode first."
            return
        }

        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        req.addValue("application/json", forHTTPHeaderField: "Content-Type")
        if let key = apiKeyForLocalApiRequest() { req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["path": folder.path])

        libraryBusy = true
        libraryStatus = "Adding watched folder…"
        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self else { return }
            DispatchQueue.main.async { self.libraryBusy = false }
            if let http = urlResponse as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                let msg = RunnerChatErrorMessage.decode(statusCode: http.statusCode, body: data)
                DispatchQueue.main.async { self.libraryStatus = msg }
                return
            }
            if error != nil {
                DispatchQueue.main.async { self.libraryStatus = "Add folder failed: \(error!.localizedDescription)" }
                return
            }
            DispatchQueue.main.async {
                self.libraryStatus = "Watched folder added."
                self.refreshLibraries()
            }
        }.resume()
    }

    func sweepActiveLibrary() {
        runSseLibraryOp(verb: "Sweep", endpoint: "sweep")
    }

    func rebuildActiveLibrary() {
        runSseLibraryOp(verb: "Rebuild", endpoint: "rebuild")
    }

    /// M14: defense-in-depth recovery for the embedding model. C2 made
    /// PrepApp auto-pull during Download/Finalize so this button should
    /// rarely run, but a user who lands on the runner with the embedder
    /// missing (manual tamper, partial prep, mid-chat delete) has no other
    /// in-app recovery — they would otherwise have to go back to PrepApp
    /// and re-finalize.
    ///
    /// Mirrors `PullEmbeddingModel_Click` in the Windows runner
    /// (`runner/MainWindow.xaml.cs`). The route accepts no body and uses
    /// the embedding model name from the sidecar's PortableConfig, so the
    /// UI side only has to POST and surface success / failure.
    func pullEmbeddingModel() {
        guard let baseUrl = networkApiBaseUrl,
              let url = URL(string: "\(baseUrl)/api/models/embedding/pull") else {
            libraryStatus = "Start Network Mode first."
            return
        }

        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        if let key = apiKeyForLocalApiRequest() {
            req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
        }

        libraryBusy = true
        libraryStatus = "Pulling embedding model…"
        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self else { return }
            DispatchQueue.main.async { self.libraryBusy = false }
            if let error {
                DispatchQueue.main.async {
                    self.libraryStatus = "Pull failed: \(error.localizedDescription)"
                }
                return
            }
            if let http = urlResponse as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                let msg = RunnerChatErrorMessage.decode(statusCode: http.statusCode, body: data)
                DispatchQueue.main.async { self.libraryStatus = msg }
                return
            }
            var modelName = ""
            if let data,
               let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
               let m = obj["model"] as? String {
                modelName = m
            }
            DispatchQueue.main.async {
                self.libraryStatus = modelName.isEmpty
                    ? "Embedding model ready."
                    : "Embedding model ready: \(modelName)"
                // Clear the readiness hint now that the embedder is present.
                self.embeddingModelInstalled = true
            }
        }.resume()
    }

    private func runSseLibraryOp(verb: String, endpoint: String) {
        guard let activeId = activeLibraryId else {
            libraryStatus = "Create or select a library first."
            return
        }
        guard let baseUrl = networkApiBaseUrl,
              let url = URL(string: "\(baseUrl)/api/library/\(activeId)/\(endpoint)") else {
            libraryStatus = "Start Network Mode first."
            return
        }

        var req = URLRequest(url: url)
        req.httpMethod = "POST"
        if let key = apiKeyForLocalApiRequest() { req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        req.addValue("application/x-ndjson", forHTTPHeaderField: "Accept")

        libraryBusy = true
        libraryStatus = "\(verb)ing…"
        libraryProgress = nil
        libraryProgressDetail = ""

        // Task #99: stream sweep/rebuild progress live, same as ingest — these
        // re-embed the whole library and can run for minutes, so the old
        // buffer-until-done path left the UI looking idle the entire time.
        self.ingestFileStart = nil
        self.lastProgressFile = ""
        let accumulator = IngestProgressAccumulator()
        let delegate = NdjsonStreamDelegate(
            onLine: { [weak self] line in
                self?.foldProgressedOpFrame(line, into: accumulator)
            },
            onComplete: { [weak self] statusCode, errorBody, transportError in
                guard let self else { return }
                let session = self.activeIngestSession
                self.activeIngestSession = nil
                self.activeIngestTask = nil
                defer { session?.finishTasksAndInvalidate() }

                let outcome = accumulator.outcome
                DispatchQueue.main.async {
                    self.libraryBusy = false
                    self.libraryProgress = nil
                    self.libraryProgressDetail = ""

                    let nsError = transportError as NSError?
                    if nsError?.domain == NSURLErrorDomain && nsError?.code == NSURLErrorCancelled {
                        return // cancelled by Lock/Stop — state already reset there
                    }
                    if statusCode == 0 {
                        self.libraryStatus = "\(verb) failed: \(transportError?.localizedDescription ?? "request failed")"
                        return
                    }
                    if !(200..<300).contains(statusCode) {
                        self.libraryStatus = RunnerChatErrorMessage.decode(statusCode: statusCode, body: errorBody)
                        return
                    }
                    if let errorMessage = outcome.errorMessage {
                        self.libraryStatus = "\(verb) failed: \(errorMessage)"
                        return
                    }
                    outcome.rejectionLogs.forEach { self.log($0) }
                    if let detail = outcome.completedDetail {
                        self.activeLibrary = detail
                        var status = "\(verb) complete: \(detail.fileCount) file(s)"
                        if outcome.skippedFiles > 0 { status += ", \(outcome.skippedFiles) skipped" }
                        if outcome.failedChunks > 0 { status += " (\(outcome.failedChunks) chunk(s) failed)" }
                        if outcome.rejectedCount > 0 { status += " (\(outcome.rejectedCount) rejected — see logs)" }
                        self.libraryStatus = status
                    } else {
                        self.libraryStatus = "\(verb) finished."
                    }
                    self.refreshLibraries()
                }
            })

        let sessionConfig = URLSessionConfiguration.default
        sessionConfig.timeoutIntervalForRequest = 300
        let session = URLSession(configuration: sessionConfig, delegate: delegate, delegateQueue: nil)
        let task = session.dataTask(with: req)
        self.activeIngestSession = session
        self.activeIngestTask = task
        task.resume()
    }

    /// Folds one streamed sweep/rebuild NDJSON line into `accumulator` and
    /// paints the live bar. Like `foldIngestFrame` but the file position comes
    /// from the frame's totalFiles/completedFiles (one request walks the whole
    /// library) rather than a per-file upload index. (Task #99)
    private func foldProgressedOpFrame(_ line: Data, into accumulator: IngestProgressAccumulator) {
        guard let frame = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              let type = frame["type"] as? String else {
            return
        }
        switch type {
        case "file-rejected":
            accumulator.outcome.rejectedCount += 1
            if let fileName = frame["fileName"] as? String,
               let reason = frame["reason"] as? String {
                accumulator.outcome.rejectionLogs.append("[library] rejected '\(fileName)': \(reason)")
            }
        case "progress":
            if let s = frame["skippedFiles"] as? Int, s > 0 { accumulator.outcome.skippedFiles = s }
            let currentFile = (frame["currentFile"] as? String) ?? ""
            let isTerminalFrame = currentFile.isEmpty
            if isTerminalFrame {
                if let f = frame["failedChunks"] as? Int { accumulator.outcome.failedChunks = f }
            } else {
                // Restart the ETA clock when the op advances to a new file.
                if currentFile != lastProgressFile {
                    lastProgressFile = currentFile
                    ingestFileStart = nil
                }
                let embedded = (frame["embeddedChunks"] as? Int) ?? 0
                let total = (frame["totalChunks"] as? Int) ?? 0
                let completedFiles = (frame["completedFiles"] as? Int) ?? 0
                let totalFiles = (frame["totalFiles"] as? Int) ?? 0
                paintIngestProgress(embedded: embedded, total: total, currentFile: currentFile,
                                    fileIndex: completedFiles, fileCount: totalFiles)
            }
        case "complete":
            accumulator.outcome.completedDetail = self.decodeActiveLibrary(frame["library"] as? [String: Any])
        case "error":
            accumulator.outcome.errorMessage = frame["message"] as? String
        case "indexing-heartbeat":
            break
        default:
            break
        }
    }

    func removeFile(storedRelativePath: String) {
        guard let activeId = activeLibraryId else { return }
        guard let baseUrl = networkApiBaseUrl else { return }
        let encoded = storedRelativePath
            .addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? storedRelativePath
        guard let url = URL(string: "\(baseUrl)/api/library/\(activeId)/files/\(encoded)") else { return }

        var req = URLRequest(url: url)
        req.httpMethod = "DELETE"
        if let key = apiKeyForLocalApiRequest() { req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }

        libraryBusy = true
        libraryStatus = "Removing file…"
        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self else { return }
            DispatchQueue.main.async { self.libraryBusy = false }
            if let http = urlResponse as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                let msg = RunnerChatErrorMessage.decode(statusCode: http.statusCode, body: data)
                DispatchQueue.main.async { self.libraryStatus = msg }
                return
            }
            if error != nil {
                DispatchQueue.main.async { self.libraryStatus = "Remove failed: \(error!.localizedDescription)" }
                return
            }
            DispatchQueue.main.async {
                self.libraryStatus = "File removed."
                self.refreshLibraries()
            }
        }.resume()
    }

    // D2: stop watching a folder. Inverse of pickAndAddWatchedFolder — DELETE the
    // folder by path on the active library, then refresh.
    func removeWatchedFolder(path: String) {
        guard let activeId = activeLibraryId else { return }
        guard let baseUrl = networkApiBaseUrl,
              let url = URL(string: "\(baseUrl)/api/library/\(activeId)/folders") else {
            libraryStatus = "Start Network Mode first."
            return
        }

        var req = URLRequest(url: url)
        req.httpMethod = "DELETE"
        req.addValue("application/json", forHTTPHeaderField: "Content-Type")
        if let key = apiKeyForLocalApiRequest() { req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["path": path])

        libraryBusy = true
        libraryStatus = "Removing watched folder…"
        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self else { return }
            DispatchQueue.main.async { self.libraryBusy = false }
            if let http = urlResponse as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                let msg = RunnerChatErrorMessage.decode(statusCode: http.statusCode, body: data)
                DispatchQueue.main.async { self.libraryStatus = msg }
                return
            }
            if error != nil {
                DispatchQueue.main.async { self.libraryStatus = "Remove folder failed: \(error!.localizedDescription)" }
                return
            }
            DispatchQueue.main.async {
                self.libraryStatus = "Watched folder removed."
                self.refreshLibraries()
            }
        }.resume()
    }

    // D2: rename the active library via PATCH /api/library/{id}.
    func renameLibrary(name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        guard let activeId = activeLibraryId else {
            libraryStatus = "Create or select a library first."
            return
        }
        guard let baseUrl = networkApiBaseUrl,
              let url = URL(string: "\(baseUrl)/api/library/\(activeId)") else {
            libraryStatus = "Start Network Mode first."
            return
        }

        var req = URLRequest(url: url)
        req.httpMethod = "PATCH"
        req.addValue("application/json", forHTTPHeaderField: "Content-Type")
        if let key = apiKeyForLocalApiRequest() { req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
        req.httpBody = try? JSONSerialization.data(withJSONObject: ["name": trimmed])

        libraryBusy = true
        libraryStatus = "Renaming library…"
        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self else { return }
            DispatchQueue.main.async { self.libraryBusy = false }
            if let http = urlResponse as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                let msg = RunnerChatErrorMessage.decode(statusCode: http.statusCode, body: data)
                DispatchQueue.main.async { self.libraryStatus = msg }
                return
            }
            if error != nil {
                DispatchQueue.main.async { self.libraryStatus = "Rename failed: \(error!.localizedDescription)" }
                return
            }
            DispatchQueue.main.async {
                self.libraryStatus = "Library renamed."
                self.refreshLibraries()
            }
        }.resume()
    }

    // D2: delete the active library (folder + index) via DELETE /api/library/{id}.
    // The response carries the refreshed list with the active selection cleared.
    func deleteActiveLibrary() {
        guard let activeId = activeLibraryId else {
            libraryStatus = "Create or select a library first."
            return
        }
        guard let baseUrl = networkApiBaseUrl,
              let url = URL(string: "\(baseUrl)/api/library/\(activeId)") else {
            libraryStatus = "Start Network Mode first."
            return
        }

        var req = URLRequest(url: url)
        req.httpMethod = "DELETE"
        if let key = apiKeyForLocalApiRequest() { req.addValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }

        libraryBusy = true
        libraryStatus = "Deleting library…"
        URLSession.shared.dataTask(with: req) { [weak self] data, urlResponse, error in
            guard let self else { return }
            DispatchQueue.main.async { self.libraryBusy = false }
            if let http = urlResponse as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                let msg = RunnerChatErrorMessage.decode(statusCode: http.statusCode, body: data)
                DispatchQueue.main.async { self.libraryStatus = msg }
                return
            }
            if error != nil {
                DispatchQueue.main.async { self.libraryStatus = "Delete failed: \(error!.localizedDescription)" }
                return
            }
            DispatchQueue.main.async {
                self.persistActiveLibraryId(nil)
                self.refreshLibraries()
                self.libraryStatus = "Library deleted."
            }
        }.resume()
    }

    /// Parsed result of one streamed ingest/sweep/rebuild response. Folded
    /// incrementally by `foldIngestFrame` / `foldProgressedOpFrame` (task #99).
    private struct IngestFileOutcome {
        var rejectedCount = 0
        var skippedFiles = 0
        var failedChunks = 0
        var completedDetail: LibraryDetail? = nil
        var errorMessage: String? = nil
        var rejectionLogs: [String] = []
    }

    private func decodeActiveLibrary(_ obj: [String: Any]?) -> LibraryDetail? {
        guard let obj else { return nil }
        guard let data = try? JSONSerialization.data(withJSONObject: obj) else { return nil }
        return try? JSONDecoder().decode(LibraryDetail.self, from: data)
    }

    /// Persists the new activeDocumentLibraryId via Swift's encrypted/plaintext
    /// save path. Required because the sidecar's IConfigStore is a no-op on
    /// Mac (MAC5/MAC6 plaintext-config invariant) — the sidecar mutates its
    /// in-memory PortableConfig and returns the new ID for us to save here.
    private func persistActiveLibraryId(_ libraryId: String?) {
        saveConfig { current in
            var copy = current
            if let libraryId, !libraryId.isEmpty {
                copy["activeDocumentLibraryId"] = libraryId
            } else {
                copy.removeValue(forKey: "activeDocumentLibraryId")
            }
            return copy
        }
    }

    /// MAC38: reads `<ssdRoot>/mac/tools/ollama/ollama-package-trust.json`
    /// and validates it as the Mac Ollama trust anchor. The attestation is
    /// PrepApp's signed receipt that the bytes on disk matched the upstream
    /// release's vendor-published SHA-256 at staging time. Refuses launch
    /// when the file is missing, malformed, points at a non-allowlisted
    /// host, or carries a malformed digest. Re-hashing the 180MB+ binary on
    /// every launch is too slow; the attestation's existence + integrity is
    /// the gate.
    func evaluateTrustGate(ssdRoot: URL) -> TrustGateResult {
        let attestationURL = ssdRoot
            .appendingPathComponent("mac/tools/ollama")
            .appendingPathComponent(MacOllamaTrust.attestationFileName)

        guard FileManager.default.fileExists(atPath: attestationURL.path) else {
            return .refused("Missing trust attestation. Re-stage the macOS Ollama bundle from PrepApp.")
        }

        guard let data = try? Data(contentsOf: attestationURL) else {
            return .refused("Trust attestation unreadable. Re-stage the macOS Ollama bundle.")
        }

        guard let attestation = try? JSONDecoder().decode(OllamaPackageTrustAttestation.self, from: data) else {
            return .refused("Trust attestation malformed. Re-stage the macOS Ollama bundle.")
        }

        guard let url = URL(string: attestation.Url),
              let scheme = url.scheme?.lowercased(),
              scheme == "https",
              let host = url.host?.lowercased(),
              MacOllamaTrust.allowlistedHosts.contains(host) else {
            return .refused("Trust attestation URL is not from an allowlisted github.com host. Re-stage from PrepApp.")
        }

        let sha = attestation.Sha256.lowercased()
        let isValidSha = sha.count == 64 && sha.allSatisfy { ch in
            ("0"..."9").contains(ch) || ("a"..."f").contains(ch)
        }
        guard isValidSha else {
            return .refused("Trust attestation digest is malformed (not a 64-char SHA-256). Re-stage from PrepApp.")
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

/// MAC36b: NDJSON streaming delegate for `/api/chat/stream`. Holds a
/// weak ref to the view-model so an outliving session (cancelled,
/// not-yet-invalidated) doesn't keep the VM alive. Callbacks dispatch
/// UI mutations to the main queue from inside the VM helpers.
private final class ChatStreamDelegate: NSObject, URLSessionDataDelegate {
    private weak var owner: RunnerViewModel?
    private var buffer = NdjsonFrameBuffer()
    /// M12: populated in `didReceive response`. `0` means we never saw a
    /// response (transport error before headers); 200..<300 means feed
    /// data into the NDJSON parser; non-2xx means buffer for
    /// `RunnerChatErrorMessage.decode`.
    private var httpStatus: Int = 0
    private var errorBuffer = Data()

    init(owner: RunnerViewModel) {
        self.owner = owner
    }

    func urlSession(_ session: URLSession,
                    dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        guard let http = response as? HTTPURLResponse else {
            completionHandler(.cancel)
            return
        }
        httpStatus = http.statusCode
        let disposition = owner?.chatStreamDidReceiveResponse(http) ?? .cancel
        completionHandler(disposition)
    }

    func urlSession(_ session: URLSession,
                    dataTask: URLSessionDataTask,
                    didReceive data: Data) {
        if (200..<300).contains(httpStatus) {
            let lines = buffer.append(data)
            for line in lines where !line.isEmpty {
                owner?.chatStreamDidReceiveLine(line)
            }
        } else {
            errorBuffer.append(data)
        }
    }

    func urlSession(_ session: URLSession,
                    task: URLSessionTask,
                    didCompleteWithError error: Error?) {
        if (200..<300).contains(httpStatus) {
            if let tail = buffer.flush(), !tail.isEmpty {
                owner?.chatStreamDidReceiveLine(tail)
            }
            owner?.chatStreamDidComplete(error: error)
        } else if httpStatus > 0 {
            // Non-2xx with headers received: surface the buffered body via
            // the structured error path. Skip chatStreamDidComplete's
            // transport-error handling so we don't double-report.
            owner?.chatStreamDidReceiveErrorBody(statusCode: httpStatus, body: errorBuffer)
            owner?.chatStreamDidComplete(error: nil)
        } else {
            // Transport error before any response landed.
            owner?.chatStreamDidComplete(error: error)
        }
    }
}

/// Task #99: generic NDJSON streaming delegate for the document ingest /
/// sweep / rebuild responses. Mirrors `ChatStreamDelegate` (the macOS 11
/// baseline rules out `URLSession.bytes(for:)`), but forwards each frame and
/// the completion through closures so the per-operation aggregation can live
/// at the call site. 2xx bytes feed the NDJSON line splitter; non-2xx bytes
/// are buffered so the caller can decode a structured error body.
private final class NdjsonStreamDelegate: NSObject, URLSessionDataDelegate {
    private var buffer = NdjsonFrameBuffer()
    private var httpStatus = 0
    private var errorBuffer = Data()
    private let onLine: (Data) -> Void
    private let onComplete: (_ statusCode: Int, _ errorBody: Data, _ transportError: Error?) -> Void

    init(onLine: @escaping (Data) -> Void,
         onComplete: @escaping (Int, Data, Error?) -> Void) {
        self.onLine = onLine
        self.onComplete = onComplete
    }

    func urlSession(_ session: URLSession,
                    dataTask: URLSessionDataTask,
                    didReceive response: URLResponse,
                    completionHandler: @escaping (URLSession.ResponseDisposition) -> Void) {
        httpStatus = (response as? HTTPURLResponse)?.statusCode ?? 0
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession,
                    dataTask: URLSessionDataTask,
                    didReceive data: Data) {
        if (200..<300).contains(httpStatus) {
            for line in buffer.append(data) where !line.isEmpty {
                onLine(line)
            }
        } else {
            errorBuffer.append(data)
        }
    }

    func urlSession(_ session: URLSession,
                    task: URLSessionTask,
                    didCompleteWithError error: Error?) {
        if (200..<300).contains(httpStatus), let tail = buffer.flush(), !tail.isEmpty {
            onLine(tail)
        }
        onComplete(httpStatus, errorBuffer, error)
    }
}

struct ContentView: View {
    @StateObject var vm = RunnerViewModel()

    var body: some View {
        // The page outgrew a 640pt window once the Voice + Documents sections
        // expand, so the whole thing scrolls. maxWidth: .infinity (below) keeps
        // the editors full-width — a ScrollView otherwise collapses its content
        // to intrinsic width and the layout would hug the left edge.
        ScrollView {
        VStack(alignment: .leading, spacing: 10) {
            Text("Free AI SSD macOS Runner").font(.title2)
            Text(vm.status)
            HStack {
                Button("Select SSD") { vm.pickSsdRoot() }
                // MAC39: conditional verb based on encryption state.
                //   Encrypted SSD (unlocked): Lock — tears down chat AND
                //     zeroizes unlock material. Same as the MAC30 default.
                //   Plaintext SSD: Start/Stop — toggles the chat host
                //     explicitly. There's no key to zeroize, so "Lock" was
                //     just a misleading way to stop the chat stack and
                //     leave the user without a way to bring it back without
                //     re-selecting the SSD.
                if vm.isEncryptedSsd {
                    if !vm.isEncryptedLocked {
                        Button("Lock") { vm.lockSession(reason: "Manual lock") }
                    }
                } else {
                    if vm.networkApiBaseUrl != nil {
                        Button("Stop") { vm.stopChatHost(reason: "Manual stop") }
                    } else {
                        Button("Start") { vm.startChatHost() }
                    }
                }
            }
            Picker("Model", selection: $vm.selectedModel) {
                ForEach(vm.modelNames, id: \.self) { Text($0).tag($0) }
            }
            TextEditor(text: $vm.prompt).frame(height: 120)
            // MAC36c: spinner + disabled state while a streaming chat is
            // in flight. Disabled also gates on missing model/prompt so
            // the button can't kick off a no-op request.
            HStack(spacing: 8) {
                Button(action: { vm.sendPrompt() }) {
                    HStack(spacing: 6) {
                        if vm.isSending { ProgressView().controlSize(.small) }
                        Text("Send")
                    }
                }
                // Task #99: also disabled while documents are indexing — querying a
                // half-built index answers ungrounded.
                .disabled(vm.isSending
                          || vm.libraryBusy
                          || vm.selectedModel.isEmpty
                          || vm.prompt.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                // Task #94: voice input (native SFSpeechRecognizer, on-device).
                // Tap to dictate, tap again to stop → transcribe → fill the
                // prompt (and auto-send when enabled). Permission is requested on
                // first use. Independent of TTS, so it's always offered while the
                // chat host is up.
                Button(action: { vm.toggleVoiceInput() }) {
                    HStack(spacing: 4) {
                        Image(systemName: vm.isListening ? "mic.fill" : "mic")
                        Text(vm.isListening ? "Stop" : "Speak")
                    }
                }
                .disabled(vm.networkApiBaseUrl == nil || vm.isSending || vm.libraryBusy)
                .foregroundColor(vm.isListening ? .red : nil)
            }
            if let voiceError = vm.voiceError {
                Text(voiceError)
                    .font(.callout)
                    .foregroundColor(.red)
            }
            if vm.libraryBusy {
                Text("Documents are indexing — chat is paused until ready.")
                    .font(.callout)
                    .foregroundColor(.orange)
            }
            TextEditor(text: $vm.response).frame(height: 200)
            if let chatError = vm.chatError {
                // M12: red banner for X13 ChatResult.Failure / transport
                // error / non-2xx HTTP body. Distinct from the orange RAG
                // warning so the user reads one signal per outcome.
                Text(chatError)
                    .font(.callout)
                    .foregroundColor(.red)
            }
            if let warning = vm.ragWarning {
                Text(warning)
                    .font(.callout)
                    .foregroundColor(.orange)
            }
            if !vm.responseSources.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Sources")
                        .font(.headline)
                    ForEach(vm.responseSources, id: \.self) { source in
                        Text(source)
                            .font(.callout)
                            .foregroundColor(.secondary)
                    }
                }
            }

            // MAC34: the chat host is auto-spawned at unlock time and bound
            // to 127.0.0.1 by default. This toggle now only controls whether
            // the same host is also reachable from the LAN — when ON, the
            // sidecar restarts bound to `networkBindAddress` from config
            // (which the user must edit in JSON to actually expose; the
            // default is still 127.0.0.1, so toggling ON with the default
            // is a no-op for now).
            Divider()
            HStack {
                Toggle("Expose API on LAN", isOn: Binding(
                    get: { vm.networkModeEnabled },
                    set: { newValue in vm.setExposeApiOnLan(newValue) }
                ))
                .disabled(vm.isEncryptedLocked)
                Spacer()
                Text(vm.networkApiStatus)
                    .font(.callout)
                    .foregroundColor(.secondary)
            }

            // Task #35: model-parameter controls (parity with the WPF runner's
            // System-tab "Model parameters" card), including the Thinking control.
            Divider()
            ModelParametersSection(vm: vm)

            // Task #94: native voice — TTS (speak responses) + STT settings.
            Divider()
            VoiceSection(vm: vm)

            // MAC8: Documents (library management). All ops go through the
            // sidecar's /api/library/* endpoints. Post-MAC34 the sidecar is
            // always running once the SSD is unlocked (loopback even when LAN
            // exposure is off), so this section is usable at unlock — the
            // Network Mode toggle only changes the sidecar's bind address. The
            // `apiUp` check below just confirms the sidecar has reported ready;
            // when it hasn't, surface the real reason (locked / starting /
            // crashed) rather than telling the user to flip a toggle.
            Divider()
            DocumentsSection(vm: vm)
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(minWidth: 720, minHeight: 640)
        .sheet(isPresented: $vm.unlockDialogPresented) { UnlockSheet(vm: vm) }
        .sheet(isPresented: $vm.createLibraryDialogPresented) { CreateLibrarySheet(vm: vm) }
        .sheet(isPresented: $vm.renameLibraryDialogPresented) { RenameLibrarySheet(vm: vm) }
        // Older Alert API, not confirmationDialog/Button(role:) — those are macOS 12+
        // and the mac-runner targets the macOS 11 (MAC1) baseline.
        .alert(isPresented: $vm.deleteLibraryConfirmPresented) {
            Alert(
                title: Text("Delete library"),
                message: Text("Delete this library and all its indexed files? This cannot be undone."),
                primaryButton: .destructive(Text("Delete")) { vm.deleteActiveLibrary() },
                secondaryButton: .cancel()
            )
        }
    }
}

struct ModelParametersSection: View {
    @ObservedObject var vm: RunnerViewModel

    private var contextLabel: String {
        vm.modelContextWindow > 0 ? "\(Int(vm.modelContextWindow))" : "default"
    }
    private var temperatureLabel: String {
        vm.modelTemperature >= 0 ? String(format: "%.2f", vm.modelTemperature) : "default"
    }
    private var topPLabel: String {
        vm.modelTopP >= 0 ? String(format: "%.2f", vm.modelTopP) : "default"
    }
    private var maxOutputLabel: String {
        vm.modelMaxOutputTokens > 0 ? "\(Int(vm.modelMaxOutputTokens))" : "unlimited"
    }

    var body: some View {
        DisclosureGroup("Model parameters") {
            VStack(alignment: .leading, spacing: 8) {
                Text("Tune how the model responds. Far-left slider positions use the model's built-in default.")
                    .font(.callout)
                    .foregroundColor(.secondary)

                paramRow(title: "Context", label: contextLabel) {
                    Slider(value: $vm.modelContextWindow, in: 0...32768, step: 512,
                           onEditingChanged: { editing in if !editing { vm.commitModelParams() } })
                }
                paramRow(title: "Temperature", label: temperatureLabel) {
                    Slider(value: $vm.modelTemperature, in: RunnerViewModel.temperatureSliderMin...2.0, step: 0.05,
                           onEditingChanged: { editing in if !editing { vm.commitModelParams() } })
                }
                paramRow(title: "Top P", label: topPLabel) {
                    Slider(value: $vm.modelTopP, in: RunnerViewModel.topPSliderMin...1.0, step: 0.05,
                           onEditingChanged: { editing in if !editing { vm.commitModelParams() } })
                }
                paramRow(title: "Max output", label: maxOutputLabel) {
                    Slider(value: $vm.modelMaxOutputTokens, in: RunnerViewModel.maxOutputSliderMin...8192, step: 128,
                           onEditingChanged: { editing in if !editing { vm.commitModelParams() } })
                }

                HStack {
                    Text("Thinking").frame(width: 90, alignment: .leading)
                    Picker("", selection: Binding(
                        get: { vm.modelThinkMode },
                        set: { vm.setThinkMode($0) }
                    )) {
                        Text("Default").tag("")
                        Text("Off (no reasoning)").tag("off")
                        Text("Low").tag("low")
                        Text("Medium").tag("medium")
                        Text("High").tag("high")
                    }
                    .pickerStyle(MenuPickerStyle())
                    .labelsHidden()
                    Spacer()
                }
                Text("Off disables reasoning for thinking models that loop; only works on models that declare thinking support — others use Max output.")
                    .font(.caption)
                    .foregroundColor(.secondary)

                Button("Reset to model defaults") { vm.resetModelParams() }
                    .padding(.top, 2)
            }
            .padding(.top, 6)
            .disabled(vm.isEncryptedLocked)
        }
    }

    @ViewBuilder
    private func paramRow<Content: View>(
        title: String, label: String, @ViewBuilder slider: () -> Content
    ) -> some View {
        HStack {
            Text(title).frame(width: 90, alignment: .leading)
            slider()
            Text(label).frame(width: 70, alignment: .trailing)
                .foregroundColor(.secondary)
        }
    }
}

struct VoiceSection: View {
    @ObservedObject var vm: RunnerViewModel
    @Environment(\.openURL) private var openURL

    /// Deep-link to the Spoken Content pane where Premium/Enhanced voices are
    /// downloaded. Opens Accessibility settings on newer macOS; harmless if the
    /// exact sub-pane has moved between releases.
    private static let voiceDownloadSettingsURL =
        URL(string: "x-apple.systempreferences:com.apple.preference.universalaccess?TextToSpeech")

    private var rateLabel: String {
        let r = Int(vm.ttsRate)
        return r == 0 ? "normal" : (r > 0 ? "+\(r)" : "\(r)")
    }
    private var volumeLabel: String { "\(Int(vm.ttsVolume))%" }

    var body: some View {
        DisclosureGroup("Voice") {
            VStack(alignment: .leading, spacing: 8) {
                Text("Native macOS voice — responses are spoken with AVSpeechSynthesizer and dictation runs on-device, so no audio leaves this Mac.")
                    .font(.callout)
                    .foregroundColor(.secondary)

                // ── Spoken responses (TTS) ──
                Toggle("Speak responses aloud", isOn: Binding(
                    get: { vm.ttsEnabled },
                    set: { vm.setTtsEnabled($0) }
                ))

                HStack {
                    Text("Voice").frame(width: 90, alignment: .leading)
                    Picker("", selection: Binding(
                        get: { vm.ttsVoiceIdentifier },
                        set: { vm.setTtsVoice($0) }
                    )) {
                        Text("System default").tag("")
                        ForEach(vm.availableTtsVoices) { voice in
                            Text(voice.label).tag(voice.id)
                        }
                    }
                    .pickerStyle(MenuPickerStyle())
                    .labelsHidden()
                    Spacer()
                }

                // ⭐ marks Premium/Enhanced neural voices. If the picker is all
                // "Default" compact voices it sounds robotic — point the user at
                // the free downloads. (Apple reserves the actual Siri voice for
                // the system, so a downloaded neural voice is the nicest we can
                // wire into AVSpeechSynthesizer.)
                HStack(spacing: 6) {
                    Text("⭐ = neural voice. For the nicest sound, download a Premium voice in System Settings.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    if let url = Self.voiceDownloadSettingsURL {
                        Button("Get voices…") { openURL(url) }
                            .font(.caption)
                    }
                    Spacer()
                }

                paramRow(title: "Rate", label: rateLabel) {
                    Slider(value: $vm.ttsRate, in: -10...10, step: 1,
                           onEditingChanged: { editing in if !editing { vm.commitTtsRate() } })
                }
                paramRow(title: "Volume", label: volumeLabel) {
                    Slider(value: $vm.ttsVolume, in: 0...100, step: 5,
                           onEditingChanged: { editing in if !editing { vm.commitTtsVolume() } })
                }

                Button("Test voice") { vm.testTtsVoice() }
                    .padding(.top, 2)

                Divider().padding(.vertical, 4)

                // ── Voice input (STT) ──
                Text("Voice input")
                    .font(.headline)
                Text("Use the Speak button by the prompt to dictate a question.")
                    .font(.caption)
                    .foregroundColor(.secondary)
                Toggle("Send automatically after dictation", isOn: Binding(
                    get: { vm.autoSendVoiceInput },
                    set: { vm.setAutoSendVoiceInput($0) }
                ))
            }
            .padding(.top, 6)
            .disabled(vm.isEncryptedLocked)
        }
    }

    @ViewBuilder
    private func paramRow<Content: View>(
        title: String, label: String, @ViewBuilder slider: () -> Content
    ) -> some View {
        HStack {
            Text(title).frame(width: 90, alignment: .leading)
            slider()
            Text(label).frame(width: 70, alignment: .trailing)
                .foregroundColor(.secondary)
        }
    }
}

struct DocumentsSection: View {
    @ObservedObject var vm: RunnerViewModel

    private var apiUp: Bool { vm.networkApiBaseUrl != nil }

    /// Message for the state where the library UI can't be shown yet. Post-MAC34
    /// the sidecar comes up at unlock regardless of the Network Mode toggle, so
    /// the only legitimate reasons `apiUp` is false are: the session is locked,
    /// or the sidecar hasn't reported ready (still starting, or crashed). Word
    /// the hint to the actual cause instead of the stale "turn on Network Mode".
    private var documentsUnavailableHint: String {
        if vm.isEncryptedLocked {
            return "Unlock the SSD to manage documents."
        }
        return vm.networkApiStatus.isEmpty
            ? "Starting local services…"
            : vm.networkApiStatus
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Documents").font(.headline)
                Spacer()
                if !vm.libraryStatus.isEmpty {
                    Text(vm.libraryStatus)
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            // Task #99: live indexing indicator. A determinate bar once the
            // chunk total is known, otherwise an indeterminate bar (e.g. while
            // OCR runs before embedding). Makes "the system is still working,
            // not idle" unmistakable so the user doesn't query a half-built
            // index and mistake the ungrounded answer for a bug.
            if vm.libraryBusy {
                VStack(alignment: .leading, spacing: 2) {
                    if let fraction = vm.libraryProgress {
                        ProgressView(value: fraction)
                    } else {
                        ProgressView()
                            .progressViewStyle(.linear)
                    }
                    if !vm.libraryProgressDetail.isEmpty {
                        Text(vm.libraryProgressDetail)
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                }
                .padding(.vertical, 2)
            }

            // OCR opt-in (parity with the Windows runner's Reference Documents
            // card). Disabled-with-hint until the Tesseract bundle is staged on
            // the SSD — flipping it on without the engine present would be a
            // no-op at ingest. When staged, the toggle persists `ocrEnabled`.
            VStack(alignment: .leading, spacing: 2) {
                Toggle("Extract text from PDF images (OCR)", isOn: Binding(
                    get: { vm.ocrEnabled },
                    set: { newValue in vm.setOcrEnabled(newValue) }
                ))
                .disabled(vm.isEncryptedLocked || !vm.ocrBundleStaged)
                if !vm.ocrBundleStaged {
                    Text("Re-run the prep tool with “Enable PDF image OCR” to stage the OCR engine onto this drive.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }

            if !apiUp {
                Text(documentsUnavailableHint)
                    .font(.callout)
                    .foregroundColor(.secondary)
            } else {
                HStack {
                    Picker("Library", selection: Binding(
                        get: { vm.activeLibraryId ?? "" },
                        set: { newValue in
                            vm.setActiveLibrary(libraryId: newValue.isEmpty ? nil : newValue)
                        }
                    )) {
                        Text("None").tag("")
                        ForEach(vm.libraries, id: \.id) { lib in
                            Text(lib.name).tag(lib.id)
                        }
                    }
                    .frame(maxWidth: 280)
                    Button("Create") {
                        vm.createLibraryName = ""
                        vm.createLibraryDialogPresented = true
                    }
                    .disabled(vm.libraryBusy)
                    Button("Rename") {
                        vm.renameLibraryName = vm.activeLibrary?.name ?? ""
                        vm.renameLibraryDialogPresented = true
                    }
                    .disabled(vm.libraryBusy || vm.activeLibraryId == nil)
                    Button("Delete") {
                        vm.deleteLibraryConfirmPresented = true
                    }
                    .disabled(vm.libraryBusy || vm.activeLibraryId == nil)
                    Spacer()
                }
                HStack {
                    Button("Add Files") { vm.pickAndIngestFiles() }
                        .disabled(vm.libraryBusy || vm.activeLibraryId == nil)
                    Button("Add Folder") { vm.pickAndAddWatchedFolder() }
                        .disabled(vm.libraryBusy || vm.activeLibraryId == nil)
                    Button("Sweep") { vm.sweepActiveLibrary() }
                        .disabled(vm.libraryBusy || vm.activeLibraryId == nil)
                    Button("Rebuild") { vm.rebuildActiveLibrary() }
                        .disabled(vm.libraryBusy || vm.activeLibraryId == nil)
                    // M14: parity with WPF runner's Index-tools button.
                    // Not gated on activeLibraryId — the embedding model is
                    // a global resource on the running Ollama daemon, not
                    // a per-library asset.
                    Button("Pull embedding model") { vm.pullEmbeddingModel() }
                        .disabled(vm.libraryBusy)
                    Spacer()
                }

                // Workstream C: proactive embedding-model readiness, parity with the
                // WPF runner. The "Pull embedding model" button above is the action.
                if !vm.embeddingModelInstalled {
                    Text("⚠ The embedding model isn't installed yet — use \u{201C}Pull embedding model\u{201D} above before adding files.")
                        .font(.callout)
                        .foregroundColor(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }

                // Workstream C: first-run zero-state guidance, parity with the WPF
                // NoLibraryEmptyState numbered flow.
                if vm.libraries.isEmpty {
                    VStack(alignment: .leading, spacing: 4) {
                        Text("Get started:").font(.callout).bold()
                        Text("1. Create a library")
                        Text("2. Add files (PDF, TXT, Markdown, JSON, CSV)")
                        Text("3. Ask grounded questions")
                    }
                    .font(.callout)
                    .foregroundColor(.secondary)
                    .padding(.top, 4)
                }

                if let detail = vm.activeLibrary, !detail.files.isEmpty {
                    Text("Files (\(detail.fileCount))")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    ScrollView {
                        VStack(alignment: .leading, spacing: 2) {
                            ForEach(detail.files, id: \.storedRelativePath) { file in
                                HStack {
                                    // Workstream D1: size + imported date beneath the name,
                                    // parity with the WPF runner's file rows.
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(file.fileName).font(.callout)
                                        Text("\(LibraryFileFormatting.size(file.sizeBytes)) · \(LibraryFileFormatting.shortDate(file.importedAtUtc))")
                                            .font(.caption)
                                            .foregroundColor(.secondary)
                                    }
                                    Spacer()
                                    Button("Remove") { vm.removeFile(storedRelativePath: file.storedRelativePath) }
                                        .buttonStyle(.borderless)
                                        .font(.caption)
                                        .disabled(vm.libraryBusy)
                                }
                            }
                        }
                    }
                    .frame(maxHeight: 120)
                }

                // Workstream D1: watched folders, parity with the WPF runner.
                // D2: each row gains an inline Remove button.
                if let detail = vm.activeLibrary, !detail.watchedFolders.isEmpty {
                    Text("Watched folders")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    VStack(alignment: .leading, spacing: 1) {
                        ForEach(detail.watchedFolders, id: \.self) { folder in
                            HStack {
                                Text(folder)
                                    .font(.caption)
                                    .foregroundColor(.secondary)
                                    .lineLimit(1)
                                    .truncationMode(.middle)
                                    .help(folder)
                                Spacer()
                                Button("Remove") { vm.removeWatchedFolder(path: folder) }
                                    .buttonStyle(.borderless)
                                    .font(.caption)
                                    .disabled(vm.libraryBusy)
                            }
                        }
                    }
                }
            }
        }
    }
}

struct CreateLibrarySheet: View {
    @ObservedObject var vm: RunnerViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("New library").font(.title3)
            TextField("Library name", text: $vm.createLibraryName)
                .textFieldStyle(.roundedBorder)
            HStack {
                Spacer()
                Button("Cancel") {
                    vm.createLibraryDialogPresented = false
                    vm.createLibraryName = ""
                }
                Button("Create") {
                    let name = vm.createLibraryName
                    vm.createLibraryDialogPresented = false
                    vm.createLibraryName = ""
                    vm.createLibrary(name: name)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(vm.createLibraryName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(20)
        .frame(minWidth: 360)
    }
}

struct RenameLibrarySheet: View {
    @ObservedObject var vm: RunnerViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Rename library").font(.title3)
            TextField("Library name", text: $vm.renameLibraryName)
                .textFieldStyle(.roundedBorder)
            HStack {
                Spacer()
                Button("Cancel") {
                    vm.renameLibraryDialogPresented = false
                    vm.renameLibraryName = ""
                }
                Button("Rename") {
                    let name = vm.renameLibraryName
                    vm.renameLibraryDialogPresented = false
                    vm.renameLibraryName = ""
                    vm.renameLibrary(name: name)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(vm.renameLibraryName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(20)
        .frame(minWidth: 360)
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
