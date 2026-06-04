import Foundation

// MAC17 mac-prep-app test runner. Mirrors mac-runner/Tests/SsdEncryptionTests.swift
// pattern: a standalone CLI binary compiled with swiftc that exits non-zero on
// any failed assertion. Run from CI via:
//
//     swiftc \
//         mac-prep-app/Sources/PrepFlowStep.swift \
//         mac-prep-app/Sources/DiskutilFormatCommand.swift \
//         mac-prep-app/Sources/CommandPayloadEncoder.swift \
//         mac-prep-app/Tests/PrepAppTests.swift \
//         -parse-as-library -target "arm64-apple-macos11.0" \
//         -o /tmp/mac-prep-tests && /tmp/mac-prep-tests
//
// We intentionally limit the test surface to the *pure*, non-UI types
// (DiskutilFormatCommand + PrepFlowStep + the static parseCandidates seam
// on DiskutilDriveService). PrepHostController and PrepViewModel are
// exercised end-to-end on the published binary in CI, where a real macOS
// environment is available. Pure-builder tests here are the parity-pin
// against the C# DiskutilFormatCommandTests and the regression pin
// against the MAC21 post-format mount-discovery bug — both would fail
// fast on any drift.

@main
struct PrepAppTestsMain {
    static func main() async {
        // Subcommand: write a canonical Mac PrepApp encrypted-config
        // fixture for the cross-language test. Invoked once (or on
        // deliberate format change) to (re)generate
        // tests/Fixtures/MacEncryptedConfig/swift-prep-encrypted/.
        // Mirrors the MAC5 mac-runner Tests' write-fixture subcommand.
        let args = CommandLine.arguments
        if args.count >= 3, args[1] == "write-prep-fixture" {
            let outDir = URL(fileURLWithPath: args[2])
            do {
                try writeMac17PrepFixture(outDir: outDir)
                print("Wrote MAC17 prep fixture to \(outDir.path)")
                exit(0)
            } catch {
                print("MAC17 prep fixture write failed: \(error)")
                exit(1)
            }
        }

        let runner = TestRunner()

        // MARK: DiskutilFormatCommand parity-pin tests

        runner.test("Build emits expected argv shape") {
            let built = try DiskutilFormatCommand.build(
                diskIdentifier: "disk2", label: "FREEAI", fileSystem: "ExFAT")
            try expect(built.fileName == "/usr/sbin/diskutil",
                       "fileName: \(built.fileName)")
            try expect(built.arguments == ["eraseDisk", "ExFAT", "FREEAI", "MBR", "disk2"],
                       "arguments: \(built.arguments)")
        }

        runner.test("Build accepts case variants and emits canonical ExFAT") {
            for variant in ["ExFAT", "EXFAT", "exfat", "exFAT"] {
                let built = try DiskutilFormatCommand.build(
                    diskIdentifier: "disk2", label: "FREEAI", fileSystem: variant)
                try expect(built.arguments[1] == "ExFAT",
                           "variant=\(variant) emitted \(built.arguments[1])")
            }
        }

        runner.test("Build pins MBR partition scheme") {
            let built = try DiskutilFormatCommand.build(
                diskIdentifier: "disk2", label: "FREEAI", fileSystem: "ExFAT")
            try expect(built.arguments.contains("MBR"), "missing MBR in \(built.arguments)")
            try expect(!built.arguments.contains("GPT"), "unexpected GPT")
            try expect(!built.arguments.contains("APM"), "unexpected APM")
        }

        runner.test("ParseDiskIdentifier accepts common forms") {
            let cases: [(String, String)] = [
                ("disk2", "disk2"),
                ("disk20", "disk20"),
                ("disk2s1", "disk2s1"),
                ("/dev/disk2", "disk2"),
                ("/dev/disk2s1", "disk2s1"),
                ("  disk3  ", "disk3"),
            ]
            for (input, expected) in cases {
                let parsed = try DiskutilFormatCommand.parseDiskIdentifier(input)
                try expect(parsed == expected,
                           "input='\(input)' → '\(parsed)', expected '\(expected)'")
            }
        }

        runner.test("ParseDiskIdentifier rejects malformed forms") {
            for bad in ["", "  ", "disk", "disks2", "disk2s", "disk2sX", "disk2x1", "/dev/sda", "hd0"] {
                var threw = false
                do { _ = try DiskutilFormatCommand.parseDiskIdentifier(bad) }
                catch { threw = true }
                try expect(threw, "expected throw for '\(bad)'")
            }
        }

        runner.test("Build rejects APFS / NTFS / unsupported filesystems") {
            for bad in ["APFS", "NTFS", "FAT32", "HFS+", "MSDOS"] {
                var threw = false
                do {
                    _ = try DiskutilFormatCommand.build(
                        diskIdentifier: "disk2", label: "FREEAI", fileSystem: bad)
                } catch { threw = true }
                try expect(threw, "expected throw for fs='\(bad)'")
            }
        }

        runner.test("SanitizeLabel removes metacharacters and caps length") {
            try expect(DiskutilFormatCommand.sanitizeLabel("FREEAI") == "FREEAI")
            try expect(DiskutilFormatCommand.sanitizeLabel("  FREEAI  ") == "FREEAI")
            try expect(DiskutilFormatCommand.sanitizeLabel("a/b\\c:d*e?f\"g<h>i|j") == "abcdefghij",
                       "got \(DiskutilFormatCommand.sanitizeLabel("a/b\\c:d*e?f\"g<h>i|j"))")
            let oversized = String(repeating: "X", count: 32)
            try expect(DiskutilFormatCommand.sanitizeLabel(oversized).count == 15)
        }

        runner.test("Build substitutes single space when label sanitizes to empty") {
            let built = try DiskutilFormatCommand.build(
                diskIdentifier: "disk2", label: "///\\\\:::", fileSystem: "ExFAT")
            try expect(built.arguments[2] == " ", "label arg was '\(built.arguments[2])'")
        }

        // MARK: MAC21 — DiskutilDriveService.parseCandidates fixtures
        //
        // Pin the post-format mount-discovery fix: after `diskutil
        // eraseDisk` lays down a partition table + ExFAT volume, the
        // new volume mounts on a child partition and the parent's own
        // MountPoint stays empty. Pre-fix the parser only read
        // MountPoint from the parent — the post-format flow tripped
        // "Selected drive has no mount point after format." every
        // time. These fixtures exercise the static parser seam without
        // a real disk.

        runner.test("MAC21: partitioned disk surfaces child partition mount") {
            let data = try loadDiskutilFixture("diskutil-list-partitioned.plist")
            let infoLookup: (String) -> [String: Any]? = { id in
                guard id == "disk4" else { return nil }
                // Parent's info post-format: VolumeName / MountPoint
                // empty (the volume is on the child), but Internal,
                // Ejectable, TotalSize, MediaName are populated.
                return [
                    "Internal": false,
                    "Ejectable": true,
                    "Removable": true,
                    "TotalSize": Int64(32_016_367_616),
                    "MediaName": "USB SSD Media",
                    "IORegistryEntryName": "USB SSD",
                    "VolumeName": "",
                    "MountPoint": "",
                    "Content": "FDisk_partition_scheme"
                ] as [String: Any]
            }
            let candidates = try DiskutilDriveService.parseCandidates(
                listPlistData: data, infoLookup: infoLookup)
            try expect(candidates.count == 1, "expected 1 candidate, got \(candidates.count)")
            let c = candidates[0]
            try expect(c.identifier == "disk4", "parent identifier was \(c.identifier)")
            try expect(c.totalSizeBytes == 32_016_367_616, "size was \(c.totalSizeBytes)")
            try expect(c.mountedPartition?.identifier == "disk4s1",
                       "expected child disk4s1, got \(String(describing: c.mountedPartition?.identifier))")
            try expect(c.mountedPartition?.mountPoint.path == "/Volumes/FREEAI",
                       "child mount path was \(String(describing: c.mountedPartition?.mountPoint.path))")
            try expect(c.mountedPartition?.volumeName == "FREEAI",
                       "child volume name was \(String(describing: c.mountedPartition?.volumeName))")
            try expect(c.mountedPartition?.sizeBytes == 32_014_270_464,
                       "child size was \(String(describing: c.mountedPartition?.sizeBytes))")
            // Computed mountPoint should mirror the child's mount —
            // PrepViewModel.runStaging reads this directly.
            try expect(c.mountPoint?.path == "/Volumes/FREEAI",
                       "computed mountPoint was \(String(describing: c.mountPoint?.path))")
            // Display name falls back to the child's volume name when
            // the parent's VolumeName is empty post-format.
            try expect(c.displayName == "FREEAI",
                       "display name was \(c.displayName)")
        }

        runner.test("MAC21: whole-disk volume falls back to parent mount") {
            let data = try loadDiskutilFixture("diskutil-list-wholedisk.plist")
            let infoLookup: (String) -> [String: Any]? = { id in
                guard id == "disk5" else { return nil }
                return [
                    "Internal": false,
                    "Ejectable": true,
                    "TotalSize": Int64(500_107_862_016),
                    "MediaName": "External Portable SSD",
                    "VolumeName": "PORTABLE",
                    "MountPoint": "/Volumes/PORTABLE"
                ] as [String: Any]
            }
            let candidates = try DiskutilDriveService.parseCandidates(
                listPlistData: data, infoLookup: infoLookup)
            try expect(candidates.count == 1, "expected 1 candidate, got \(candidates.count)")
            let c = candidates[0]
            try expect(c.identifier == "disk5")
            // Whole-disk fallback: mounted partition takes the parent
            // identifier so format calls (which use `c.identifier`) and
            // staging calls (which use `c.mountPoint`) both target the
            // right thing.
            try expect(c.mountedPartition?.identifier == "disk5",
                       "expected parent fallback id, got \(String(describing: c.mountedPartition?.identifier))")
            try expect(c.mountPoint?.path == "/Volumes/PORTABLE")
            try expect(c.displayName == "PORTABLE")
        }

