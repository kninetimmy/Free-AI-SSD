import SwiftUI

// MARK: - App entry
//
// MAC17 Mac PrepApp. SwiftUI scene + flow views.
//
// Per the 2026-05-06 Mac UI design language decision, every view here
// uses native macOS controls — stock Buttons, Forms, Lists, Toggles,
// SecureField — with brand tinting only via the AccentColor / Status
// helpers in BrandColors.swift. No custom button styles, no font
// overrides, no hardcoded backgrounds. Destructive confirmations
// (NSAlert in PrepViewModel.confirmEraseAndProceed) stay system-default.

@main
struct PrepApp: App {
    @StateObject private var vm = PrepViewModel()

    var body: some Scene {
        WindowGroup("Free AI SSD — Prep") {
            ContentView(vm: vm)
                .frame(minWidth: 720, minHeight: 540)
                .accentColor(.brandAccentCyan)
        }
    }
}

struct ContentView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            Group {
                switch vm.currentStep {
                case .welcome:           WelcomeStepView(vm: vm)
                case .driveSelection:    DriveSelectionStepView(vm: vm)
                case .eraseConfirmation: EraseConfirmationStepView(vm: vm)
                case .formatting:        ProgressLogStepView(vm: vm, title: "Formatting drive…")
                case .staging:           ProgressLogStepView(vm: vm, title: "Staging artifacts…")
                case .encryptionSetup:   EncryptionSetupStepView(vm: vm)
                case .modelPull:         ModelPullStepView(vm: vm)
                case .modelPullPaused(let tag, let snapshot):
                    ModelPullPausedStepView(vm: vm, tag: tag, snapshot: snapshot)
                case .modelPullFailed(let tags):
                    ModelPullFailedStepView(vm: vm, tags: tags)
                case .manageModels:      ManageModelsStepView(vm: vm)
                case .readiness:         ProgressLogStepView(vm: vm, title: "Running readiness checks…")
                case .done:              DoneStepView(vm: vm)
                case .failed(let msg):   FailedStepView(message: msg, vm: vm)
                }
            }
            .padding(20)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private var header: some View {
        HStack {
            Text("Free AI SSD — Prep")
                .font(.title2)
                .bold()
            Spacer()
            Text(stepLabel(vm.currentStep))
                .font(.subheadline)
                .foregroundColor(.secondary)
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 12)
    }

    private func stepLabel(_ step: PrepFlowStep) -> String {
        switch step {
        case .welcome:           return "Welcome"
        case .driveSelection:    return "1 / 6 — Choose drive"
        case .eraseConfirmation: return "2 / 6 — Confirm erase"
        case .formatting:        return "2 / 6 — Formatting"
        case .staging:           return "3 / 6 — Staging"
        case .encryptionSetup:   return "4 / 6 — Encryption"
        case .modelPull:         return "5 / 6 — Models"
        case .modelPullPaused:   return "5 / 6 — Pull paused"
        case .modelPullFailed:   return "5 / 6 — Pull failed"
        case .manageModels:      return "Manage models"
        case .readiness:         return "6 / 6 — Readiness"
        case .done:              return "Done"
        case .failed:            return "Failed"
        }
    }
}

// MARK: - Welcome

struct WelcomeStepView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Prepare a drive for Free AI SSD")
                .font(.title)
                .bold()
            Text("""
            This app sets up an external SSD with everything needed to run a \
            local AI assistant offline: Ollama, the Free AI Runner app, and \
            an encrypted configuration store.

            On the next screen you'll pick a drive. The selected drive will \
            be erased — back up first if needed.
            """)
            .foregroundColor(.secondary)
            .lineLimit(nil)
            .fixedSize(horizontal: false, vertical: true)

            Spacer()

            HStack {
                Spacer()
                Button("Get started") { vm.startFlow() }
                    .keyboardShortcut(.defaultAction)
                    .controlSize(.large)
            }
        }
    }
}

// MARK: - Drive selection

