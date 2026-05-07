# MAC17b Execution Prompt — PrepApp Issue #5: SSD layout via sidecar `ensure-structure`

Last of the seven Gemini-review items from PR #193 (MAC17 MVP).
MAC17a (PR #195 + PR #197) closed out the threading cluster and the
PrepHostController cancel/leak fixes; this PR replaces the hardcoded
`macSubdirs` list with a sidecar command that delegates to the
canonical C# `SsdLayout.EnsureStructure(...)`.

This is structural, not behavioral. Same set of directories should
end up on disk before and after — the win is that the next time the
C# layout grows a directory (it has happened — see `git log` on
`shared/SsdLayout.cs`), the Mac PrepApp picks it up automatically
instead of silently shipping drives with a missing tree.

## Branch + PR

- Branch name: `kninetimmy/mac17b-prep-ensure-structure`
- Base: `main`
- PR title: `MAC17b: PrepApp ensure-structure sidecar command (Issue #5)`
- PR body should reference the original Issue #5 entry in
  `agent_docs/mac17_followup_notes.md` and call out that this
  finishes the PR #193 review backlog.

## Scope

**In scope:**
1. Add an `ensure-structure` command to the `mac-prep-host/` stdin
   command set. It delegates to
   `FreeAiSsd.Shared.SsdLayout.EnsureStructure(_ssdRoot)`.
2. Replace the hardcoded `macSubdirs` block in
   `mac-prep-app/Sources/PrepViewModel.swift` (`runStaging`,
   currently lines ~211–223) with a single
   `_ = try await hostController.send("ensure-structure")`.
3. New host-side test that pins drift between `SsdLayout` and the
   actual filesystem state after `ensure-structure` runs.

**Out of scope (do not touch):**
- Encrypted-config format / scheme name / cross-language fixture
  under `tests/Fixtures/MacEncryptedConfig/`.
- MAC5 plaintext invariant — the sidecar still must not see plaintext
  PortableConfig over stdin or anywhere else. `ensure-structure` is
  pure filesystem layout, no config.
- `EncryptionService` registration in `HostLifetime` — stays absent.
- `DiskutilFormatCommand` / drive-format path. No security-control
  edits.
- Drive-by cleanups, refactors, doc edits unrelated to Issue #5.
- No `--no-verify`, no `git push --force` to `main`.

## Implementation shape

### 1. Sidecar command — `mac-prep-host/HostLifetime.cs`

The csproj already has `<ProjectReference Include="..\shared\FreeAiSsd.Shared.csproj" />`
and `using FreeAiSsd.Shared;` is already present, so `SsdLayout` is
in scope. Add a single switch arm in `HandleCommandAsync` matching
the existing pattern:

```csharp
case "ensure-structure":
    EnsureStructure();
    break;
```

And the implementation, sitting next to the other private command
methods. Mirror the synchronous `DiscoverModels` shape (no async,
no `_testMode` short-circuit — this is pure `Directory.CreateDirectory`,
fast and idempotent, and short-circuiting it would defeat the purpose
of the test that pins layout drift):

```csharp
private void EnsureStructure()
{
    SsdLayout.EnsureStructure(_ssdRoot);
    EmitResult("ensure-structure", new { ok = true });
}
```

Rationale on skipping `_testMode`: the only `_testMode` skips today
are for prep-core service calls that hit the network or shell out
(`StageMacRunnerAsync`, `StageMacOllamaAsync`,
`StagePrerequisitesAsync`, `PullModelAsync`, `VerifyModelAsync`).
`SsdLayout.EnsureStructure` is just `Directory.CreateDirectory` calls
against a path the test already controls — running it under test mode
is what makes the new test meaningful.

### 2. Swift call site — `mac-prep-app/Sources/PrepViewModel.swift`

Replace the `macSubdirs` block (currently `runStaging` lines ~211–223)
with a sidecar call that runs **after** the sidecar is started, since
the existing flow today creates directories *before* sidecar startup.
That ordering needs to flip:

Current order in `runStaging`:
1. Create `macSubdirs` directly via `FileManager` (lines ~211–227).
2. `hostController.startAndWaitReady(ssdRoot: mount)` (line ~231).
3. `stage-runner` / `stage-ollama` / `stage-prereqs`.