        runner.test("MAC21: internal disks excluded") {
            // Inline-synthesized list output containing one external +
            // one internal entry. Internal must be filtered.
            let listData = try plistFromAllDisks([
                ["DeviceIdentifier": "disk2"],
                ["DeviceIdentifier": "disk3"]
            ])
            let infoLookup: (String) -> [String: Any]? = { id in
                if id == "disk2" {
                    return ["Internal": true, "TotalSize": Int64(1_000_000_000)] as [String: Any]
                }
                if id == "disk3" {
                    return [
                        "Internal": false,
                        "TotalSize": Int64(2_000_000_000),
                        "VolumeName": "EXT",
                        "MountPoint": "/Volumes/EXT"
                    ] as [String: Any]
                }
                return nil
            }
            let candidates = try DiskutilDriveService.parseCandidates(
                listPlistData: listData, infoLookup: infoLookup)
            try expect(candidates.count == 1, "expected 1 (Internal disk2 filtered), got \(candidates.count)")
            try expect(candidates[0].identifier == "disk3")
        }

        runner.test("MAC21: unmounted partitioned disk yields nil mountedPartition") {
            // Pre-format raw disk: partitions exist but none are
            // mounted yet (e.g. fresh-out-of-box drive). Parser must
            // return a candidate with mountedPartition=nil so the UI
            // surfaces "format required" without claiming a phantom
            // mount.
            let listData = try plistFromAllDisks([
                [
                    "DeviceIdentifier": "disk6",
                    "Size": Int64(64_000_000_000),
                    "Partitions": [
                        ["DeviceIdentifier": "disk6s1", "MountPoint": "", "Size": Int64(63_900_000_000)]
                    ]
                ]
            ])
            let infoLookup: (String) -> [String: Any]? = { id in
                guard id == "disk6" else { return nil }
                return [
                    "Internal": false,
                    "TotalSize": Int64(64_000_000_000),
                    "MediaName": "Cheap USB Drive"
                ] as [String: Any]
            }
            let candidates = try DiskutilDriveService.parseCandidates(
                listPlistData: listData, infoLookup: infoLookup)
            try expect(candidates.count == 1)
            try expect(candidates[0].mountedPartition == nil,
                       "expected nil mount, got \(String(describing: candidates[0].mountedPartition))")
            try expect(candidates[0].mountPoint == nil)
        }

        // MARK: PrepFlowStep equality / case-coverage smoke

        runner.test("PrepFlowStep equality") {
            try expect(PrepFlowStep.welcome == .welcome)
            try expect(PrepFlowStep.failed(message: "x") == .failed(message: "x"))
            try expect(PrepFlowStep.failed(message: "a") != .failed(message: "b"))
        }

        // MARK: MAC31a — .modelPullPaused state pins
        //
        // pullStarterModels at PrepViewModel.swift used to fall through to
        // .readiness on cancellation, which buried MAC31's resume seed.
        // The new step preserves the cancelled tag + last progress
        // snapshot so the UI can offer Retry. Equatable comparing the
        // associated values is what `currentStep == .modelPullPaused(...)`
        // checks rely on for diffing.

        runner.test("MAC31a: .modelPullPaused equality matches on tag + snapshot") {
            let a = PrepFlowStep.modelPullPaused(tag: "llama3.2:1b",
                                                 progressSnapshot: "Pulling llama3.2:1b… 42%")
            let b = PrepFlowStep.modelPullPaused(tag: "llama3.2:1b",
                                                 progressSnapshot: "Pulling llama3.2:1b… 42%")
            try expect(a == b)
        }

        runner.test("MAC31a: .modelPullPaused inequality on tag drift") {
            let a = PrepFlowStep.modelPullPaused(tag: "llama3.2:1b", progressSnapshot: nil)
            let b = PrepFlowStep.modelPullPaused(tag: "llama3.2:3b", progressSnapshot: nil)
            try expect(a != b)
        }

        runner.test("MAC31a: .modelPullPaused inequality on snapshot drift") {
            let a = PrepFlowStep.modelPullPaused(tag: "x", progressSnapshot: "10%")
            let b = PrepFlowStep.modelPullPaused(tag: "x", progressSnapshot: "20%")
            let c = PrepFlowStep.modelPullPaused(tag: "x", progressSnapshot: nil)
            try expect(a != b)
            try expect(a != c)
        }

        runner.test("MAC31a: .modelPullPaused not equal to .modelPull or .readiness") {
            let paused = PrepFlowStep.modelPullPaused(tag: "x", progressSnapshot: nil)
            try expect(paused != .modelPull)
            try expect(paused != .readiness)
        }

        // MARK: M15 — .modelPullFailed state pins

        runner.test("M15: .modelPullFailed equality matches failed tags") {
            let a = PrepFlowStep.modelPullFailed(tags: ["llama3.2:1b", "qwen2.5:0.5b"])
            let b = PrepFlowStep.modelPullFailed(tags: ["llama3.2:1b", "qwen2.5:0.5b"])
            try expect(a == b)
        }

        runner.test("M15: .modelPullFailed inequality on tag drift") {
            let a = PrepFlowStep.modelPullFailed(tags: ["llama3.2:1b"])
            let b = PrepFlowStep.modelPullFailed(tags: ["llama3.2:3b"])
            try expect(a != b)
        }

        runner.test("M15: .modelPullFailed not equal to paused or readiness") {
            let failed = PrepFlowStep.modelPullFailed(tags: ["x"])
            try expect(failed != .modelPullPaused(tag: "x", progressSnapshot: nil))
            try expect(failed != .readiness)
        }

        // MARK: #338 — web-UI access mode (device-only vs LAN) parity
        //
        // Pins the Swift port of the Windows PrepViewModel access-mode rules
        // (PR #338): LAN forces encryption on; device-only preserves the
        // user's encryption choice; the encrypt toggle is locked in LAN mode;
        // and the Done-step API-key panel is gated to LAN + a non-empty key.
        // Exercises the pure helpers in PrepFlowStep.swift so the rules are
        // covered without constructing the @MainActor PrepViewModel (same
        // approach as the F2a / M11 filter tests below).

        runner.test("#338: device-only preserves the user's encryption choice") {
            let offKept = resolveAccessMode(
                selecting: .deviceOnly, lanConfirmed: false, currentEncryption: false)
            try expect(offKept.mode == .deviceOnly, "mode: \(offKept.mode)")
            try expect(offKept.enableEncryption == false, "encryption should stay off")

            let onKept = resolveAccessMode(
                selecting: .deviceOnly, lanConfirmed: false, currentEncryption: true)
            try expect(onKept.mode == .deviceOnly, "mode: \(onKept.mode)")
            try expect(onKept.enableEncryption == true,
                       "an opted-in at-rest encryption choice should be preserved")
        }

        runner.test("#338: confirmed LAN forces encryption on") {
            let r = resolveAccessMode(
                selecting: .lan, lanConfirmed: true, currentEncryption: false)
            try expect(r.mode == .lan, "mode: \(r.mode)")
            try expect(r.enableEncryption == true, "LAN must force encryption on")
        }

        runner.test("#338: cancelled LAN confirm leaves everything unchanged") {
            let r = resolveAccessMode(
                selecting: .lan, lanConfirmed: false, currentEncryption: false)
            try expect(r.mode == .deviceOnly,
                       "cancel should snap back to device-only, got \(r.mode)")
            try expect(r.enableEncryption == false,
                       "cancel must not force encryption on")
        }

        runner.test("#338: encryption toggle is locked only in LAN mode") {
            try expect(accessEncryptionToggleEnabled(for: .deviceOnly) == true,
                       "device-only should allow editing the toggle")
            try expect(accessEncryptionToggleEnabled(for: .lan) == false,
                       "LAN should lock the toggle")
        }

        runner.test("#338: Done-step API key surfaces only for LAN with a key") {
            try expect(accessShowFinalizedApiKey(mode: .lan, key: "deadbeef") == true,
                       "LAN + non-empty key should surface")
            try expect(accessShowFinalizedApiKey(mode: .deviceOnly, key: "deadbeef") == false,
                       "device-only must never surface the key panel")
            try expect(accessShowFinalizedApiKey(mode: .lan, key: nil) == false,
                       "nil key should not surface")
            try expect(accessShowFinalizedApiKey(mode: .lan, key: "") == false,
                       "empty key should not surface")
        }

        // MARK: #91 / task #92 — optional staging commands (OCR opt-in)
        //
        // Pins the pure mapping from the staging opt-in toggle to the list of
        // `stage-*` sidecar arms PrepViewModel.runStaging() emits after the core
        // arms. The Tesseract OCR fast-follow (#342) added the stage-tesseract
        // arm; these guard that installOcr emits it and nothing fires when off.
        // (Mac Piper staging was removed — the Mac runner never consumed it; see
        // PrepFlowStep.) Same pure-helper approach as the #338 access-mode tests.

        runner.test("#91: no optional staging commands when OCR off") {
            try expect(optionalStagingCommands(installOcr: false) == [],
                       "expected no commands when nothing opted in")
        }

        runner.test("#91: installOcr emits stage-tesseract") {
            try expect(optionalStagingCommands(installOcr: true) == ["stage-tesseract"],
                       "got \(optionalStagingCommands(installOcr: true))")
        }

        // MARK: MAC34 — InitialPortableConfigPayload generates a non-empty
        // 64-hex network API key by default. Pre-MAC34 this defaulted to
        // `""` which fail-closed every chat request through the LAN API
        // path with `503 API key is required by configuration but not set
        // on host.` See .memhub/rendered/PROJECT_LEDGER.md (MAC34).

        runner.test("MAC34: InitialPortableConfigPayload generates 64-hex networkApiKey by default") {
            let a = InitialPortableConfigPayload()
            try expect(!a.networkApiKey.isEmpty,
                       "default networkApiKey should not be empty")
            try expect(a.networkApiKey.count == 64,
                       "expected 64 hex chars, got \(a.networkApiKey.count): \(a.networkApiKey)")
            // Must be lowercase hex.
            let allowed = Set("0123456789abcdef")
            for ch in a.networkApiKey {
                try expect(allowed.contains(ch),
                           "non-hex char \(ch) in key \(a.networkApiKey)")
            }
        }

