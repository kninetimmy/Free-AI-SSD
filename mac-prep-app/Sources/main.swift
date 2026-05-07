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
                case .modelPull:         ProgressLogStepView(vm: vm, title: "Pulling starter models…")
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

            Form {
                TextField("Volume label", text: $vm.volumeLabel)
                Toggle("Also prepare for Windows (cross-platform)", isOn: $vm.prepareForWindowsToo)
            }

            HStack {
                Spacer()
                Button("Continue") { vm.proceedToEraseConfirmation() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(vm.selectedCandidate == nil)
            }
        }
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
            Toggle("Encrypt the drive's configuration store", isOn: $vm.enableEncryption)
            if vm.enableEncryption {
                Form {
                    SecureField("Passphrase", text: $vm.passphrase)
                    SecureField("Confirm passphrase", text: $vm.passphraseConfirm)
                }
                Text("The passphrase decrypts the SSD's config on every launch. Store it somewhere you won't lose it — there is no recovery path.")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Divider()

            Text("Starter models")
                .font(.headline)
            VStack(alignment: .leading) {
                ForEach(vm.availableStarterModels, id: \.self) { tag in
                    Toggle(tag, isOn: Binding(
                        get: { vm.selectedStarterModels.contains(tag) },
                        set: { sel in
                            if sel { vm.selectedStarterModels.insert(tag) }
                            else   { vm.selectedStarterModels.remove(tag) }
                        }
                    ))
                }
            }
            Text("Starter model pull happens after encryption. If the pull fails (e.g. Mac Ollama isn't running yet), it's non-fatal — you can pull models later from Mac Runner.")
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Spacer()

            HStack {
                Spacer()
                Button("Write encryption & continue") {
                    Task { await vm.writeEncryptionAndProceed() }
                }
                .keyboardShortcut(.defaultAction)
                .disabled(vm.enableEncryption && (vm.passphrase.isEmpty || vm.passphrase != vm.passphraseConfirm))
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

// MARK: - Done

struct DoneStepView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Drive ready")
                .font(.title)
                .bold()
            Text("Free AI SSD is staged and ready to use. Launch the Runner from the SSD's `mac/Runner.app` to start chatting.")
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
                Button("Finish") { vm.finalize() }
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
                Button("Restart") { vm.restart() }
                    .keyboardShortcut(.defaultAction)
            }
        }
    }
}
