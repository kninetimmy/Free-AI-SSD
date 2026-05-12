import SwiftUI

// C6 Stage 3 — Manage-models step. Reached from the already-configured-drive
// banner on DriveSelectionStepView. Surfaces installed models + Remove
// per-row. The Add disclosure (lower section) lands in sub-stage 3.6
// once StarterModelPickerView has been extracted from EncryptionSetupStepView.
//
// Encrypted drives render a read-only variant per decision D13: installed
// list is visible (disk walk doesn't need decryption) but Remove buttons
// are disabled with an explainer banner. Add support lands in C7.

struct ManageModelsStepView: View {
    @ObservedObject var vm: PrepViewModel
    @State private var isAddExpanded: Bool = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            if vm.driveConfiguration.isConfigEncrypted {
                encryptedWarningBanner
            }
            installedSection
            Divider()
            addSection
            Spacer()
            footer
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

    private var encryptedWarningBanner: some View {
        Text("This drive is encrypted. Unlock support for Add and Remove lands in C7. Installed models are shown for reference.")
            .font(.callout)
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.yellow.opacity(0.15))
            .overlay(
                RoundedRectangle(cornerRadius: 6)
                    .stroke(Color.yellow.opacity(0.6), lineWidth: 1))
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

    // M18: the DisclosureGroup is intentionally NOT `.disabled` on encrypted
    // drives. Previously the whole group was disabled when `canManageModelsAdd`
    // is false, which hid the inner C7 explanation behind a chevron the user
    // could never expand. Now the chevron always works; the C7 hint surfaces
    // on the label itself (so the user sees it without expanding) and the
    // longer message is inside the content for users who do expand. The
    // picker is hidden — not just disabled — to avoid running its Refresh /
    // source-switch / HF-token side effects on a drive where pulls can't run.
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
                Text("Unlock required (C7) — Add disabled on encrypted drives. Adding new models will work once encrypted-config persistence ships.")
                    .font(.caption)
                    .foregroundColor(.secondary)
                    .padding(.vertical, 8)
            }
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                Text("Add a model").font(.headline)
                if !vm.canManageModelsAdd {
                    Text("Unlock required (C7) — disabled on encrypted drives.")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
            }
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