        runner.test("MAC34: InitialPortableConfigPayload key differs between instances") {
            // Defense check that the default isn't a baked-in constant —
            // every freshly-constructed payload should pull fresh OS RNG.
            let a = InitialPortableConfigPayload()
            let b = InitialPortableConfigPayload()
            try expect(a.networkApiKey != b.networkApiKey,
                       "two payloads produced the same networkApiKey: \(a.networkApiKey)")
        }

        runner.test("MAC34: InitialPortableConfigPayload allows explicit override") {
            // The MAC17 fixture writer hands `networkApiKey: ""` deliberately
            // so the cross-language fixture stays bit-stable. Confirm an
            // explicit override still wins over the default generator.
            let a = InitialPortableConfigPayload(networkApiKey: "")
            try expect(a.networkApiKey == "",
                       "explicit '' override should win, got '\(a.networkApiKey)'")
        }

        // MARK: MAC30 — PlaintextConfigWriter writes a parseable
        // portable-config.json that PortableConfig.cs can deserialize, with
        // the networkApiKey field cleared (plaintext invariant: no API key
        // ever lands on disk in cleartext).

        runner.test("MAC30: PlaintextConfigWriter writes parseable portable-config.json") {
            let tempRoot = FileManager.default.temporaryDirectory
                .appendingPathComponent("mac30-plaintext-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: tempRoot) }

            let writer = PlaintextConfigWriter()
            try writer.writeInitialPlaintextConfig(
                ssdRoot: tempRoot, payload: InitialPortableConfigPayload())

            let configURL = tempRoot
                .appendingPathComponent("config")
                .appendingPathComponent("portable-config.json")
            try expect(FileManager.default.fileExists(atPath: configURL.path),
                       "expected portable-config.json at \(configURL.path)")

            let data = try Data(contentsOf: configURL)
            let parsed = try JSONSerialization.jsonObject(with: data)
            guard let dict = parsed as? [String: Any] else {
                try expect(false, "expected JSON object root")
                return
            }
            // Every camelCase key PortableConfig.cs reads must be present.
            for key in ["ollamaPort", "networkModeEnabled", "networkBindAddress",
                        "networkPort", "networkRequireApiKey", "networkApiKey",
                        "preferredCompute", "models"] {
                try expect(dict[key] != nil, "missing key: \(key)")
            }
        }

        runner.test("MAC30: PlaintextConfigWriter clears networkApiKey on disk") {
            // The plaintext invariant: even if InitialPortableConfigPayload
            // generated a random key, the writer must zero it before write.
            // Otherwise a LAN secret would land on disk in cleartext.
            let tempRoot = FileManager.default.temporaryDirectory
                .appendingPathComponent("mac30-key-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: tempRoot) }

            let payload = InitialPortableConfigPayload()  // generates random key
            try expect(!payload.networkApiKey.isEmpty,
                       "precondition: payload should generate a key in memory")

            try PlaintextConfigWriter().writeInitialPlaintextConfig(
                ssdRoot: tempRoot, payload: payload)

            let configURL = tempRoot
                .appendingPathComponent("config")
                .appendingPathComponent("portable-config.json")
            let data = try Data(contentsOf: configURL)
            let dict = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            let onDiskKey = dict?["networkApiKey"] as? String
            try expect(onDiskKey == "",
                       "networkApiKey on disk should be empty, got: '\(onDiskKey ?? "nil")'")
        }

        runner.test("MAC30: PlaintextConfigWriter creates config directory if missing") {
            // Fresh SSD has no config/ directory yet — writer must mkdir -p.
            let tempRoot = FileManager.default.temporaryDirectory
                .appendingPathComponent("mac30-mkdir-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: tempRoot) }

            // Note: tempRoot itself doesn't exist either at this point.
            try PlaintextConfigWriter().writeInitialPlaintextConfig(
                ssdRoot: tempRoot, payload: InitialPortableConfigPayload())

            let configDir = tempRoot.appendingPathComponent("config")
            var isDir: ObjCBool = false
            let exists = FileManager.default.fileExists(atPath: configDir.path,
                                                        isDirectory: &isDir)
            try expect(exists && isDir.boolValue,
                       "expected config/ to exist as a directory")
        }

        // MARK: PrepHostController cancel-path tests (MAC17a Issue #1)
        //
        // Pin the bug fix: on timeout the pending continuation slot must
        // be freed and the command name re-usable. Pre-fix, the slot
        // leaked and a re-send under the same name would either trap on
        // double-resume or silently overwrite the leaked continuation.
        // Drives the internal `awaitCommandResult` seam directly so we
        // don't need a real spawned sidecar.

        runner.test("Issue #1: send timeout frees pending result slot") {
            let controller = PrepHostController()
            var caught: Error?
            do {
                _ = try await controller.awaitCommandResult(
                    commandName: "test-cmd",
                    timeout: 0.05,
                    dispatch: {})
            } catch {
                caught = error
            }
            try expect(caught != nil, "expected timeout/cancel to throw")
            try expect(controller.pendingResultsCountForTest == 0,
                       "pending slot leaked: count=\(controller.pendingResultsCountForTest)")
        }

        // MARK: PR #195 review fixes — Fix 4 (deinit drains pending)
        //
        // Pin the behavior that the controller's deinit now relies on:
        // failAllPending drains a registered pending continuation and
        // resumes it with the supplied error. Pre-fix, deinit would
        // call only process?.terminate(); the terminationHandler
        // captures [weak self] so it no-ops once self is gone, and any
        // in-flight CheckedContinuation in pendingResults would trap
        // on its own dealloc. Driven via a Task that registers via
        // awaitCommandResult, then invoked synchronously through the
        // failAllPendingForTest seam (matching what deinit now does).

        runner.test("Fix 4: failAllPending drains pending continuations") {
            let controller = PrepHostController()

            let task = Task { () -> String in
                do {
                    _ = try await controller.awaitCommandResult(
                        commandName: "drain-me",
                        timeout: 60.0,
                        dispatch: {})
                    return "completed"
                } catch let e as PrepHostError {
                    switch e {
                    case .notRunning: return "notRunning"
                    default:          return "other-prep:\(e)"
                    }
                } catch is CancellationError {
                    return "cancelled"
                } catch {
                    return "other:\(error)"
                }
            }

            // Yield long enough for the addTask body to run the
            // registration `queue.sync` block and seat the
            // continuation in pendingResults.
            for _ in 0..<20 {
                try? await Task.sleep(nanoseconds: 10_000_000) // 10ms
                if controller.pendingResultsCountForTest == 1 { break }
            }
            try expect(controller.pendingResultsCountForTest == 1,
                       "expected 1 pending registration before drain, got \(controller.pendingResultsCountForTest)")

            // Simulate the deinit drain.
            controller.failAllPendingForTest(PrepHostError.notRunning)

            let outcome = await task.value
            try expect(outcome == "notRunning",
                       "expected notRunning after drain, got \(outcome)")
            try expect(controller.pendingResultsCountForTest == 0,
                       "slot leaked after drain: count=\(controller.pendingResultsCountForTest)")
        }

        runner.test("Issue #1: same command name is re-usable after timeout") {
            let controller = PrepHostController()

            // First call: time out, slot must clear.
            _ = try? await controller.awaitCommandResult(
                commandName: "cmd-x",
                timeout: 0.05,
                dispatch: {})
            try expect(controller.pendingResultsCountForTest == 0,
                       "first-call slot leaked")

            // Second call with the same command name must not crash on
            // re-registration. Pre-fix, the slot still held the stale
            // continuation and the next stdout `result:` line would
            // try to resume it, trapping on double-resume in real use.
            var caught: Error?
            do {
                _ = try await controller.awaitCommandResult(
                    commandName: "cmd-x",
                    timeout: 0.05,
                    dispatch: {})
            } catch {
                caught = error
            }
            try expect(caught != nil, "second-call expected timeout/cancel")
            try expect(controller.pendingResultsCountForTest == 0,
                       "second-call slot leaked")
        }

        // MARK: F2a — picker filter (search + most-popular cap)

        runner.test("F2a: empty search returns the source list unchanged") {
            let entries = makeF2aFixture()
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: false, popularLimit: 15)
            try expect(out.count == entries.count, "got \(out.count)")
        }

        runner.test("F2a: search matches against tag, sizeTier, and bestAt") {
            let entries = makeF2aFixture()
            let byTag = applyStarterModelFilters(
                to: entries, search: "llama", showOnlyMostPopular: false, popularLimit: 15)
            try expect(byTag.allSatisfy { $0.tag.contains("llama") },
                       "byTag had non-llama entries: \(byTag.map(\.tag))")
            try expect(byTag.count >= 2, "byTag count: \(byTag.count)")

            let byBestAt = applyStarterModelFilters(
                to: entries, search: "REASONING",   // case-insensitive
                showOnlyMostPopular: false, popularLimit: 15)
            try expect(byBestAt.contains(where: { $0.tag == "qwen2.5:7b" }),
                       "byBestAt missing qwen2.5:7b: \(byBestAt.map(\.tag))")

            let bySize = applyStarterModelFilters(
                to: entries, search: "Large", showOnlyMostPopular: false, popularLimit: 15)
            try expect(bySize.allSatisfy { $0.sizeTier == "Large" },
                       "bySize had non-Large: \(bySize.map(\.sizeTier))")
        }

        runner.test("F2a: search trims leading/trailing whitespace") {
            let entries = makeF2aFixture()
            let trimmed = applyStarterModelFilters(
                to: entries, search: "   llama  ", showOnlyMostPopular: false, popularLimit: 15)
            let plain = applyStarterModelFilters(
                to: entries, search: "llama", showOnlyMostPopular: false, popularLimit: 15)
            try expect(trimmed.map(\.tag) == plain.map(\.tag),
                       "trimmed=\(trimmed.map(\.tag)) plain=\(plain.map(\.tag))")
        }

        runner.test("F2a: most-popular sorts desc by pull count and caps at limit") {
            let entries = makeF2aFixture()
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: true, popularLimit: 3)
            try expect(out.count == 3, "expected 3 (cap), got \(out.count)")
            // makeF2aFixture popularity ranking: gemma2:2b (200M) >
            // llama3.2:1b (114M) > llama3.2:3b (90M) > qwen2.5:7b (50M).
            try expect(out.map(\.tag) == ["gemma2:2b", "llama3.2:1b", "llama3.2:3b"],
                       "ranking drift: \(out.map(\.tag))")
        }

        runner.test("F2a: most-popular drops entries without a pull count") {
            let entries = makeF2aFixture()
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: true, popularLimit: 15)
            try expect(out.allSatisfy { $0.pullCount != nil },
                       "popular set included nil-pullCount entry: \(out.map { ($0.tag, $0.pullCount as Any) })")
            try expect(!out.contains(where: { $0.tag == "bundled-only:1b" }),
                       "expected bundled-only:1b excluded; got \(out.map(\.tag))")
        }

