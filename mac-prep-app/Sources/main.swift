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

            // F2a: action row collapses Most popular + Search + Refresh
            // into a single line so the catalog body owns the rest of
            // the page. The picker scrolls inside a `.frame(maxHeight:
            // .infinity)` ScrollView that grows with the window — no
            // hardcoded 240px cap.
            HStack(spacing: 10) {
                Text("Starter models")
                    .font(.headline)
                Spacer(minLength: 12)
                // F2a: macOS 11.0 baseline rules out
                // `.toggleStyle(.button)` (12+), so a plain Button
                // flips the state. Active state is communicated by
                // the trailing checkmark in the label.
                Button {
                    vm.showOnlyMostPopular.toggle()
                } label: {
                    HStack(spacing: 4) {
                        Text("Most popular")
                        if vm.showOnlyMostPopular {
                            Text("✓").bold()
                        }
                    }
                }
                .help("Show the top \(PrepViewModel.mostPopularCount) by pull count from ollama.com/library. Refresh first to populate pull counts on the bundled list.")
                TextField("Search models…", text: $vm.modelSearchText)
                    .textFieldStyle(.roundedBorder)
                    .frame(minWidth: 180, maxWidth: 280)
                Button {
                    Task { await vm.refreshCatalog() }
                } label: {
                    if vm.isRefreshingCatalog {
                        HStack(spacing: 6) {
                            ProgressView().controlSize(.small)
                            Text("Refreshing…")
                        }
                    } else {
                        Text("Refresh from Ollama")
                    }
                }
                .disabled(vm.isRefreshingCatalog)
            }
            if !vm.catalogStatusText.isEmpty {
                Text(vm.catalogStatusText)
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            // M11: announce the visible row count + cap reason when a
            // filter is active. Empty string when neither toggle nor
            // search is engaged (catalogStatusText already shows the
            // total in that case).
            if !vm.starterRowCountCaption.isEmpty {
                Text(vm.starterRowCountCaption)
                    .font(.caption)
                    .bold()
                    .foregroundColor(Color.brandAccentCyan)
                    .fixedSize(horizontal: false, vertical: true)
            }

            // C3 / C4 / C5: second filter row — parameter cap, capability
            // chips, sort mode. Mirrors the WPF MainWindow.xaml layout so
            // the Mac picker offers the same affordances. macOS 11.0
            // baseline keeps the chips as Buttons-with-checkmarks (same
            // pattern as the Most-popular toggle above).
            HStack(spacing: 10) {
                Text("Max size")
                    .font(.caption)
                    .foregroundColor(.secondary)
                Picker("", selection: parameterCapBinding(vm)) {
                    Text("All").tag(Optional<Double>.none)
                    Text("≤7B").tag(Optional<Double>.some(7))
                    Text("≤14B").tag(Optional<Double>.some(14))
                    Text("≤30B").tag(Optional<Double>.some(30))
                    Text("≤70B").tag(Optional<Double>.some(70))
                }
                .pickerStyle(.menu)
                .labelsHidden()
                .frame(width: 90)
                .help("Hide models with a parameter count above the chosen cap. Models without a known size pass through (your existing config and on-disk models stay visible).")

                Text("Capabilities")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .padding(.leading, 8)
                capabilityChip(label: PrepViewModel.capabilityTools, vm: vm)
                capabilityChip(label: PrepViewModel.capabilityVision, vm: vm)
                capabilityChip(label: PrepViewModel.capabilityThinking, vm: vm)
                capabilityChip(label: PrepViewModel.capabilityAudio, vm: vm)

                Spacer(minLength: 6)

                Text("Sort")
                    .font(.caption)
                    .foregroundColor(.secondary)
                Picker("", selection: $vm.sortMode) {
                    Text("Popular").tag(PickerSortMode.popular)
                    Text("Newest").tag(PickerSortMode.newest)
                    Text("A–Z").tag(PickerSortMode.alphabetical)
                }
                .pickerStyle(.menu)
                .labelsHidden()
                .frame(width: 110)
                .help("Reorder the picker. Popular keeps the natural ollama.com order. Newest sorts by the scraped last-updated timestamp; entries without a timestamp sort last.")
            }

            starterPickerBody

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

    /// F2a: scrolling list that fills the rest of the page. Empty
    /// state surfaces *why* nothing is visible (filter active vs.
    /// catalog empty) so the user knows the next move.
    @ViewBuilder
    private var starterPickerBody: some View {
        let entries = vm.visibleStarterModels
        if entries.isEmpty {
            VStack(spacing: 8) {
                Spacer(minLength: 0)
                if vm.starterCatalog.isEmpty {
                    Text("No models loaded.")
                        .font(.body)
                    Text("Click Refresh from Ollama to fetch the catalog.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                } else if vm.showOnlyMostPopular && vm.starterCatalog.allSatisfy({ $0.pullCount == nil }) {
                    Text("No popularity data on the bundled catalog.")
                        .font(.body)
                    Text("Click Refresh from Ollama to populate pull counts, then re-toggle Most popular.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .multilineTextAlignment(.center)
                } else if !vm.requiredCapabilities.isEmpty
                            && vm.starterCatalog.allSatisfy({ $0.capabilities.isEmpty }) {
                    Text("No capability data on the bundled catalog.")
                        .font(.body)
                    Text("Click Refresh from Ollama to populate capabilities, then reapply the chip filter.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .multilineTextAlignment(.center)
                } else {
                    Text("No models match the current filter.")
                        .font(.body)
                    Text("Clear the search box, lower the size cap, or unselect capability chips to see more entries.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                        .multilineTextAlignment(.center)
                }
                Spacer(minLength: 0)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 6) {
                    ForEach(entries) { entry in
                        Toggle(isOn: Binding(
                            get: { vm.selectedStarterModels.contains(entry.tag) },
                            set: { sel in
                                if sel { vm.selectedStarterModels.insert(entry.tag) }
                                else   { vm.selectedStarterModels.remove(entry.tag) }
                            }
                        )) {
                            VStack(alignment: .leading, spacing: 1) {
                                HStack(spacing: 6) {
                                    Text(entry.tag).font(.body).bold()
                                    Text(entry.sizeTier)
                                        .font(.caption2)
                                        .padding(.horizontal, 5).padding(.vertical, 1)
                                        .background(Color.brandStatusInfo.opacity(0.15))
                                        .foregroundColor(Color.brandStatusInfo)
                                        .clipShape(RoundedRectangle(cornerRadius: 3))
                                    if let count = entry.pullCount {
                                        Text(formatPullCount(count) + " pulls")
                                            .font(.caption2)
                                            .foregroundColor(.secondary)
                                    }
                                }
                                if !entry.bestAt.isEmpty {
                                    Text(entry.bestAt)
                                        .font(.caption)
                                        .foregroundColor(.secondary)
                                        .lineLimit(2)
                                        .fixedSize(horizontal: false, vertical: true)
                                }
                            }
                        }
                        .toggleStyle(.checkbox)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 4)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    /// C4: a single capability chip — a Button-with-checkmark mirror of
    /// the Most-popular toggle pattern (macOS 11.0 baseline rules out
    /// `.toggleStyle(.button)` from macOS 12+). Toggles the underlying
    /// set on the VM via `toggleCapability`.
    @ViewBuilder
    private func capabilityChip(label: String, vm: PrepViewModel) -> some View {
        Button {
            vm.toggleCapability(label)
        } label: {
            HStack(spacing: 3) {
                Text(label)
                if vm.requiredCapabilities.contains(label.lowercased()) {
                    Text("✓").bold()
                }
            }
        }
        .help("Show only models that advertise the \(label) capability. Multiple chips compose with AND semantics.")
    }

    /// C3: SwiftUI Picker requires a single typed binding. The VM stores
    /// the cap as `Double?`; this helper bridges the picker tags
    /// (Optional<Double>) back to the VM property.
    private func parameterCapBinding(_ vm: PrepViewModel) -> Binding<Double?> {
        Binding<Double?>(
            get: { vm.maxParametersBillion },
            set: { vm.maxParametersBillion = $0 })
    }
}

/// F2a: format a pull-count number into the Ollama library shorthand
/// (e.g. 114_100_000 → "114.1M"). Mirrors the inverse of
/// `LiveModelCatalogService.ParsePullCount` for caption display.
private func formatPullCount(_ count: Int64) -> String {
    let absVal = abs(count)
    if absVal >= 1_000_000_000 {
        return String(format: "%.1fB", Double(count) / 1_000_000_000.0)
    } else if absVal >= 1_000_000 {
        return String(format: "%.1fM", Double(count) / 1_000_000.0)
    } else if absVal >= 1_000 {
        return String(format: "%.1fK", Double(count) / 1_000.0)
    } else {
        return "\(count)"
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

// MARK: - Done

struct DoneStepView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            Text("Drive ready")
                .font(.title)
                .bold()
            Text("Your SSD is ready. Open `mac/Runner.app` on the SSD to start chatting. Quit when ready.")
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
