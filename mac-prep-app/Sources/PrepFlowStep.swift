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

    /// Sidecar's readiness command runs and we render the result list.
    case readiness

    /// Done — drive is ready, instruct the user to launch Runner.app.
    case done

    /// Terminal failure state — surfaces an error message and an option to
    /// retry from the appropriate previous step.
    case failed(message: String)
}