        runner.test("F2a: search and popular compose (search first, then top-N)") {
            let entries = makeF2aFixture()
            let out = applyStarterModelFilters(
                to: entries, search: "llama", showOnlyMostPopular: true, popularLimit: 15)
            try expect(out.allSatisfy { $0.tag.contains("llama") },
                       "composed result had non-llama entries: \(out.map(\.tag))")
            // Composed: llama3.2:1b (114M) > llama3.2:3b (90M); the
            // 200M gemma2:2b is filtered out by the search clause.
            try expect(out.map(\.tag) == ["llama3.2:1b", "llama3.2:3b"],
                       "composed ranking drift: \(out.map(\.tag))")
        }

        // MARK: M11 — visible-row caption

        runner.test("M11: caption empty when no filter and no search") {
            let s = formatStarterRowCountCaption(
                visible: 399, total: 399, showOnlyMostPopular: false, hasSearch: false)
            try expect(s.isEmpty, "expected empty, got: \(s)")
        }

        runner.test("M11: caption empty when total is zero") {
            let s = formatStarterRowCountCaption(
                visible: 0, total: 0, showOnlyMostPopular: true, hasSearch: false)
            try expect(s.isEmpty, "expected empty, got: \(s)")
        }

        runner.test("M11: caption announces top-N cap when popular only") {
            let s = formatStarterRowCountCaption(
                visible: 15, total: 399, showOnlyMostPopular: true, hasSearch: false)
            try expect(s == "Showing top 15 of 399 by pulls.", "got: \(s)")
        }

        runner.test("M11: caption notes search filter when search only") {
            let s = formatStarterRowCountCaption(
                visible: 12, total: 399, showOnlyMostPopular: false, hasSearch: true)
            try expect(s == "Showing 12 of 399 matching search.", "got: \(s)")
        }

        runner.test("M11: caption combines popular + search") {
            let s = formatStarterRowCountCaption(
                visible: 4, total: 399, showOnlyMostPopular: true, hasSearch: true)
            try expect(s == "Showing top 4 of 399 by pulls (filtered by search).", "got: \(s)")
        }

        // MARK: C3 — parameter cap

