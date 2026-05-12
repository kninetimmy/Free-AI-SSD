import SwiftUI

// C6 Stage 3 — Manage-models step. Reached from the already-configured-drive
// banner on DriveSelectionStepView. Surfaces installed models + Remove
// per-row. The Add disclosure (lower section) lands in sub-stage 3.6
// once StarterModelPickerView has been extracted from EncryptionSetupStepView.
//
// Encrypted drives render a banner explaining the lock state. Per C7,
// the banner now hosts an Unlock button that opens a SecureField sheet
// (mirror of RunnerViewModel.UnlockSheet); a successful unlock flips
// the banner to a green "✓ Unlocked for this session" indicator and
// enables Add + Remove for the lifetime of the .manageModels step.

struct ManageModelsStepView: View {
    @ObservedObject var vm: PrepViewModel
    @State private var isAddExpanded: Bool = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            if vm.driveConfiguration.isConfigEncrypted {
                encryptedDriveBanner
            }
            installedSection
            Divider()
            addSection
            Spacer()
            footer
        }
        .sheet(isPresented: $vm.unlockDialogPresented) {
            UnlockSheet(vm: vm)
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("Manage models")
                .font(.title2)
                .bold()
            if let mount = vm.selectedCandidate?.mountPoint {
                Text("Drive: \(mount.lastPathComponent)")
                    .font(.subheadline)
                    .foregroundColor(.secondary)
            }
        }
    }

    /// C7: banner switches between locked (yellow + Unlock button) and
    /// unlocked (green check) states for the lifetime of `.manageModels`.
    /// Both states only render when the drive is encrypted at all — an
    /// unencrypted drive shows no banner, same as before.
    @ViewBuilder
    private var encryptedDriveBanner: some View {
        if vm.isManageModelsUnlocked {
            HStack(alignment: .top, spacing: 8) {
                Text("✓").foregroundColor(.green).bold()
                Text("Unlocked for this session. Add and Remove enabled.")
                    .font(.callout)
                Spacer()
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.green.opacity(0.12))
            .overlay(
                RoundedRectangle(cornerRadius: 6)
                    .stroke(Color.green.opacity(0.6), lineWidth: 1))
        } else {
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    Text("This drive is encrypted. Click Unlock to enable Add and Remove. Installed models are shown for reference.")
                        .font(.callout)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer()
                Button("Unlock…") {
                    vm.presentUnlockSheet()
                }
                .disabled(vm.isBusy || vm.isUnlocking)
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.yellow.opacity(0.15))
            .overlay(
                RoundedRectangle(cornerRadius: 6)
                    .stroke(Color.yellow.opacity(0.6), lineWidth: 1))
        }
    }

    private var installedSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Installed models (\(vm.installedModels.count))")
                    .font(.headline)
                Spacer()
                Button("Refresh") {
                    Task { await vm.refreshInstalledModels() }
                }
                .disabled(vm.isBusy)
            }
            if vm.installedModels.isEmpty {
                Text("No models on this drive yet. Use Add a model below to pull one.")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .padding(.vertical, 8)
            } else {
                List(vm.installedModels, id: \.self) { tag in
                    HStack {
                        Text(tag).font(.system(.body, design: .monospaced))
                        Spacer()
                        Button("Remove") {
                            Task { await vm.removeModel(tag: tag) }
                        }
                        .disabled(!vm.canManageModelsRemove || vm.isBusy)
                    }
                }
                .frame(minHeight: 120, maxHeight: 240)
            }
        }
    }

    /// C7: post-unlock the disclosure renders the full picker (same as
    /// the unencrypted path). M18's inline "Unlock required (C7)" caption
    /// has been removed — the banner above tells the user everything
    /// they need to know about the lock state.
    private var addSection: some View {
        DisclosureGroup(isExpanded: $isAddExpanded) {
            if vm.canManageModelsAdd {
                VStack(alignment: .leading, spacing: 12) {
                    StarterModelPickerView(vm: vm)
                    HStack {
                        Spacer()
                        Button("Pull selected") {
                            Task { await vm.pullSelectedFromManagement() }
                        }
                        .keyboardShortcut(.defaultAction)
                        .disabled(vm.selectedStarterModels.isEmpty || vm.isBusy)
                    }
                }
                .padding(.top, 8)
            } else {
                Text("Add a model is disabled until you unlock the drive.")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .padding(.vertical, 8)
            }
        } label: {
            Text("Add a model").font(.headline)
        }
    }

    private var footer: some View {
        HStack {
            Button("Done") {
                Task { await vm.exitManageModels() }
            }
            .keyboardShortcut(.defaultAction)
            .disabled(vm.isBusy)
            Spacer()
        }
    }
}

// MARK: - UnlockSheet (C7)
//
// Mirror of RunnerViewModel.UnlockSheet (mac-runner/Sources/main.swift:1735-
// 1766). Same visual shape and keyboard semantics so the user has one
// mental model for "unlock an encrypted SSD." Differs only in that the
// PrepApp version is bound to PrepViewModel and submits via
// `attemptUnlock(password:)` (synchronous mirror of Runner's).

struct UnlockSheet: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Unlock encrypted SSD").font(.title3)
            Text("Enter the passphrase set during PrepApp finalization. Add and Remove will be enabled for this session.")
                .font(.callout)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            SecureField("Passphrase", text: $vm.unlockDialogPassword)
                .textFieldStyle(.roundedBorder)
            // `.onSubmit` is macOS 12+ only and the MAC1 deployment
            // target is 11.0; the Unlock button's
            // `.keyboardShortcut(.defaultAction)` below already gives
            // Enter-to-submit semantics.
            if let err = vm.unlockDialogError {
                Text(err).foregroundColor(.red).font(.callout)
            }
            HStack {
                Spacer()
                Button("Cancel") { vm.cancelUnlock() }
                    .keyboardShortcut(.cancelAction)
                Button("Unlock") {
                    vm.attemptUnlock(password: vm.unlockDialogPassword)
                }
                .keyboardShortcut(.defaultAction)
                .disabled(vm.unlockDialogPassword.isEmpty || vm.isUnlocking)
            }
        }
        .padding(20)
        .frame(minWidth: 380)
    }
}