struct DriveSelectionStepView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Pick a drive to prepare")
                    .font(.headline)
                Spacer()
                Button("Refresh") {
                    Task { await vm.refreshCandidates() }
                }
            }
            Text(vm.statusMessage)
                .font(.subheadline)
                .foregroundColor(.secondary)

            List(vm.candidates, selection: Binding(
                get: { vm.selectedCandidate?.id },
                set: { newId in
                    vm.selectedCandidate = vm.candidates.first { $0.id == newId }
                }
            )) { drive in
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(drive.displayName).font(.body)
                        Text("\(drive.identifier) — \(drive.sizeDisplay)")
                            .font(.caption)
                            .foregroundColor(.secondary)
                    }
                    Spacer()
                    if drive.removable {
                        Text("External")
                            .font(.caption2)
                            .padding(.horizontal, 6).padding(.vertical, 2)
                            .background(Color.brandStatusInfo.opacity(0.15))
                            .foregroundColor(Color.brandStatusInfo)
                            .clipShape(RoundedRectangle(cornerRadius: 4))
                    }
                }
                .tag(drive.id)
            }
            .frame(minHeight: 180)

            // C6 Stage 3: contextual banner appears when the selected drive
            // already carries our config marker. Offers Manage models /
            // Start over and disables the fresh-format inputs below.
            if vm.showAlreadyConfiguredBanner {
                alreadyConfiguredBanner
            }

            Form {
                TextField("Volume label", text: $vm.volumeLabel)
                Toggle("Also prepare for Windows (cross-platform)", isOn: $vm.prepareForWindowsToo)
            }
            .disabled(!vm.canInitiateFreshFormat)

            HStack {
                Spacer()
                Button("Continue") { vm.proceedToEraseConfirmation() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(vm.selectedCandidate == nil || !vm.canInitiateFreshFormat)
            }
        }
    }

    /// C6 Stage 3: contextual cyan banner mirroring the WPF surface.
    /// Visible for FullyConfigured and ConfiguredEmpty drives.
    private var alreadyConfiguredBanner: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(vm.alreadyConfiguredBannerText)
                .font(.callout)
                .foregroundColor(.primary)
            HStack {
                Button("Manage models") {
                    Task { await vm.enterManageModels() }
                }
                Button("Start over (formats drive)") {
                    vm.startOverFromBanner()
                }
                Spacer()
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.brandAccentCyan.opacity(0.12))
        .overlay(
            RoundedRectangle(cornerRadius: 6)
                .stroke(Color.brandAccentCyan, lineWidth: 1))
    }
}

// MARK: - Erase confirmation

struct EraseConfirmationStepView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Confirm destructive operation")
                .font(.headline)
            if let drive = vm.selectedCandidate {
                VStack(alignment: .leading, spacing: 6) {
                    Text("Drive: \(drive.displayName) (\(drive.identifier))")
                    Text("Size: \(drive.sizeDisplay)")
                    Text("Format: exFAT (Windows + macOS compatible)")
                    Text("Mount: \(drive.mountPoint?.path ?? "(unmounted)")")
                }
                .foregroundColor(.secondary)
            }
            Text("Clicking Erase will show a final macOS confirmation. The OS may ask for your password.")
                .font(.subheadline)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Spacer()

            HStack {
                Button("Back") { vm.currentStep = .driveSelection }
                Spacer()
                Button("Erase") { vm.confirmEraseAndProceed() }
                    .keyboardShortcut(.defaultAction)
            }
        }
    }
}

// MARK: - Encryption setup

