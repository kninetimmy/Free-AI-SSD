import Foundation

// MAC17 mac-prep-app test runner. Mirrors mac-runner/Tests/SsdEncryptionTests.swift
// pattern: a standalone CLI binary compiled with swiftc that exits non-zero on
// any failed assertion. Run from CI via:
//
//     swiftc \
//         mac-prep-app/Sources/PrepFlowStep.swift \
//         mac-prep-app/Sources/DiskutilFormatCommand.swift \
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

        // MARK: MAC34 — InitialPortableConfigPayload generates a non-empty
        // 64-hex network API key by default. Pre-MAC34 this defaulted to
        // `""` which fail-closed every chat request through the LAN API
        // path with `503 API key is required by configuration but not set
        // on host.` See agent_docs/mac_project_backlog.md MAC34.

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

        await runner.run()
    }
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