New order:
1. `hostController.startAndWaitReady(ssdRoot: mount)`.
2. `_ = try await hostController.send("ensure-structure")`.
3. `stage-runner` / `stage-ollama` / `stage-prereqs`.

Verify by re-reading `HostHandshake` / `HostLifetime` startup that
nothing in the sidecar's `Start()` path requires any of the
`macSubdirs` entries to exist before the handshake. Today `Start()`
calls `WriteLineSafe("ready")` and `_logger?.Info(...)`. The
constructor builds an `SsdLogger(_ssdRoot, "macos-prep-host")` —
**this writes to the `logs/` directory**, so `logs/` must already
exist or `SsdLogger` construction will fall through to the
`stderr.WriteLine($"Failed to initialize SsdLogger: ...")` branch
and log lines will be silently dropped.

Two ways to handle this:

- **(a) Recommended.** Have the Swift side create just `logs/` (one
  `FileManager.createDirectory` call, no list duplication) before
  starting the sidecar, then call `ensure-structure` after handshake
  to lay down the rest. This preserves working logging on first
  boot. Note this in a single short comment so a future reader
  knows why one directory escapes the sidecar delegation.
- (b) Move SsdLogger construction lazy / first-use inside
  `HostLifetime` so it doesn't matter if `logs/` exists at
  construction. More invasive — skip unless (a) turns out broken.

Go with (a). The diff is small, the carve-out is one directory, and
the comment makes the constraint explicit instead of leaving a
hidden ordering dependency.

Resulting Swift block roughly:

```swift
do {
    // logs/ has to exist before the sidecar starts so SsdLogger can
    // open its log file; everything else is laid down by the sidecar
    // via SsdLayout.EnsureStructure once we're handshaken.
    try FileManager.default.createDirectory(
        at: mount.appendingPathComponent("logs"),
        withIntermediateDirectories: true)
} catch {
    currentStep = .failed(message: "Failed to create logs directory: \(error.localizedDescription)")
    return
}

do {
    try await hostController.startAndWaitReady(ssdRoot: mount)
} catch {
    currentStep = .failed(message: "Sidecar startup failed: \(error.localizedDescription)")
    return
}

do {
    _ = try await hostController.send("ensure-structure")
    appendLog("SSD layout created.")
} catch {
    currentStep = .failed(message: "Failed to create SSD layout: \(error.localizedDescription)")
    return
}

do {
    _ = try await hostController.send("stage-runner")
    _ = try await hostController.send("stage-ollama")
    _ = try await hostController.send("stage-prereqs")
    appendLog("Staging complete.")
    currentStep = .encryptionSetup
} catch {
    currentStep = .failed(message: "Staging failed: \(error.localizedDescription)")
}
```

Delete the `macSubdirs` array entirely.

### 3. Drift-pinning test — `tests/MacPrepHostSmokeTests.cs`

Add one new in-process test that runs on Windows CI alongside the
existing handshake/readiness tests. Pattern matches the existing
`HostRunner_TestMode_HandshakeReadinessShutdown_ExitsClean` shape:
hand a handshake + `ensure-structure` + `shutdown` over `StringReader`,
assert the result line appears, and walk every public path constant
on `SsdLayout` and verify the directory exists on disk under the
temp `ssdRoot`.