        runner.test("C3: parameter cap drops entries above the cap") {
            let entries = makeC3C4C5Fixture()
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: false, popularLimit: 15,
                maxParametersBillion: 14)
            // 7B and 14B pass; 70B drops; bundled (nil params) passes through.
            try expect(out.allSatisfy { ($0.parametersBillion ?? 0) <= 14 || $0.parametersBillion == nil },
                       "got params \(out.map { ($0.tag, $0.parametersBillion as Any) })")
            try expect(!out.contains(where: { $0.tag == "deepseek-r1:70b" }),
                       "70B should have been dropped: \(out.map(\.tag))")
            try expect(out.contains(where: { $0.tag == "bundled-only:1b" }),
                       "nil-params bundled entry should pass through: \(out.map(\.tag))")
        }

        runner.test("C3: nil cap is a no-op") {
            let entries = makeC3C4C5Fixture()
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: false, popularLimit: 15,
                maxParametersBillion: nil)
            try expect(out.count == entries.count, "got \(out.count) of \(entries.count)")
        }

        // MARK: C4 — capability AND filter

        runner.test("C4: capability filter requires every selected cap (AND)") {
            let entries = makeC3C4C5Fixture()
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: false, popularLimit: 15,
                requiredCapabilities: ["tools", "vision"])
            // Only multi-tool:8b carries both tools+vision in the fixture.
            // bundled-only has empty caps → passes through.
            try expect(out.contains(where: { $0.tag == "multi-tool:8b" }),
                       "multi-tool:8b missing: \(out.map(\.tag))")
            try expect(out.contains(where: { $0.tag == "bundled-only:1b" }),
                       "empty-caps bundled entry should pass through: \(out.map(\.tag))")
            try expect(!out.contains(where: { $0.tag == "tools-only:7b" }),
                       "tools-only entry should be excluded by AND: \(out.map(\.tag))")
        }

        runner.test("C4: empty capability set is a no-op") {
            let entries = makeC3C4C5Fixture()
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: false, popularLimit: 15,
                requiredCapabilities: [])
            try expect(out.count == entries.count, "got \(out.count) of \(entries.count)")
        }

        // MARK: C5 — sort by newest

        runner.test("C5: sort by newest orders by lastUpdated desc, nils last") {
            let entries = makeC3C4C5Fixture()
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: false, popularLimit: 15,
                sortMode: .newest)
            // Fixture lastUpdated: multi-tool 2026-05-08, tools-only 2026-04-01,
            // bundled-only nil. Verify the first non-nil entry is multi-tool.
            let firstNonNil = out.first { $0.lastUpdated != nil }
            try expect(firstNonNil?.tag == "multi-tool:8b",
                       "expected multi-tool:8b first, got \(firstNonNil?.tag ?? "nil")")
            // bundled-only:1b has nil lastUpdated → must sort to the end.
            try expect(out.last?.tag == "bundled-only:1b",
                       "nil-lastUpdated should sort last, got \(out.last?.tag ?? "nil")")
        }

        runner.test("C5: alphabetical sort orders by tag ascending") {
            let entries = makeC3C4C5Fixture()
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: false, popularLimit: 15,
                sortMode: .alphabetical)
            let tags = out.map(\.tag)
            let sorted = tags.sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
            try expect(tags == sorted, "tags=\(tags) sorted=\(sorted)")
        }

        // MARK: C24 — refresh-catalog wire-format pin
        //
        // Regression pin for the PR #259 host omission: mac-prep-host
        // refresh-catalog projected `tag/params/sizeTier/useCases/pullCount`
        // but dropped `parametersBillion` and `lastUpdated`, so Max-size
        // and Sort: Newest were no-ops on Mac after Refresh. Drives the
        // payload through decodeStarterEntries (the exact path
        // PrepViewModel.refreshFromOllama uses) and asserts the new
        // fields survive the round-trip into the typed entries.

        runner.test("C24: refresh-catalog payload exposes parametersBillion to the cap filter") {
            let payload = makeC24RefreshCatalogPayload()
            let decoded = decodeStarterEntries(from: payload)
            try expect(decoded.count == 3, "got \(decoded.count) entries")
            // Round-trip check — the regression manifested as nil here.
            let big = decoded.first { $0.tag == "deepseek-r1:30b" }
            try expect(big?.parametersBillion == 30.0,
                       "parametersBillion missing from wire: \(big?.parametersBillion as Any)")
            let display = decoded.map(StarterModelDisplayEntry.from)
            let out = applyStarterModelFilters(
                to: display, search: "", showOnlyMostPopular: false, popularLimit: 15,
                maxParametersBillion: 7)
            try expect(!out.contains(where: { $0.tag == "deepseek-r1:30b" }),
                       "30B should drop under ≤7B cap: \(out.map(\.tag))")
            try expect(out.contains(where: { $0.tag == "qwen2.5:7b" }),
                       "7B should survive: \(out.map(\.tag))")
        }

        runner.test("C24: refresh-catalog payload exposes lastUpdated to the newest sort") {
            let payload = makeC24RefreshCatalogPayload()
            let decoded = decodeStarterEntries(from: payload)
            // Round-trip check — the regression manifested as nil here.
            let newer = decoded.first { $0.tag == "qwen2.5:7b" }
            try expect(newer?.lastUpdated == "2026-05-08T00:00:00+00:00",
                       "lastUpdated missing from wire: \(newer?.lastUpdated as Any)")
            let display = decoded.map(StarterModelDisplayEntry.from)
            let out = applyStarterModelFilters(
                to: display, search: "", showOnlyMostPopular: false, popularLimit: 15,
                sortMode: .newest)
            let firstNonNil = out.first { $0.lastUpdated != nil }
            try expect(firstNonNil?.tag == "qwen2.5:7b",
                       "newest non-nil expected qwen2.5:7b, got \(firstNonNil?.tag ?? "nil")")
        }

        // MARK: caption — new branches

        runner.test("caption: parameter-cap-only emits matching-filter line") {
            let s = formatStarterRowCountCaption(
                visible: 8, total: 12, showOnlyMostPopular: false, hasSearch: false,
                maxParametersBillion: 14)
            try expect(s == "Showing 8 of 12 matching filter (≤14B).", "got: \(s)")
        }

        runner.test("caption: capabilities + cap compose alphabetically") {
            let s = formatStarterRowCountCaption(
                visible: 3, total: 12, showOnlyMostPopular: false, hasSearch: false,
                maxParametersBillion: 7,
                requiredCapabilities: ["vision", "tools"])
            try expect(s == "Showing 3 of 12 matching filter (≤7B, tools+vision).", "got: \(s)")
        }

        runner.test("caption: newest sort appends sentence") {
            let s = formatStarterRowCountCaption(
                visible: 12, total: 12, showOnlyMostPopular: false, hasSearch: false,
                sortMode: .newest)
            try expect(s == "Sorted by newest.", "got: \(s)")
        }

        runner.test("caption: popular + filter + newest combine") {
            let s = formatStarterRowCountCaption(
                visible: 5, total: 200, showOnlyMostPopular: true, hasSearch: true,
                maxParametersBillion: 14,
                requiredCapabilities: ["tools"],
                sortMode: .newest)
            try expect(s == "Showing top 5 of 200 by pulls (filtered by search; ≤14B, tools). Sorted by newest.",
                       "got: \(s)")
        }

        // MARK: C26 — Most-popular limit dropdown
        //
        // The dropdown rebinds `popularLimit` on each selection. Pure
        // filter pin: smaller limit shrinks the visible set; larger
        // limit expands it. Matches the WPF code-behind's binding to
        // PrepViewModel.MostPopularLimit.

        runner.test("C26: popularLimit caps the visible top-N (10)") {
            let entries = (0..<30).map { i in
                StarterModelDisplayEntry(
                    tag: "limit:\(i)", sizeTier: "Medium",
                    bestAt: "Variant \(i)",
                    pullCount: Int64(100_000_000 - i * 1_000_000))
            }
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: true, popularLimit: 10)
            try expect(out.count == 10, "expected 10, got \(out.count)")
            try expect(out.first?.tag == "limit:0",
                       "top entry should be limit:0, got \(out.first?.tag ?? "nil")")
        }

        runner.test("C26: popularLimit 25 expands the visible top-N") {
            let entries = (0..<30).map { i in
                StarterModelDisplayEntry(
                    tag: "limit:\(i)", sizeTier: "Medium",
                    bestAt: "Variant \(i)",
                    pullCount: Int64(100_000_000 - i * 1_000_000))
            }
            let out = applyStarterModelFilters(
                to: entries, search: "", showOnlyMostPopular: true, popularLimit: 25)
            try expect(out.count == 25, "expected 25, got \(out.count)")
        }

        // MARK: C25 — capability pass-through marker (computation pin)
        //
        // The marker itself is a SwiftUI .opacity() modifier; the
        // computation that drives it is `entry.capabilities.isEmpty &&
        // !vm.requiredCapabilities.isEmpty`. Pin the boolean so future
        // changes to the chip filter posture surface here.

        runner.test("C25: pass-through flag true when caps empty + chip active") {
            let bundled = StarterModelDisplayEntry(
                tag: "bundled:1b", sizeTier: "Small", bestAt: "Fallback",
                pullCount: nil, capabilities: [],
                parametersBillion: nil, lastUpdated: nil)
            let required: Set<String> = ["tools"]
            let isPassThrough = bundled.capabilities.isEmpty && !required.isEmpty
            try expect(isPassThrough, "expected pass-through marker active")
        }

        runner.test("C25: pass-through flag false when no chip active") {
            let bundled = StarterModelDisplayEntry(
                tag: "bundled:1b", sizeTier: "Small", bestAt: "Fallback",
                pullCount: nil, capabilities: [],
                parametersBillion: nil, lastUpdated: nil)
            let required: Set<String> = []
            let isPassThrough = bundled.capabilities.isEmpty && !required.isEmpty
            try expect(!isPassThrough, "expected no marker — no chip engaged")
        }

        runner.test("C25: pass-through flag false when row has capability data") {
            let live = StarterModelDisplayEntry(
                tag: "live:8b", sizeTier: "Medium", bestAt: "Live entry",
                pullCount: 10_000_000, capabilities: ["tools"],
                parametersBillion: 8, lastUpdated: nil)
            let required: Set<String> = ["tools"]
            let isPassThrough = live.capabilities.isEmpty && !required.isEmpty
            try expect(!isPassThrough, "row has caps; marker must not apply")
        }

        // MARK: C27 Stage 1 — Hugging Face source dropdown wire round-trip

        runner.test("C27: ModelSourceKind.parse defaults to .ollama when missing") {
            // Pre-C27 payloads omit the `source` field. Defensive default
            // so a stale sidecar doesn't break the picker.
            try expect(ModelSourceKind.parse(nil) == .ollama,
                       "nil should default to .ollama")
            try expect(ModelSourceKind.parse("Ollama") == .ollama,
                       "Ollama string should parse to .ollama")
        }

        runner.test("C27: ModelSourceKind.parse maps HuggingFace string") {
            try expect(ModelSourceKind.parse("HuggingFace") == .huggingFace,
                       "HuggingFace string should parse to .huggingFace")
        }

        runner.test("C27: ModelSourceKind.parse falls back to .ollama on unknown") {
            // Future host enum values land here without crashing.
            try expect(ModelSourceKind.parse("WeirdNewSource") == .ollama,
                       "unknown source should fall back to .ollama")
        }

        runner.test("C27: discover-hf-catalog payload decodes with source field") {
            let payload = makeC27HuggingFacePayload()
            let decoded = decodeStarterEntries(from: payload)
            try expect(decoded.count == 2, "got \(decoded.count) entries")
            try expect(decoded.first?.source == "HuggingFace",
                       "source string missing: \(decoded.first?.source as Any)")
        }

        runner.test("C27: StarterModelDisplayEntry.from maps HF source") {
            let payload = makeC27HuggingFacePayload()
            let decoded = decodeStarterEntries(from: payload)
            let display = decoded.map(StarterModelDisplayEntry.from)
            try expect(display.allSatisfy { $0.sourceKind == .huggingFace },
                       "all rows should be .huggingFace: \(display.map { ($0.tag, $0.sourceKind) })")
        }

        runner.test("C27: HF rows with empty capabilities pass through chip filter") {
            // HF anonymous payloads carry no capability tags. The C25
            // pass-through posture means a user narrowing by `tools`
            // still sees HF rows (the row-style opacity marker signals
            // why).
            let payload = makeC27HuggingFacePayload()
            let display = decodeStarterEntries(from: payload).map(StarterModelDisplayEntry.from)
            let out = applyStarterModelFilters(
                to: display, search: "", showOnlyMostPopular: false, popularLimit: 15,
                requiredCapabilities: ["tools"])
            try expect(out.count == display.count,
                       "all HF rows should pass through: \(out.map(\.tag))")
        }

        runner.test("C27: HF rows participate in Most-popular sort") {
            let payload = makeC27HuggingFacePayload()
            let display = decodeStarterEntries(from: payload).map(StarterModelDisplayEntry.from)
            let out = applyStarterModelFilters(
                to: display, search: "", showOnlyMostPopular: true, popularLimit: 5)
            // qwen3 has 245321 downloads, llama has 188204 — qwen ranks first.
            try expect(out.first?.tag == "hf.co/bartowski/Qwen3-8B-GGUF",
                       "qwen3 should rank first by pullCount, got \(out.first?.tag ?? "nil")")
        }

        runner.test("C27: HF rows participate in Newest sort") {
            let payload = makeC27HuggingFacePayload()
            let display = decodeStarterEntries(from: payload).map(StarterModelDisplayEntry.from)
            let out = applyStarterModelFilters(
                to: display, search: "", showOnlyMostPopular: false, popularLimit: 15,
                sortMode: .newest)
            // llama lastModified is later than qwen3.
            try expect(out.first?.tag == "hf.co/lmstudio-community/Llama-3.2-7B-Instruct-GGUF",
                       "llama should sort newest first, got \(out.first?.tag ?? "nil")")
        }

        runner.test("C27 Stage 4: HF entries decode isExpandable=true") {
            let payload = makeC27HuggingFacePayloadStage4()
            let decoded = decodeStarterEntries(from: payload)
            try expect(decoded.allSatisfy { $0.isExpandable == true },
                       "stage 4 sidecar payload should mark every HF row expandable")
            let display = decoded.map(StarterModelDisplayEntry.from)
            try expect(display.allSatisfy { $0.isExpandable },
                       "display rows should carry isExpandable through the projection")
        }

        runner.test("C27 Stage 4: pre-Stage-4 payload defaults isExpandable=false") {
            // Old sidecar (Stage 1/2 era) emits no `isExpandable` field —
            // the `Bool?` decode falls through and `from(_:)` resolves to
            // false so the chevron stays hidden.
            let payload = makeC27HuggingFacePayload()
            let display = decodeStarterEntries(from: payload).map(StarterModelDisplayEntry.from)
            try expect(display.allSatisfy { !$0.isExpandable },
                       "pre-Stage-4 payload should leave rows non-expandable")
        }

        runner.test("C27 Stage 4: quant child constructor wires parent + size") {
            // Pin the Swift display constructor's quant-child branch:
            // parentRepoId + quantLabel + size flow through and isQuantChild
            // is true. Mirrors the C# `IsQuantChild` predicate.
            let child = StarterModelDisplayEntry(
                tag: "hf.co/Qwen/Qwen3-8B-GGUF:Q4_K_M",
                sizeTier: "Custom",
                bestAt: "Q4_K_M",
                pullCount: 245321,
                capabilities: [],
                parametersBillion: nil,
                lastUpdated: nil,
                sourceKind: .huggingFace,
                isExpandable: false,
                parentRepoId: "Qwen/Qwen3-8B-GGUF",
                quantLabel: "Q4_K_M",
                quantSizeBytes: 4_500_000_000)
            try expect(child.isQuantChild, "expected isQuantChild to be true")
            try expect(child.quantLabel == "Q4_K_M", "quant label mismatch")
            try expect(child.quantSizeBytes == 4_500_000_000, "size mismatch")
        }

        runner.test("C27: HF rows with nil parametersBillion pass through cap filter") {
            // The HF service emits a best-effort parametersBillion from
            // the repo id (e.g., "8B" → 8.0). A repo without a numeric
            // hint must still survive the ≤7B cap so users searching by
            // model family aren't accidentally hidden.
            let nilParamHfRow = StarterModelDisplayEntry(
                tag: "hf.co/owner/Mystery-Model-GGUF",
                sizeTier: "Custom", bestAt: "",
                pullCount: 100, capabilities: [],
                parametersBillion: nil, lastUpdated: nil,
                sourceKind: .huggingFace)
            let out = applyStarterModelFilters(
                to: [nilParamHfRow], search: "", showOnlyMostPopular: false,
                popularLimit: 15, maxParametersBillion: 7)
            try expect(out.count == 1, "nil-params HF row should pass through cap filter")
        }

        // MARK: C6 — DriveConfigurationDetector Swift parity

        runner.test("C6: detect(nil) returns empty snapshot") {
            let snap = DriveConfigurationDetector.detect(nil)
            try expect(snap == DriveConfigurationSnapshot.empty,
                       "nil root should map to empty snapshot")
            try expect(snap.state == .unconfigured, "state should be .unconfigured")
        }

        runner.test("C6: detect(missing directory) returns empty snapshot") {
            let url = URL(fileURLWithPath: NSTemporaryDirectory())
                .appendingPathComponent("free-ai-ssd-tests")
                .appendingPathComponent(UUID().uuidString)
            // Deliberately don't create it.
            let snap = DriveConfigurationDetector.detect(url)
            try expect(snap == DriveConfigurationSnapshot.empty,
                       "missing dir should map to empty snapshot")
        }

        runner.test("C6: detect(empty directory) returns unconfigured") {
            let root = try makeC6TempRoot()
            defer { cleanupC6TempRoot(root) }
            let snap = DriveConfigurationDetector.detect(root)
            try expect(snap.state == .unconfigured, "empty dir → unconfigured")
            try expect(snap.hasOurConfig == false, "hasOurConfig must be false")
            try expect(snap.modelManifestCount == 0, "manifest count must be 0")
        }

        runner.test("C6: detect(plaintext config only) returns configuredEmpty, not encrypted") {
            let root = try makeC6TempRoot()
            defer { cleanupC6TempRoot(root) }
            try writeC6Config(at: root, plaintext: true, encrypted: false)
            let snap = DriveConfigurationDetector.detect(root)
            try expect(snap.state == .configuredEmpty, "plaintext config alone → configuredEmpty")
            try expect(snap.hasOurConfig == true, "hasOurConfig must be true")
            try expect(snap.isConfigEncrypted == false, "isConfigEncrypted must be false")
            try expect(snap.hasModels == false, "hasModels must be false")
        }

        runner.test("C6: detect(encrypted config only) returns configuredEmpty + isEncrypted") {
            let root = try makeC6TempRoot()
            defer { cleanupC6TempRoot(root) }
            try writeC6Config(at: root, plaintext: false, encrypted: true)
            let snap = DriveConfigurationDetector.detect(root)
            try expect(snap.state == .configuredEmpty, "encrypted config alone → configuredEmpty")
            try expect(snap.isConfigEncrypted == true, "isConfigEncrypted must be true")
        }

        runner.test("C6: detect(both configs) prefers plaintext signal") {
            // Mid-migration edge: both files present briefly. Plaintext-newer
            // is the unlock-not-needed signal — match C#'s isConfigEncrypted=false.
            let root = try makeC6TempRoot()
            defer { cleanupC6TempRoot(root) }
            try writeC6Config(at: root, plaintext: true, encrypted: true)
            let snap = DriveConfigurationDetector.detect(root)
            try expect(snap.state == .configuredEmpty, "both → configuredEmpty")
            try expect(snap.isConfigEncrypted == false, "plaintext presence overrides encrypted signal")
        }

        runner.test("C6: detect(plaintext + one manifest) returns fullyConfigured") {
            let root = try makeC6TempRoot()
            defer { cleanupC6TempRoot(root) }
            try writeC6Config(at: root, plaintext: true, encrypted: false)
            try writeC6Manifest(at: root, model: "llama3", tag: "latest")
            let snap = DriveConfigurationDetector.detect(root)
            try expect(snap.state == .fullyConfigured, "config + manifest → fullyConfigured")
            try expect(snap.hasModels == true, "hasModels must be true")
            try expect(snap.modelManifestCount == 1, "manifest count must be 1")
        }

        runner.test("C6: detect(plaintext + many manifests) counts them") {
            let root = try makeC6TempRoot()
            defer { cleanupC6TempRoot(root) }
            try writeC6Config(at: root, plaintext: true, encrypted: false)
            try writeC6Manifest(at: root, model: "llama3", tag: "latest")
            try writeC6Manifest(at: root, model: "qwen2.5", tag: "7b")
            try writeC6Manifest(at: root, model: "phi3", tag: "3.8b")
            let snap = DriveConfigurationDetector.detect(root)
            try expect(snap.state == .fullyConfigured, "3 manifests → fullyConfigured")
            try expect(snap.modelManifestCount == 3, "manifest count must be 3")
        }

        runner.test("C6: detect(manifests but no config) returns empty — foreign-data guard") {
            // A user's own Ollama install on the same external disk must
            // not be claimed as ours. The marker is our config file.
            let root = try makeC6TempRoot()
            defer { cleanupC6TempRoot(root) }
            try writeC6Manifest(at: root, model: "llama3", tag: "latest")
            try writeC6Manifest(at: root, model: "qwen2.5", tag: "7b")
            let snap = DriveConfigurationDetector.detect(root)
            try expect(snap == DriveConfigurationSnapshot.empty,
                       "manifests-only must map to empty (foreign data)")
        }

        runner.test("C6: detect(config + empty manifests dir) returns configuredEmpty") {
            let root = try makeC6TempRoot()
            defer { cleanupC6TempRoot(root) }
            try writeC6Config(at: root, plaintext: true, encrypted: false)
            try FileManager.default.createDirectory(
                at: root.appendingPathComponent("models/manifests"),
                withIntermediateDirectories: true)
            let snap = DriveConfigurationDetector.detect(root)
            try expect(snap.state == .configuredEmpty, "empty manifests dir → configuredEmpty")
        }

        runner.test("C6: detect respects manifestEnumerationCap") {
            let root = try makeC6TempRoot()
            defer { cleanupC6TempRoot(root) }
            try writeC6Config(at: root, plaintext: true, encrypted: false)
            let manifestDir = root
                .appendingPathComponent("models/manifests/registry.ollama.ai/library/spam")
            try FileManager.default.createDirectory(
                at: manifestDir, withIntermediateDirectories: true)
            let total = DriveConfigurationDetector.manifestEnumerationCap + 10
            for i in 0..<total {
                try Data("{}".utf8).write(
                    to: manifestDir.appendingPathComponent("m\(i)"))
            }
            let snap = DriveConfigurationDetector.detect(root)
            try expect(snap.state == .fullyConfigured, "cap+10 manifests → fullyConfigured")
            try expect(
                snap.modelManifestCount == DriveConfigurationDetector.manifestEnumerationCap,
                "manifest count must be capped at \(DriveConfigurationDetector.manifestEnumerationCap)")
        }

        // MARK: M19 — CommandPayloadEncoder JSON parity pins

        runner.test("M19: encode wraps simple string in valid JSON") {
            let payload = CommandPayloadEncoder.encode(["token": "hf_abc123"])
            try expect(payload != nil, "encoder must succeed on a plain ASCII string")
            // Round-trip via JSONSerialization to assert structural validity
            // (avoids brittle key-order assertions).
            let parsed = try parseSinglePairJSON(payload!)
            try expect(parsed.0 == "token" && parsed.1 == "hf_abc123",
                       "round-trip key/value drift: \(parsed)")
        }

        runner.test("M19: encode escapes embedded double quotes") {
            // Hand-rolled escape path got this right, but the test pins
            // continued correctness now that we delegate to
            // JSONSerialization.
            let payload = CommandPayloadEncoder.encode(["search": #"foo "bar" baz"#])
            try expect(payload != nil, "encoder must accept embedded quotes")
            let parsed = try parseSinglePairJSON(payload!)
            try expect(parsed.1 == #"foo "bar" baz"#,
                       "round-trip lost embedded quotes: \(parsed.1)")
        }

        runner.test("M19: encode escapes embedded backslashes") {
            let payload = CommandPayloadEncoder.encode(["search": #"C:\path\to\file"#])
            try expect(payload != nil, "encoder must accept backslashes")
            let parsed = try parseSinglePairJSON(payload!)
            try expect(parsed.1 == #"C:\path\to\file"#,
                       "round-trip lost backslashes: \(parsed.1)")
        }

        runner.test("M19: encode escapes newline + tab + CR (the regression class)") {
            // These are the inputs the hand-rolled escape path mangled:
            // raw 0x0A / 0x09 / 0x0D pass through unescaped, which is
            // invalid JSON and trips JsonDocument.Parse on the C# side.
            let raw = "line1\nline2\tcol2\r\nline3"
            let payload = CommandPayloadEncoder.encode(["search": raw])
            try expect(payload != nil, "encoder must accept control chars")
            try expect(!payload!.contains("\n") && !payload!.contains("\t") && !payload!.contains("\r"),
                       "encoded string must not contain raw control characters: \(payload!)")
            let parsed = try parseSinglePairJSON(payload!)
            try expect(parsed.1 == raw,
                       "round-trip lost control characters: \(parsed.1)")
        }

        runner.test("M19: encode emits a single line (no pretty-print)") {
            // The sidecar command parser splits on the first space, so
            // payloads must stay on one line. JSONSerialization without
            // `.prettyPrinted` already does this; this test pins it.
            let payload = CommandPayloadEncoder.encode([
                "token": "tok",
                "extra": "value"
            ])
            try expect(payload != nil, "encoder must succeed")
            try expect(!payload!.contains("\n"),
                       "payload must be single-line: \(payload!)")
        }

        runner.test("M19: encode produces valid JSON object that JSONSerialization round-trips") {
            // Catches future regressions if the encoder ever wraps the
            // dict in an unexpected shape (e.g. an array).
            let payload = CommandPayloadEncoder.encode(["token": "hf_xyz"])!
            let data = payload.data(using: .utf8)!
            let obj = try JSONSerialization.jsonObject(with: data, options: [])
            try expect(obj is [String: Any], "encoder must emit a JSON object, got: \(type(of: obj))")
        }

        // MARK: - C7 encrypted-drive Manage Models unlock pins
        //
        // PrepViewModel itself is @MainActor + SwiftUI-bound and not part
        // of the pure-Swift test binary. These pins exercise the
        // round-trip primitives the VM stitches together (decrypt → cache
        // material → mutate → re-encrypt with cached material → re-open).
        // The Mac Runner already pins the underlying SsdEncryption
        // contract; these pins are the C7-specific "cached material
        // survives a HF token mutation" promise.

        runner.test("C7: unlock then save-with-cached-material persists huggingFaceToken across re-open") {
            let root = try makeC7TempRoot()
            defer { cleanupC7TempRoot(root) }
            let password = "c7-roundtrip-pw"
            // Seed an encrypted blob WITHOUT a HF token (simulates an
            // existing v1.3.x drive that finalized before the user added
            // a token).
            try seedC7Fixture(root: root, password: password,
                              plaintext: ["version": "1.0", "isEncrypted": true])

            guard case .success(let unlocked) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: password) else {
                throw ExpectationFailure(message:"initial unlock failed")
            }

            // Simulate the VM: mutate the in-memory dict + save via cached material.
            var mutated = unlocked.config
            mutated["huggingFaceToken"] = "hf_abc123_persisted"
            try SsdEncryption.saveEncryptedConfig(
                ssdRoot: root, config: mutated, material: unlocked.material)

            // Re-open with the same password. Token should be present.
            guard case .success(let reopened) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: password) else {
                throw ExpectationFailure(message:"re-open after save failed")
            }
            let token = reopened.config["huggingFaceToken"] as? String
            try expect(token == "hf_abc123_persisted",
                       "expected persisted token, got: \(token ?? "nil")")
        }

        runner.test("C7: save-with-cached-material preserves unknown fields the PrepApp doesn't model") {
            let root = try makeC7TempRoot()
            defer { cleanupC7TempRoot(root) }
            let password = "c7-unknown-fields-pw"
            // Seed with fields the Mac PrepApp doesn't model directly
            // (networkApiBindAddress, embeddingModel) so we can prove
            // the round-trip preserves them.
            try seedC7Fixture(root: root, password: password,
                              plaintext: [
                                "version": "1.0",
                                "networkApiBindAddress": "192.168.1.42",
                                "embeddingModel": "nomic-embed-text:latest",
                                "huggingFaceToken": "hf_initial"
                              ])

            guard case .success(let unlocked) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: password) else {
                throw ExpectationFailure(message:"initial unlock failed")
            }

            var mutated = unlocked.config
            mutated["huggingFaceToken"] = "hf_edited"
            try SsdEncryption.saveEncryptedConfig(
                ssdRoot: root, config: mutated, material: unlocked.material)

            guard case .success(let reopened) = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: password) else {
                throw ExpectationFailure(message:"re-open after save failed")
            }
            try expect(reopened.config["networkApiBindAddress"] as? String == "192.168.1.42",
                       "networkApiBindAddress dropped during round-trip")
            try expect(reopened.config["embeddingModel"] as? String == "nomic-embed-text:latest",
                       "embeddingModel dropped during round-trip")
            try expect(reopened.config["huggingFaceToken"] as? String == "hf_edited",
                       "HF token edit not persisted")
        }

        runner.test("C7: wrong-passphrase unlock returns incorrectPassword without producing material") {
            let root = try makeC7TempRoot()
            defer { cleanupC7TempRoot(root) }
            try seedC7Fixture(root: root, password: "correct-pw",
                              plaintext: ["version": "1.0", "huggingFaceToken": "hf_secret"])

            let result = SsdEncryption.tryUnlockPortableConfig(
                ssdRoot: root, password: "wrong-pw")
            guard case .failure(let err) = result else {
                throw ExpectationFailure(message:"expected failure for wrong password")
            }
            try expect(err == .incorrectPassword,
                       "expected .incorrectPassword, got \(err)")
        }

        runner.test("C7: UnlockMaterial.zeroize wipes derivedKey buffer in place") {
            // Mirrors SsdEncryptionTests' zeroize pin so the PrepApp test
            // surface independently asserts the cached-key zeroization
            // contract that C7's resetManageModelsUnlockState relies on.
            let key = Data(repeating: 0xAB, count: SsdEncryptionConstants.keyBytes)
            let m = UnlockMaterial(
                derivedKey: key, salt: Data(repeating: 0x01, count: 16),
                iterations: SsdEncryptionConstants.pbkdf2Iterations,
                scheme: SsdEncryptionConstants.schemeName)
            try expect(m.derivedKey.contains(0xAB),
                       "pre-zero: expected key buffer to hold the seeded byte")
            m.zeroize()
            try expect(m.derivedKey.allSatisfy { $0 == 0 },
                       "post-zero: every byte should be 0; got non-zero")
        }

        await runner.run()
    }
}