struct EncryptionSetupStepView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Set up encryption")
                .font(.headline)
            // MAC30: encryption is opt-in. Default OFF — most users want
            // a frictionless plaintext config. Toggle ON re-shows the
            // MAC17a passphrase flow.
            Toggle("Encrypt SSD config", isOn: $vm.enableEncryption)
                .toggleStyle(.checkbox)
            if vm.enableEncryption {
                Form {
                    SecureField("Passphrase", text: $vm.passphrase)
                    SecureField("Confirm passphrase", text: $vm.passphraseConfirm)
                }
                Text("The passphrase decrypts the SSD's config on every launch. Store it somewhere you won't lose it — there is no recovery path.")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("Encryption is optional. Recommended if you plan to expose the Runner API on your LAN — your API key is only stored encrypted. You can always re-prep the SSD later to enable it.")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Divider()

            // C6 Stage 3: picker UI extracted to StarterModelPickerView
            // so the new ManageModelsStepView can reuse it. Single
            // source of truth for ~16 VM bindings.
            StarterModelPickerView(vm: vm)

            Text("Starter model pull happens after encryption. If the pull fails (e.g. Mac Ollama isn't running yet), it's non-fatal — you can pull models later from Mac Runner.")
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack {
                Spacer()
                Button(vm.enableEncryption ? "Write encryption & continue" : "Continue without encryption") {
                    Task { await vm.writeConfigAndProceed() }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(vm.enableEncryption &&
                          (vm.passphrase.isEmpty || vm.passphrase != vm.passphraseConfirm))
            }
        }
    }
}

// MARK: - Progress log (used by formatting / staging / model pull / readiness)

struct ProgressLogStepView: View {
    @ObservedObject var vm: PrepViewModel
    let title: String

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(title).font(.headline)
                if vm.isBusy { ProgressView().controlSize(.small) }
            }
            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) {
                        ForEach(Array(vm.logLines.enumerated()), id: \.offset) { idx, line in
                            Text(line)
                                .font(.system(.caption, design: .monospaced))
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .id(idx)
                        }
                    }
                    .padding(8)
                }
                .background(Color(NSColor.textBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .onChange(of: vm.logLines.count) { _ in
                    if let last = vm.logLines.indices.last {
                        withAnimation { proxy.scrollTo(last, anchor: .bottom) }
                    }
                }
            }
            .frame(maxHeight: .infinity)
        }
    }
}

// MARK: - Model pull
//
// Dedicated step view for the pull batch. Differs from
// ProgressLogStepView in two ways:
//   1. A single in-place "progress" Text view bound to
//      vm.pullProgressLine receives the sidecar's `progress: ...`
//      ticks — one per Ollama /api/pull NDJSON frame, formatted by
//      OllamaPullProgress.ToDisplayString on the C# side. The
//      scrolling log still surfaces stalls and other diagnostics
//      from [ollama serve stderr] etc.
//   2. A Cancel button gated on vm.canCancelPull lets the user
//      bail out of a slow pull without force-quitting the app.
//      Cancellation preserves partial blobs on disk so a Retry
//      resumes from where it stopped (sub-bug c).

struct ModelPullStepView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Pulling starter models…").font(.headline)
                if vm.isBusy { ProgressView().controlSize(.small) }
                Spacer()
                if vm.canCancelPull {
                    Button("Cancel") { vm.cancelPull() }
                        .controlSize(.regular)
                }
            }

            // Single in-place progress line. Falls back to a placeholder
            // before the first sidecar tick so the view doesn't jump
            // when the first `progress: Pulling <tag>…` arrives.
            Text(vm.pullProgressLine.isEmpty ? "Preparing pull…" : vm.pullProgressLine)
                .font(.system(.body, design: .monospaced))
                .foregroundColor(.secondary)
                .lineLimit(1)
                .truncationMode(.middle)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .background(Color(NSColor.textBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 6))

            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) {
                        ForEach(Array(vm.logLines.enumerated()), id: \.offset) { idx, line in
                            Text(line)
                                .font(.system(.caption, design: .monospaced))
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .id(idx)
                        }
                    }
                    .padding(8)
                }
                .background(Color(NSColor.textBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .onChange(of: vm.logLines.count) { _ in
                    if let last = vm.logLines.indices.last {
                        withAnimation { proxy.scrollTo(last, anchor: .bottom) }
                    }
                }
            }
            .frame(maxHeight: .infinity)
        }
    }
}

// MARK: - Model pull paused (MAC31a)

struct ModelPullPausedStepView: View {
    @ObservedObject var vm: PrepViewModel
    let tag: String
    let snapshot: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Pull paused").font(.headline)

