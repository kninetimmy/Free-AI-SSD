import Foundation

// MARK: - Flow step state machine
//
// MAC17 PrepApp drives a linear, well-defined flow. Each step has a single
// SwiftUI view (rendered by ContentView in main.swift) and a single set of
// transitions. Keeping the steps in one enum makes the flow auditable —
// reviewers can read the cases top-to-bottom and see the entire user
// journey without chasing through view files.

enum PrepFlowStep: Equatable {
    /// Initial screen. Explains what the app does and warns the user that
    /// the next step lists candidate disks for destructive operations.
    case welcome

    /// User picks a disk from the list of external candidates and chooses
    /// Mac-only-vs-cross-platform target. (Both target shapes use exFAT in
    /// the MAC17 MVP per the 2026-05-05 prep-parity decision.)
    case driveSelection

    /// C6 Stage 3: user picked an already-configured drive and clicked
    /// Manage models from the contextual banner. Surfaces installed
    /// models + Add/Remove without re-running the destructive format
    /// flow. Encrypted drives render a read-only variant (Add/Remove
    /// disabled until C7 lands unlock UX).
    case manageModels

    /// Native NSAlert sheet the user must dismiss with "Erase" before any
    /// destructive call. We do this in a dedicated step (rather than an
    /// inline alert) so a confirmation dismissal doesn't accidentally
    /// chain into the format command via a stale @State.
    case eraseConfirmation

    /// `diskutil eraseDisk` running. Stdout/stderr forwarded to the in-app
    /// log view. On success → `.staging`; on failure → `.failed`.
    case formatting

    /// SSD layout is laid down + Mac Runner.app + Mac Ollama + prereqs are
    /// staged via the sidecar.
    case staging

    /// User chooses whether to enable encrypted-config and provides a
    /// passphrase. SsdEncryption.swift handles the write.
    case encryptionSetup

    /// Pull the starter model(s) via the sidecar's pull-model command.
    /// Depends on a running Mac Ollama (which the user starts manually for
    /// MVP — we surface a clear instruction; future work spawns it from
    /// the prep app itself).
    case modelPull

    /// MAC31a: pull was cancelled mid-batch. UI shows the last
    /// progress snapshot for the cancelled tag and offers Retry /
    /// Skip / Start over. Without this, `pullStarterModels` would
    /// fall through to `.readiness` on cancel and the user would
    /// have no surface to resume the partially-downloaded model.
    case modelPullPaused(tag: String, progressSnapshot: String?)

    /// M15: one or more pull commands failed but the overall prep flow
    /// remains recoverable. Keep the log visible and let the user retry
    /// only the failed tags or continue to readiness.
    case modelPullFailed(tags: [String])

    /// Sidecar's readiness command runs and we render the result list.
    case readiness

    /// Done — drive is ready, instruct the user to launch Runner.app.
    case done

    /// Terminal failure state — surfaces an error message and an option to
    /// retry from the appropriate previous step.
    case failed(message: String)
}

// MARK: - Web-UI access mode (#338 parity)
//
// Mirrors the Windows `WebUiAccessMode` (shared/Models/PrepModels.cs). Surfaced
// on the PrepApp's reframed "How will you use this drive?" step so the
// encryption decision is framed by the user's actual goal rather than a bare
// checkbox. `.deviceOnly` keeps everything on this Mac (encryption optional);
// `.lan` exposes the Runner LAN API + web chat UI to other devices, which
// requires an encrypted config because the network API key is never written in
// plaintext (PlaintextConfigWriter clears it; EncryptedConfigWriter seals it).
//
// The pure transition + gating helpers below live here — not on the
// @MainActor PrepViewModel — so the parity-pin tests can exercise the rules
// without constructing the view-model, exactly like applyStarterModelFilters
// in StarterCatalogTypes.swift. This file is compiled into both the test
// binary and the app (see the swiftc file lists in .github/workflows/build.yml).

// Hashable (refines Equatable) so it can serve as a SwiftUI Picker `.tag`.
enum WebUiAccessMode: Hashable {
    case deviceOnly
    case lan
}

/// Pure result of selecting an access mode. LAN forces encryption on (its API
/// key is only ever stored encrypted); a cancelled LAN confirm or a
/// device-only selection preserves whatever encryption choice the user already
/// made. Mirrors the state half of the Windows `RequestLanAccess` /
/// `SelectDeviceOnlyAccess`.
///
/// - Parameters:
///   - target: the mode the user clicked.
///   - lanConfirmed: result of the encryption-required confirm (ignored unless
///     `target == .lan`).
///   - currentEncryption: the user's current `enableEncryption` value, preserved
///     for device-only.
func resolveAccessMode(
    selecting target: WebUiAccessMode,
    lanConfirmed: Bool,
    currentEncryption: Bool
) -> (mode: WebUiAccessMode, enableEncryption: Bool) {
    switch target {
    case .deviceOnly:
        return (.deviceOnly, currentEncryption)
    case .lan:
        // Cancelled confirm leaves everything unchanged (device-only).
        return lanConfirmed ? (.lan, true) : (.deviceOnly, currentEncryption)
    }
}

/// The "Encrypt SSD config" toggle is user-editable only in device-only mode;
/// LAN locks it on. Mirrors the C# `IsEncryptionToggleEnabled`.
func accessEncryptionToggleEnabled(for mode: WebUiAccessMode) -> Bool {
    mode == .deviceOnly
}

/// Done-step gate: surface the generated API key only when LAN was chosen and
/// finalize produced a non-empty key. Mirrors the C# `ShowFinalizedApiKey`.
func accessShowFinalizedApiKey(mode: WebUiAccessMode, key: String?) -> Bool {
    mode == .lan && !(key ?? "").isEmpty
}

// MARK: - Optional staging commands (#91 / task #92)
//
// The staging step always runs the core arms (stage-runner / stage-ollama /
// stage-prereqs); the two opt-in bundles — Piper neural TTS and the Tesseract
// OCR fast-follow (#342) — each emit one extra `stage-*` arm only when their
// toggle is set. PrepViewModel.runStaging() iterates the result after the core
// arms; each arm is fail-soft (the sidecar catches its own exceptions and
// returns ok=false, so a bundle download failure lands in the log without
// blocking finalize). The mapping lives here — not on the @MainActor
// PrepViewModel — so the parity-pin tests can exercise it without constructing
// the view-model, exactly like the #338 access-mode helpers above.

/// Ordered list of the optional `stage-*` sidecar commands the staging step
/// emits for the given opt-in toggles. Empty when nothing is opted into. Order
/// is load-bearing: Piper before Tesseract, mirroring the documented
/// "stage-tesseract after stage-piper" sequence.
func optionalStagingCommands(installPiper: Bool, installOcr: Bool) -> [String] {
    var commands: [String] = []
    if installPiper { commands.append("stage-piper") }
    if installOcr { commands.append("stage-tesseract") }
    return commands
}