// M19 helper — parse a `{"key":"value"}` payload back into a (String, String)
// pair so the round-trip pins don't depend on JSON key order. Fails fast on
// any shape drift (multiple keys, non-string values, etc).
private func parseSinglePairJSON(_ text: String) throws -> (String, String) {
    let data = text.data(using: .utf8) ?? Data()
    let raw = try JSONSerialization.jsonObject(with: data, options: [])
    guard let dict = raw as? [String: String], dict.count == 1,
          let pair = dict.first else {
        struct ParseFailure: Error { let detail: String }
        throw ParseFailure(detail: "expected single-pair JSON object, got: \(raw)")
    }
    return (pair.key, pair.value)
}

// MARK: - C6 Swift detector test fixtures
//
// Mirrors the C# `MakeTempDriveRoot` helper in tests/PrepViewModelTests.cs.
// Creates an isolated temp directory and provides seeding helpers that
// match C#'s file paths (config dir name, encrypted/plaintext filenames,
// and the manifests/registry.ollama.ai/library/<model>/<tag> shape).

private func makeC6TempRoot() throws -> URL {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("free-ai-ssd-tests")
        .appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    return root
}

private func cleanupC6TempRoot(_ root: URL) {
    try? FileManager.default.removeItem(at: root)
}

private func writeC6Config(at root: URL, plaintext: Bool, encrypted: Bool) throws {
    let configDir = root.appendingPathComponent(SsdEncryptionConstants.configDirName)
    try FileManager.default.createDirectory(at: configDir, withIntermediateDirectories: true)
    if plaintext {
        try Data("{}".utf8).write(
            to: configDir.appendingPathComponent(SsdEncryptionConstants.plaintextConfigFileName))
    }
    if encrypted {
        try Data("{}".utf8).write(
            to: configDir.appendingPathComponent(SsdEncryptionConstants.encryptedConfigFileName))
    }
}