```csharp
[Fact]
public async Task HostRunner_EnsureStructure_CreatesEverySsdLayoutDirectory()
{
    using var workdir = new TempDir("freeai-mac17b-ensure-");
    // logs/ + config/ pre-created so SsdLogger and any other
    // construction-time IO succeeds; ensure-structure must still
    // create them idempotently and add everything else.
    Directory.CreateDirectory(Path.Combine(workdir.Path, "logs"));
    Directory.CreateDirectory(Path.Combine(workdir.Path, "config"));

    var handshake = JsonSerializer.Serialize(new { ssdRoot = workdir.Path });

    var input = new System.Text.StringBuilder();
    input.AppendLine(handshake);
    input.AppendLine("ensure-structure");
    input.AppendLine("shutdown");

    using var stdin = new StringReader(input.ToString());
    using var stdout = new StringWriter();
    using var stderr = new StringWriter();

    var exitCode = await FreeAiSsd.MacPrepHost.HostRunner.RunAsync(
        stdin, stdout, stderr, new[] { "--test-mode" });

    Assert.Equal(0, exitCode);
    Assert.Contains("result: ensure-structure", stdout.ToString());

    // Every relative path SsdLayout declares must exist on disk.
    // Add to this list any time SsdLayout grows a directory — the
    // failure mode this test pins is exactly "C# adds a dir, Mac
    // PrepApp silently doesn't ship it".
    var expected = new[]
    {
        SsdLayout.Windows,
        SsdLayout.WindowsTools,
        SsdLayout.WindowsOllama,
        SsdLayout.WindowsPrereqs,
        SsdLayout.WindowsRunner,
        SsdLayout.Mac,
        SsdLayout.MacTools,
        SsdLayout.MacOllama,
        SsdLayout.Models,
        SsdLayout.Blobs,
        SsdLayout.WhisperModels,
        SsdLayout.Config,
        SsdLayout.Logs,
        SsdLayout.Cache,
        SsdLayout.Docs,
        SsdLayout.DocLibraries,
    };

    foreach (var rel in expected)
    {
        Assert.True(
            Directory.Exists(Path.Combine(workdir.Path, rel)),
            $"Expected SsdLayout directory '{rel}' to exist after ensure-structure.");
    }
}
```

This is the test that pins drift — keep the expected list explicit
rather than reflecting over `SsdLayout`'s constants, because the
purpose is to fail loudly when somebody adds a constant without
adding it here. A reflection-based test would silently track
whatever C# does and miss the point.

(Note: `MacPrepHostConstructionTests` was the original target in the
backlog entry, but its constructor pattern leans on direct service
construction rather than the stdin command surface — `MacPrepHostSmokeTests`
is the right home for an `ensure-structure` end-to-end. If you find
a clean fit in `MacPrepHostConstructionTests` instead, that's also
fine.)

### 4. Swift unit test — optional

`mac-prep-app/Tests/PrepAppTests.swift` doesn't have an obvious seam
for testing `runStaging` end-to-end (it would need a fake host
process). The existing tests pin `DiskutilFormatCommand` and
`PrepHostController` cancel-path behavior. Skip a Swift test for
this PR — the C# side test pins the contract; any Swift-side test
would just be re-asserting that `hostController.send` was called.

## Test commands

```bash
# C# (run from repo root on Windows CI; locally on Mac is fine too)
dotnet test tests/FreeAiSsd.Tests.csproj \
  --filter "FullyQualifiedName~MacPrepHostSmokeTests" \
  --verbosity normal

# Swift (run on Mac)
cd mac-prep-app
# Match the swiftc invocation already in .github/workflows/build.yml's
# mac-prep-build job — do not introduce a separate one.
```

## CI expectations

Three jobs run on the PR (`windows-build`, `mac-runner-build`,
`mac-prep-build`); `package-release` is correctly skipped because
this PR doesn't tag a release.

- `windows-build`: full `dotnet test`, including the new
  `HostRunner_EnsureStructure_*` test.
- `mac-prep-build`: Swift tests (unchanged set), `dotnet publish`
  of `mac-prep-host`, end-to-end stdin smoke, PrepApp.app bundling.
- If the Swift call-site reorder breaks the `SsdLogger` ordering
  assumption, expect the failure to surface in the Mac end-to-end
  smoke (missing log file or stderr line). Investigate and fix
  forward on the same branch.

## Manual smoke

Defer to the existing MAC17 manual-smoke entry in
`agent_docs/project_state.md` "Open questions" — when the user runs
the cross-platform roundtrip on a real Mac + external SSD, every
directory `SsdLayout` declares should exist after staging.

## Wrap-up after merge

After merge, open a docs-only follow-up PR
(`kninetimmy/mac17b-wrapup` or similar) that:

- Marks MAC17b done in `agent_docs/mac_project_backlog.md`.
- Bumps `Last updated` in `agent_docs/project_state.md` and adds the
  PR entry to `Recently shipped`.
- Reorders `Next up` so MAC18 (compatibility docs) is #1.
- Skip the CI wait per the user's standing preference for
  docs-only wrap-up PRs after a green parent.

Done.
