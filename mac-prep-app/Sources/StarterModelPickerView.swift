import SwiftUI

// C6 Stage 3 — Extracted starter-model picker (action row + filter row +
// scrolling list + HF token field + status captions). Reused by both
// EncryptionSetupStepView (prep flow's "pick models to pull at finalize")
// and ManageModelsStepView (re-entered drive's "Add a model" disclosure).
//
// Single source of truth for the ~400-line UI that binds ~16 VM fields.
// Every binding here was previously inline in EncryptionSetupStepView and
// pinned by C3/C4/C5/C24/C25/C26/C27 tests against the VM — extraction
// is pure UI restructuring, no behavioral change.

struct StarterModelPickerView: View {
    @ObservedObject var vm: PrepViewModel

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            actionRow
            statusCaptions
            huggingFaceTokenRow
            filterRow
            starterPickerBody
            huggingFaceTokenWarning
        }
    }

    // F2a: action row collapses Most popular + Search + Refresh into a
    // single line so the catalog body owns the rest of the page.
    private var actionRow: some View {
        HStack(spacing: 10) {
            Text("Starter models")
                .font(.headline)
            Spacer(minLength: 12)
            // C27 Stage 1: catalog source picker. Switching sources
            // clears the catalog and refetches via handleSourceSwitch.
            Picker("", selection: $vm.activeSource) {
                Text("Ollama").tag(ModelSourceKind.ollama)
                Text("Hugging Face").tag(ModelSourceKind.huggingFace)
            }
            .pickerStyle(.menu)
            .labelsHidden()
            .frame(width: 140)
            .help("Pick where to browse models. Ollama (bundled + ollama.com) or Hugging Face (live GGUF Search API).")
            .onChange(of: vm.activeSource) { _ in
                Task { await vm.handleSourceSwitch() }
            }
            // F2a: macOS 11.0 baseline rules out `.toggleStyle(.button)`
            // (12+), so a plain Button flips the state. Active state is
            // communicated by the trailing checkmark in the label.
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
            .help("Show the top-N by pull count from ollama.com/library. Refresh first to populate pull counts on the bundled list.")
            // C26: limit dropdown next to the Most-popular toggle.
            Picker("", selection: $vm.mostPopularLimit) {
                ForEach(PrepViewModel.mostPopularLimitOptions, id: \.self) { n in
                    Text("Top \(n)").tag(n)
                }
            }
            .pickerStyle(.menu)
            .labelsHidden()
            .frame(width: 90)
            .help("Choose how many top entries the Most-popular toggle exposes. Sorted desc by pull count from the live catalog.")
            TextField("Search models…", text: $vm.modelSearchText)
                .textFieldStyle(.roundedBorder)
                .frame(minWidth: 180, maxWidth: 280)
                .help(vm.activeSource == .huggingFace
                      ? "Search Hugging Face for GGUF repos. Fires a debounced server query."
                      : "Filter the grid by tag, tier, or capability description (case-insensitive).")
                .onChange(of: vm.modelSearchText) { newValue in
                    // C27 Stage 1: under HF the search box drives a
                    // server query (HF has millions of repos vs. Ollama's
                    // ~400, so client-side filter isn't an option).
                    if vm.activeSource == .huggingFace {
                        vm.scheduleHuggingFaceSearch(for: newValue)
                    }
                }
            Button {
                Task {
                    if vm.activeSource == .huggingFace {
                        let needle = vm.modelSearchText.trimmingCharacters(in: .whitespacesAndNewlines)
                        await vm.refreshHuggingFaceCatalog(search: needle.isEmpty ? nil : needle)
                    } else {
                        await vm.refreshCatalog()
                    }
                }
            } label: {
                if vm.isRefreshingCatalog {
                    HStack(spacing: 6) {
                        ProgressView().controlSize(.small)
                        Text("Refreshing…")
                    }
                } else {
                    Text("Refresh")
                }
            }
            .disabled(vm.isRefreshingCatalog)
            .help(vm.activeSource == .huggingFace
                  ? "Fetch the latest GGUF repos from Hugging Face. The previous list stays in place if the fetch fails."
                  : "Fetch the latest model list from ollama.com/library. The bundled list stays in place if the fetch fails.")
        }
    }

    @ViewBuilder
    private var statusCaptions: some View {
        if !vm.catalogStatusText.isEmpty {
            Text(vm.catalogStatusText)
                .font(.caption)
                .foregroundColor(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        // M11: announce the visible row count + cap reason when a filter
        // is active. Empty string when neither toggle nor search is
        // engaged (catalogStatusText already shows the total).
        if !vm.starterRowCountCaption.isEmpty {
            Text(vm.starterRowCountCaption)
                .font(.caption)
                .bold()
                .foregroundColor(Color.brandAccentCyan)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    // C27 Stage 3: inline Hugging Face token field. Visible only when
    // activeSource == .huggingFace.
    @ViewBuilder
    private var huggingFaceTokenRow: some View {
        if vm.activeSource == .huggingFace {
            HStack(spacing: 8) {
                Text("HF token (optional)")
                    .font(.caption)
                    .foregroundColor(.secondary)
                SecureField("hf_…", text: $vm.huggingFaceToken)
                    .textFieldStyle(.roundedBorder)
                    .frame(maxWidth: 320)
                    .help("Personal Hugging Face access token. Required for gated or private GGUF repos. Stored on the SSD; sealed with AES-256-GCM when SSD encryption is on. Leave blank for anonymous browsing.")
                    .onChange(of: vm.huggingFaceToken) { newValue in
                        Task { await vm.pushHuggingFaceTokenToSidecar(newValue) }
                    }
            }
            if !vm.enableEncryption && !vm.huggingFaceToken.isEmpty {
                Text("⚠ Encryption is off — your Hugging Face token will be stored in plaintext on the SSD. Enable encryption on the next step for defense in depth.")
                    .font(.caption)
                    .foregroundColor(Color.brandStatusWarning)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    // C3 / C4 / C5: parameter cap, capability chips, sort mode.
    private var filterRow: some View {
        HStack(spacing: 10) {
            Text("Max size")
                .font(.caption)
                .foregroundColor(.secondary)
            Picker("", selection: parameterCapBinding) {
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
            capabilityChip(label: PrepViewModel.capabilityTools)
            capabilityChip(label: PrepViewModel.capabilityVision)
            capabilityChip(label: PrepViewModel.capabilityThinking)
            capabilityChip(label: PrepViewModel.capabilityAudio)

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
    }

    // Field-test gate: HF pulls fail without a token even for public
    // GGUFs (HF rate-limits anon). Surface the requirement inline so
    // users see it before the Continue click.
    @ViewBuilder
    private var huggingFaceTokenWarning: some View {
        if vm.huggingFaceSelectionNeedsToken() {
            Text("⚠ Hugging Face models selected — paste a free read-only HF token above before continuing. Click Continue for setup instructions.")
                .font(.caption)
                .foregroundColor(Color.brandStatusWarning)
                .fixedSize(horizontal: false, vertical: true)
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
                        // C25 pass-through fade only applies to Ollama rows
                        // whose capability list happens to be empty. HF rows
                        // never expose capability tags via the HF API, so
                        // fading every HF row looks "disabled" when any chip
                        // is engaged. Suppress the fade for HF.
                        let isPassThrough = entry.capabilities.isEmpty
                            && !vm.requiredCapabilities.isEmpty
                            && entry.sourceKind != .huggingFace
                        let repoId = vm.stripHuggingFacePrefix(entry.tag)
                        let isExpanded = vm.expandedRepoIds.contains(repoId)
                        let isExpanding = vm.huggingFaceExpansionInFlight.contains(repoId)
                        HStack(spacing: 4) {
                            // C27 Stage 4: chevron for HF parents, indent
                            // glyph for quant children, blank for everything
                            // else.
                            if entry.isExpandable {
                                Button {
                                    Task { await vm.toggleRepoExpansion(parent: entry) }
                                } label: {
                                    Text(isExpanding ? "…" : (isExpanded ? "▼" : "▶"))
                                        .font(.system(size: 10))
                                        .frame(width: 14)
                                }
                                .buttonStyle(.plain)
                                .help("Show or hide the GGUF quants for this Hugging Face repo. First expand fetches sizes from huggingface.co.")
                            } else if entry.isQuantChild {
                                Text("⌞")
                                    .font(.system(size: 10))
                                    .foregroundColor(Color.brandAccentCyan)
                                    .frame(width: 14)
                                    .padding(.leading, 14)
                            } else {
                                Spacer().frame(width: 14)
                            }
                            // HF parent rows can't be pulled directly —
                            // Ollama needs a specific `:quant` tag. Disable
                            // the checkbox so the only path forward is the
                            // chevron + a quant child.
                            let isHfParent = entry.isExpandable
                            Toggle(isOn: Binding(
                                get: { vm.selectedStarterModels.contains(entry.tag) },
                                set: { sel in
                                    if isHfParent {
                                        Task { await vm.toggleRepoExpansion(parent: entry) }
                                        return
                                    }
                                    if sel { vm.selectedStarterModels.insert(entry.tag) }
                                    else   { vm.selectedStarterModels.remove(entry.tag) }
                                }
                            ))
                            {
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
                                        if let size = entry.quantSizeBytes, size > 0 {
                                            Text(formatQuantSize(size))
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
                        // C25: mute rows that survive an active chip filter
                        // only because their capabilities list is empty.
                        .opacity(isPassThrough ? 0.55 : 1.0)
                        .help(isPassThrough
                              ? "No capability data for this entry — surviving the chip filter via pass-through. Refresh from Ollama to populate."
                              : "")
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, 4)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    /// C27 Stage 4: format a per-quant size in GB with one decimal.
    private func formatQuantSize(_ bytes: Int64) -> String {
        let gb = Double(bytes) / (1024.0 * 1024.0 * 1024.0)
        return String(format: "%.1f GB", gb)
    }

    /// C4: a single capability chip — Button-with-checkmark mirror of
    /// the Most-popular toggle pattern.
    @ViewBuilder
    private func capabilityChip(label: String) -> some View {
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
    private var parameterCapBinding: Binding<Double?> {
        Binding<Double?>(
            get: { vm.maxParametersBillion },
            set: { vm.maxParametersBillion = $0 })
    }
}

/// F2a: format a pull-count number into the Ollama library shorthand
/// (e.g. 114_100_000 → "114.1M"). File-scope so the picker tests can
/// also reach it. Mirrors the inverse of LiveModelCatalogService's
/// ParsePullCount for caption display.
func formatPullCount(_ count: Int64) -> String {
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