private func writeC6Manifest(at root: URL, model: String, tag: String) throws {
    let manifestDir = root
        .appendingPathComponent("models/manifests/registry.ollama.ai/library/\(model)")
    try FileManager.default.createDirectory(at: manifestDir, withIntermediateDirectories: true)
    try Data("{}".utf8).write(to: manifestDir.appendingPathComponent(tag))
}

// MARK: - F2a fixture
//
// Hand-rolled display entries instead of round-tripping through
// StarterModelEntry.from(...) so the test is independent of the
// projection logic and asserts only the filter behavior.

private func makeF2aFixture() -> [StarterModelDisplayEntry] {
    [
        StarterModelDisplayEntry(tag: "llama3.2:1b", sizeTier: "Small",
                                 bestAt: "Lightweight assistant for quick prompts (chat, fast)",
                                 pullCount: 114_000_000),
        StarterModelDisplayEntry(tag: "llama3.2:3b", sizeTier: "Small",
                                 bestAt: "Balanced small model for everyday Q&A (chat, general)",
                                 pullCount: 90_000_000),
        StarterModelDisplayEntry(tag: "qwen2.5:7b", sizeTier: "Medium",
                                 bestAt: "Versatile 7B with reasoning + coding support (reasoning, coding)",
                                 pullCount: 50_000_000),
        StarterModelDisplayEntry(tag: "gemma2:2b", sizeTier: "Small",
                                 bestAt: "Good starter when hardware is limited (cpu-friendly)",
                                 pullCount: 200_000_000),
        StarterModelDisplayEntry(tag: "deepseek-r1:70b", sizeTier: "Large",
                                 bestAt: "Frontier reasoning model (reasoning)",
                                 pullCount: 25_000_000),
        // Bundled-style entry without a pull count — must drop out of
        // the "Most popular" view, but appear in unfiltered + search.
        StarterModelDisplayEntry(tag: "bundled-only:1b", sizeTier: "Small",
                                 bestAt: "Fallback bundled entry (chat)",
                                 pullCount: nil),
    ]
}

/// C3/C4/C5 fixture — extends the F2a fixture with parametersBillion,
/// capabilities, and lastUpdated so the new filter+sort tests can pin
/// behavior without the production projection path. `multi-tool` and
/// `tools-only` carry distinct capability vectors so the AND filter is
/// distinguishable from any-of.
private func makeC3C4C5Fixture() -> [StarterModelDisplayEntry] {
    [
        StarterModelDisplayEntry(
            tag: "multi-tool:8b", sizeTier: "Medium",
            bestAt: "Tool-using vision model",
            pullCount: 50_000_000,
            capabilities: ["tools", "vision"],
            parametersBillion: 8.0,
            lastUpdated: "2026-05-08T00:00:00+00:00"),
        StarterModelDisplayEntry(
            tag: "tools-only:7b", sizeTier: "Medium",
            bestAt: "Tool-using small model",
            pullCount: 30_000_000,
            capabilities: ["tools"],
            parametersBillion: 7.0,
            lastUpdated: "2026-04-01T00:00:00+00:00"),
        StarterModelDisplayEntry(
            tag: "vision-only:14b", sizeTier: "Large",
            bestAt: "Vision-capable mid model",
            pullCount: 20_000_000,
            capabilities: ["vision"],
            parametersBillion: 14.0,
            lastUpdated: "2026-02-10T00:00:00+00:00"),
        StarterModelDisplayEntry(
            tag: "deepseek-r1:70b", sizeTier: "Large",
            bestAt: "Frontier reasoning",
            pullCount: 25_000_000,
            capabilities: ["thinking"],
            parametersBillion: 70.0,
            lastUpdated: "2026-03-20T00:00:00+00:00"),
        // Bundled-style: no caps, no params, no date — pass-through
        // under every filter, sorts last under newest.
        StarterModelDisplayEntry(
            tag: "bundled-only:1b", sizeTier: "Small",
            bestAt: "Fallback bundled entry",
            pullCount: nil,
            capabilities: [],
            parametersBillion: nil,
            lastUpdated: nil),
    ]
}