            Text("You cancelled the pull for `\(tag)`. Partial download is preserved on disk — Retry resumes from where it stopped.")
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if let snapshot, !snapshot.isEmpty {
                Text(snapshot)
                    .font(.system(.body, design: .monospaced))
                    .foregroundColor(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 6)
                    .background(Color(NSColor.textBackgroundColor))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
            }

            Spacer()

            HStack {
                Button("Start over") { Task { await vm.restart() } }
                    .controlSize(.regular)
                Spacer()
                Button("Skip") { Task { await vm.skipRemainingPulls() } }
                    .controlSize(.regular)
                Button("Retry") { Task { await vm.resumePull() } }
                    .keyboardShortcut(.defaultAction)
                    .controlSize(.large)
            }
        }
    }
}

// MARK: - Model pull failed (M15)

struct ModelPullFailedStepView: View {
    @ObservedObject var vm: PrepViewModel
    let tags: [String]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Model pull needs attention").font(.headline)

            Text(failureSummary)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            if !tags.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    ForEach(tags, id: \.self) { tag in
                        Text(tag)
                            .font(.system(.caption, design: .monospaced))
                    }
                }
                .padding(.vertical, 4)
            }

            Text("Recent log").font(.headline)

            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) {
                        ForEach(Array(vm.logLines.enumerated()), id: \.offset) { idx, line in
                            Text(line)
                                .font(.system(.caption, design: .monospaced))
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .id(idx)
                        }
                    }
                    .padding(8)
                }
                .background(Color(NSColor.textBackgroundColor))
                .clipShape(RoundedRectangle(cornerRadius: 6))
                .onAppear {
                    if let last = vm.logLines.indices.last {
                        proxy.scrollTo(last, anchor: .bottom)
                    }
                }
            }
            .frame(maxHeight: .infinity)

            HStack {
                Spacer()
                Button("Continue to readiness") {
                    Task { await vm.continueAfterPullFailures() }
                }
                .controlSize(.regular)
                Button("Retry failed pulls") {
                    Task { await vm.retryFailedPulls() }
                }
                .keyboardShortcut(.defaultAction)
                .controlSize(.large)
                .disabled(tags.isEmpty || vm.isBusy)
            }
        }
    }

    private var failureSummary: String {
        let count = tags.count
        let modelWord = count == 1 ? "model" : "models"
        return "\(count) \(modelWord) failed to pull. This is non-fatal; you can retry now or continue and pull the \(modelWord) later from Mac Runner."
    }
}

// MARK: - Done

struct DoneStepView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Drive ready")
                .font(.title)
                .bold()
            Text("Your SSD is ready. Open `Runner.app` at the top level of the SSD to start chatting. Quit when ready.")
                .foregroundColor(.secondary)

            if !vm.readinessItems.isEmpty {
                Text("Readiness").font(.headline)
                ForEach(vm.readinessItems) { row in
                    HStack {
                        Circle().fill(row.statusColor).frame(width: 10, height: 10)
                        Text(row.name)
                        Spacer()
                        Text(row.status).font(.caption).foregroundColor(.secondary)
                    }
                }
            }

            Spacer()

            HStack {
                Spacer()
                Button("Quit") {
                    Task { await vm.quit() }
                }
                .keyboardShortcut(.defaultAction)
                .controlSize(.large)
            }
        }
    }
}

// MARK: - Failed

struct FailedStepView: View {
    let message: String
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack {
                Circle().fill(Color.brandStatusDanger).frame(width: 14, height: 14)
                Text("Something went wrong").font(.title2).bold()
            }
            Text(message)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Text("Recent log:")
                .font(.headline)
                .padding(.top, 8)

            ScrollView {
                LazyVStack(alignment: .leading, spacing: 2) {
                    ForEach(Array(vm.logLines.suffix(50).enumerated()), id: \.offset) { _, line in
                        Text(line)
                            .font(.system(.caption, design: .monospaced))
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                .padding(8)
            }
            .background(Color(NSColor.textBackgroundColor))
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .frame(maxHeight: 220)

            Spacer()

            HStack {
                Spacer()
                Button("Restart") {
                    Task { await vm.restart() }
                }
                .keyboardShortcut(.defaultAction)
            }
        }
    }
}