/// C24 fixture — synthetic mac-prep-host refresh-catalog payload.
/// Mirrors the JSON shape emitted by HostLifetime.RefreshCatalogAsync
/// so this test catches drift between the C# projection and Swift
/// decode. The PR #259 regression was that this payload was emitted
/// without `parametersBillion` and `lastUpdated`, so the round-trip
/// asserts on both fields are the load-bearing pin.
private func makeC24RefreshCatalogPayload() -> [String: Any] {
    return [
        "ok": true,
        "fetchedAt": "2026-05-10T12:00:00+00:00",
        "sourceUrl": "https://ollama.com/library",
        "entries": [
            [
                "tag": "qwen2.5:7b",
                "params": "7B",
                "sizeTier": "Medium",
                "description": "Versatile small model",
                "useCases": ["tools"],
                "pullCount": Int64(50_000_000),
                "parametersBillion": 7.0,
                "lastUpdated": "2026-05-08T00:00:00+00:00",
            ],
            [
                "tag": "deepseek-r1:30b",
                "params": "30B",
                "sizeTier": "Large",
                "description": "Large reasoning model",
                "useCases": ["thinking"],
                "pullCount": Int64(20_000_000),
                "parametersBillion": 30.0,
                "lastUpdated": "2026-03-20T00:00:00+00:00",
            ],
            [
                "tag": "llama3.2:3b",
                "params": "3B",
                "sizeTier": "Small",
                "description": "Lightweight assistant",
                "useCases": ["general"],
                "pullCount": Int64(90_000_000),
                "parametersBillion": 3.0,
                "lastUpdated": "2026-04-15T00:00:00+00:00",
            ],
        ],
    ]
}

/// C27 Stage 1: synthetic discover-hf-catalog payload matching the
/// wire shape `HostLifetime.BuildCatalogEntries` emits for HF entries.
/// `source` is the new C27 field; the helper pins it on every row so
/// the Swift decode + display projection can be exercised without a
/// real sidecar process. lastModified values are intentionally ordered
/// so qwen3 < llama under the newest-sort assertion.
private func makeC27HuggingFacePayload() -> [String: Any] {
    return [
        "ok": true,
        "fetchedAt": "2026-05-11T12:00:00+00:00",
        "sourceUrl": "https://huggingface.co/api/models",
        "query": NSNull(),
        "entries": [
            [
                "tag": "hf.co/bartowski/Qwen3-8B-GGUF",
                "params": "",
                "sizeTier": "Custom",
                "description": "",
                "useCases": [String](),
                "pullCount": Int64(245321),
                "parametersBillion": 8.0,
                "lastUpdated": "2026-04-28T14:22:11+00:00",
                "source": "HuggingFace",
            ],
            [
                "tag": "hf.co/lmstudio-community/Llama-3.2-7B-Instruct-GGUF",
                "params": "",
                "sizeTier": "Custom",
                "description": "",
                "useCases": [String](),
                "pullCount": Int64(188204),
                "parametersBillion": 7.0,
                "lastUpdated": "2026-05-02T10:03:45+00:00",
                "source": "HuggingFace",
            ],
        ],
    ]
}

/// C27 Stage 4: same shape as `makeC27HuggingFacePayload` but with the
/// new `isExpandable=true` field every entry — mirrors the wire format
/// the post-Stage-4 sidecar emits.
private func makeC27HuggingFacePayloadStage4() -> [String: Any] {
    return [
        "ok": true,
        "fetchedAt": "2026-05-12T09:00:00+00:00",
        "sourceUrl": "https://huggingface.co/api/models",
        "query": NSNull(),
        "entries": [
            [
                "tag": "hf.co/Qwen/Qwen3-8B-GGUF",
                "params": "",
                "sizeTier": "Custom",
                "description": "",
                "useCases": [String](),
                "pullCount": Int64(245321),
                "parametersBillion": 8.0,
                "lastUpdated": "2026-04-28T14:22:11+00:00",
                "source": "HuggingFace",
                "isExpandable": true,
            ],
            [
                "tag": "hf.co/Qwen/Qwen3-70B-GGUF",
                "params": "",
                "sizeTier": "Custom",
                "description": "",
                "useCases": [String](),
                "pullCount": Int64(99012),
                "parametersBillion": 70.0,
                "lastUpdated": "2026-05-01T08:11:33+00:00",
                "source": "HuggingFace",
                "isExpandable": true,
            ],
        ],
    ]
}

// MARK: - Fixture writer (cross-language proof)
//
// Generates the swift-prep-encrypted fixture under
// tests/Fixtures/MacEncryptedConfig/swift-prep-encrypted/ via the same
// EncryptedConfigWriter the SwiftUI flow uses. The fixture is then
// consumed by the C# MacEncryptedConfigCrossLanguageTests to prove
// MAC17 PrepApp's first-write payload roundtrips through the Windows
// Runner. This complements (does not replace) the MAC5 csharp-encrypted/
// fixture — MAC5 pins the *blob* format, MAC17 pins the *initial-write
// plaintext shape* (InitialPortableConfigPayload).

// Constants must match the C# test's expected values.
private let mac17FixturePassword       = "mac17-prep-cross-lang-fixture-pw"
private let mac17FixtureOllamaPort     = 13577
private let mac17FixtureNetworkPort    = 41555
private let mac17FixtureNetworkBind    = "127.0.0.1"
private let mac17FixturePreferredCompute = "cpu"

func writeMac17PrepFixture(outDir: URL) throws {
    let fm = FileManager.default
    // Preserve README.md (and any sibling docs) — only clear the
    // config/ subdirectory that SsdEncryption.saveEncryptedConfig
    // writes into. This way `regenerate the fixture` doesn't trash
    // the README that explains what password unlocks it.
    let configDir = outDir.appendingPathComponent("config")
    if fm.fileExists(atPath: configDir.path) {
        try fm.removeItem(at: configDir)
    }
    try fm.createDirectory(at: outDir, withIntermediateDirectories: true)

    // Use a non-default ollamaPort (13577) so the C# unlock test can
    // distinguish "fixture decoded successfully" from "fixture decoded
    // empty and PortableConfig defaults filled in 11434." Other fields
    // stay at their canonical defaults.
    let payload = InitialPortableConfigPayload(
        ollamaPort: mac17FixtureOllamaPort,
        networkModeEnabled: false,
        networkBindAddress: mac17FixtureNetworkBind,
        networkPort: mac17FixtureNetworkPort,
        networkRequireApiKey: true,
        networkApiKey: "",
        preferredCompute: mac17FixturePreferredCompute
    )

    let writer = EncryptedConfigWriter()
    try writer.writeInitialEncryptedConfig(
        ssdRoot: outDir, payload: payload, passphrase: mac17FixturePassword)
}

// MARK: - MAC21 fixture helpers

/// Resolve a fixture under `mac-prep-app/Tests/Fixtures/` relative to
/// this file's compile-time path. `#filePath` bakes the absolute path
/// in at compile time, so the test binary finds fixtures regardless of
/// the cwd at runtime (CI runs the binary from the repo root after
/// building it from the same workspace).
private func loadDiskutilFixture(_ name: String) throws -> Data {
    let testFile = URL(fileURLWithPath: #filePath)
    let fixtureDir = testFile.deletingLastPathComponent().appendingPathComponent("Fixtures")
    let url = fixtureDir.appendingPathComponent(name)
    return try Data(contentsOf: url)
}

/// Wrap a list of AllDisksAndPartitions[] entries into a binary plist
/// matching what `diskutil list -plist external` emits at the root
/// level. Used for inline-synthesized test cases that don't warrant
/// their own XML fixture file.
private func plistFromAllDisks(_ disks: [[String: Any]]) throws -> Data {
    let root: [String: Any] = ["AllDisksAndPartitions": disks]
    return try PropertyListSerialization.data(
        fromPropertyList: root, format: .binary, options: 0)
}

// MARK: - Test runner harness (mirrors mac-runner/Tests/SsdEncryptionTests.swift)

struct ExpectationFailure: Error {
    let message: String
}

func expect(_ condition: @autoclosure () -> Bool, _ message: String = "") throws {
    if !condition() {
        throw ExpectationFailure(message: message.isEmpty ? "expectation failed" : message)
    }
}

final class TestRunner {
    private struct Case {
        let name: String
        let body: () async throws -> Void
    }

    private var cases: [Case] = []

    func test(_ name: String, _ body: @escaping () async throws -> Void) {
        cases.append(Case(name: name, body: body))
    }

    func run() async {
        var failures = 0
        for c in cases {
            do {
                try await c.body()
                print("ok      \(c.name)")
            } catch let f as ExpectationFailure {
                failures += 1
                print("FAIL    \(c.name) — \(f.message)")
            } catch {
                failures += 1
                print("ERROR   \(c.name) — \(error.localizedDescription)")
            }
        }
        print("\n\(cases.count - failures)/\(cases.count) passed.")
        if failures > 0 { exit(1) }
    }
}

// MARK: - C7 encrypted-fixture helpers
//
// Same pattern as `seedEncryptedFixture` in mac-runner/Tests/
// SsdEncryptionTests.swift, scoped here so PrepAppTests doesn't need a
// dependency on that file. Each PBKDF2 derivation takes ~1s on M-series
// silicon — tests using these helpers should pass small plaintexts and
// not run them in tight loops.

private func makeC7TempRoot() throws -> URL {
    let root = URL(fileURLWithPath: NSTemporaryDirectory())
        .appendingPathComponent("free-ai-ssd-c7-tests")
        .appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    return root
}

private func cleanupC7TempRoot(_ root: URL) {
    try? FileManager.default.removeItem(at: root)
}

private func seedC7Fixture(root: URL, password: String, plaintext: [String: Any]) throws {
    let configDir = SsdEncryption.configDirURL(ssdRoot: root)
    try FileManager.default.createDirectory(at: configDir, withIntermediateDirectories: true)

    var saltBytes = [UInt8](repeating: 0, count: SsdEncryptionConstants.saltBytes)
    let s = SecRandomCopyBytes(kSecRandomDefault, saltBytes.count, &saltBytes)
    guard s == errSecSuccess else { throw ExpectationFailure(message: "SecRandomCopyBytes failed") }
    let salt = Data(saltBytes)

    let key = try SsdEncryption.pbkdf2Sha256(
        password: password, salt: salt,
        iterations: SsdEncryptionConstants.pbkdf2Iterations,
        keyBytes: SsdEncryptionConstants.keyBytes)

    let material = UnlockMaterial(
        derivedKey: key, salt: salt,
        iterations: SsdEncryptionConstants.pbkdf2Iterations,
        scheme: SsdEncryptionConstants.schemeName)

    try SsdEncryption.saveEncryptedConfig(ssdRoot: root, config: plaintext, material: material)
    material.zeroize()
}
