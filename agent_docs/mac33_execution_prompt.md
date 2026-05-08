# MAC33 — Mac Runner shows zero models on Mac-prepped SSD (cross-OS)

Execution prompt for the top-of-queue Mac usability blocker filed
2026-05-08 from the v1.3.10 mac field test. Mac is genuinely
unusable end-to-end without this — readiness page now goes
all-green (MAC29 win), but the Runner unlocks to an empty model
picker.

Backlog entry: `agent_docs/mac_project_backlog.md` → "MAC33 — Mac
Runner shows zero selectable models on a Mac-prepped SSD".
Sibling-bug context: `agent_docs/project_state.md` MAC29 entry.

## Goal

A user who unlocks a Mac-prepped SSD in Mac Runner — or in
Windows Runner — sees the starter models they pulled in the
model picker and can select one for chat. Same for the
Companion app talking to either Runner over LAN: `/models`
returns the disk-truth list rather than an empty
config-pinned list.

## Why now

**Top of queue.** v1.3.10 mac field test confirmed MAC29 closed
the readiness false-negatives (`llama3.2:1b` pull succeeded,
readiness all-green for the first time ever on Mac). The same
field run then failed at the next step: opening Runner.app off
the SSD, entering the passphrase, watching unlock succeed —
and the model selector showing zero options. Mac post-prep is
broken until this ships. MAC30 (encryption-optional) and the
MAC31 / MAC32 UX items do not matter while the Mac Runner
cannot present the model the user just successfully pulled.

## Root cause — confirmed at kickoff via grep

Three consumers of `config.Models.Where(m => m.Status == ModelInstallStatus.Installed)`
in runner-core, all empty on a Mac-prepped SSD because the Mac
sidecar's `pull-model` arm doesn't write back to the encrypted
config (passphrase zeroized before pulls run, so re-deriving the
key per pull is too expensive to bolt on):

1. `runner-core/Services/ModelManagementService.cs:25-31` —
   `GetInstalledModelNames`. Called by the Windows WPF picker at
   `runner/MainWindow.xaml.cs:325` (`PopulateModelCombo`).
2. `runner-core/Services/ModelManagementService.cs:39` —
   `GetModelSizingWarnings`. Same filter; only relevant when at
   least one model is "installed" per config, so it's silently
   no-op'd on Mac today rather than showing wrong warnings.
3. `runner-core/Services/RunnerLocalApiService.cs:157-168` — the
   `/models` LAN endpoint. Consumed by the Mac Runner UI (its
   picker hits this endpoint) and by the Windows Companion app.

This is the symmetric form of MAC29 bug 3 in three different
consumers. MAC29 fixed `prep-core/Services/ReadinessService.cs`
by reading disk truth via `ModelOperations.DiscoverModelsOnDisk`
+ `FindModelBlobForModel`. **The Runner has the same problem,
in three places, all in runner-core.**

## Dual-OS review pass

**Mac surfaces:**
- `mac-runner/Sources/main.swift:1032` — `Picker("Model",
  selection: $vm.selectedModel)`. Picker source comes from
  `vm` (the `@MainActor class AppViewModel`). Confirm at kickoff
  whether the Mac Runner populates this list by calling the
  in-process LAN API `/models` endpoint or has its own path. If
  it's the LAN endpoint (most likely — it's how Network Mode
  flows wire up), then fix #3 above resolves it with no Swift
  code changes.
- If the Mac Runner reads `config.Models` directly anywhere on
  the Swift side (unlikely — Mac side reads `mac/portable-config`
  via the host JSON), surface it at kickoff and either bring it
  to disk-truth or route it through the C# host.

**Windows surfaces:**
- `runner/MainWindow.xaml.cs:322-330` — `PopulateModelCombo` calls
  `_modelService.GetInstalledModelNames(_config)`. Centralize the
  disk-truth swap inside that method and the WPF picker fixes
  itself with no caller changes.
- `runner/MainWindow.xaml.cs` sizing-warning callers (search for
  `GetModelSizingWarnings`) inherit the swap automatically.

**Shared-core surfaces:**
- `runner-core/Services/IModelManagementService.cs` —
  `GetInstalledModelNames` and `GetModelSizingWarnings` signatures
  gain a `string ssdRoot` parameter (or read it from the existing
  service ctor — see "Architecture" below). The signature change
  is the only public API break.
- `runner-core/Services/ModelManagementService.cs` — implementation
  swap to disk-truth via `ModelOperations.DiscoverModelsOnDisk`,
  with `config.Models` retained as a metadata fallback for any
  pinned details (none today, but keeps the door open for sizing
  metadata that lives in config but isn't on disk).
- `runner-core/Services/RunnerLocalApiService.cs:157-168` — route
  `/models` through the same disk-truth read. Either inject
  `IModelManagementService` (preferred — it's already a service)
  and call `GetInstalledModelNames`, or duplicate the
  `DiscoverModelsOnDisk` call inline. Centralize.
- **Optional but recommended** — opportunistic config rebuild on
  Runner unlock. New helper in `runner-core` (or in
  `IModelManagementService` as a side method) that, when
  `config.Models` is empty and disk has models, writes the
  enumerated set back into the encrypted config using the
  unlock-time `UnlockMaterial`. Means already-prepped SSDs
  self-heal and the disk-truth fallback eventually becomes
  redundant for them.

**Non-surfaces (one-line justification):**
- `prep-core/` — no read of installed-model state; ReadinessService
  already swapped in MAC29.
- `mac-prep-host/` — produces drives but does not read installed
  models for any picker.
- `mac-prep-app/` — same.
- `companion/` — talks to Runner via `/models`; fix #3 covers it
  transparently.
- `runner-cli/` — no model picker; chat REPL only.

**Decision:** **Bundle both OSes in one PR.** All three fixes
live in runner-core, the Windows wiring is a no-op (caller
unchanged), and the Mac side is either no-op (LAN endpoint route)
or a tiny Swift surface. Per the 2026-05-07 cross-OS parity rule,
a single-OS-only fix here would be wrong: a Windows machine
reading a Mac-prepped SSD has the identical bug.

## Architecture

### Data source

Use `ModelOperations.DiscoverModelsOnDisk(modelsRoot)` —
already exposed by MAC29, returns `IReadOnlyList<DiscoveredModel>`
(or whatever shape MAC29 landed; confirm at kickoff). Pair with
`FindModelBlobForModel` only if the picker needs blob metadata
(it doesn't today — just names — but verify).

### `ModelManagementService` rework

Today:

```csharp
public List<string> GetInstalledModelNames(PortableConfig config)
{
    return config.Models
        .Where(m => m.Status == ModelInstallStatus.Installed)
        .Select(m => m.Name)
        .ToList();
}
```

After:

```csharp
public List<string> GetInstalledModelNames(PortableConfig config, string ssdRoot)
{
    var modelsRoot = Path.Combine(ssdRoot, SsdLayout.Models);
    var onDisk = ModelOperations.DiscoverModelsOnDisk(modelsRoot)
        .Select(m => m.Name) // confirm shape at kickoff
        .Where(n => !string.IsNullOrWhiteSpace(n))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
        .ToList();
    return onDisk;
}
```

Same shape applied to `GetModelSizingWarnings`. Keep `config`
in the parameter list because sizing warnings still want the
config-pinned hardware hints if present — the swap is "decide
what's installed by disk truth, then look up sizing per name".

If the `ssdRoot` plumbing through `MainWindow.xaml.cs` and
`RunnerLocalApiService.cs` looks ugly, alternative: inject an
`IRunnerContext` that exposes `SsdRoot` and have the service
ctor capture it. But the simpler "pass `ssdRoot` through" is
likely cleaner for one PR — surface the call at kickoff.

### `RunnerLocalApiService` rework

Inject `IModelManagementService` (or reuse the existing instance
if there's already one in the service collection — there is for
the WPF runner; confirm for the Mac runner host's DI graph) and
replace lines 159-165 with `_modelService.GetInstalledModelNames(config, ssdRoot)`.

The existing `Distinct` / `OrderBy` is preserved inside the
centralized method.

### Optional: opportunistic config rebuild on unlock

When the Runner unlocks an encrypted config:

```csharp
if (config.Models.Count == 0 || !config.Models.Any(m => m.Status == ModelInstallStatus.Installed))
{
    var disk = ModelOperations.DiscoverModelsOnDisk(modelsRoot);
    if (disk.Count > 0)
    {
        foreach (var m in disk)
        {
            config.Models.Add(new ModelEntry
            {
                Name = m.Name,
                Status = ModelInstallStatus.Installed,
                // sha256 / sizeBytes filled from FindModelBlobForModel
            });
        }
        await _configStore.SaveAsync(ssdRoot, config, unlockMaterial, ct);
    }
}
```

Bias toward shipping this in the same PR — it's small, self-heals
already-prepped SSDs, and makes the disk-truth fallback amortized
free for future reads. Skip if the unlock code path is in a
location that makes the writeback awkward to wire (e.g., the
plaintext-config path doesn't need it, so guard the call with
`if (isEncrypted)`).

**Don't ship the writeback** if it would mean changing the
MAC5 plaintext invariant in any way — it must only fire when an
encrypted config was unlocked, so the writeback re-encrypts using
the same unlock material and the SSD never gains a plaintext
config file.

## Tests

### Unit tests in `tests/ModelManagementServiceTests.cs` (new or extend existing)

- `GetInstalledModelNames_DiskTruth_ReturnsModelsOnDisk_WhenConfigEmpty` —
  the field-bug pin. Synthesize an SSD layout with `models/blobs/sha256-...`
  and `models/manifests/registry.ollama.ai/library/llama3.2/1b`,
  feed an empty `config.Models`, assert the method returns
  `["llama3.2:1b"]`.
- `GetInstalledModelNames_DiskTruth_ReturnsMultipleModels` —
  two-model layout, assert both surface in alphabetical order.
- `GetInstalledModelNames_NoModelsAnywhere_ReturnsEmpty` —
  empty SSD, empty config, assert empty list. No throw.
- `GetInstalledModelNames_DiskTruthSupersedesConfig` — config
  has a stale "Installed" entry for a model whose blob was
  deleted; assert the method returns disk truth (no stale
  entry). Pins that we don't union the two sources.
- `GetModelSizingWarnings_DiskTruth_PicksUpModelOnDisk` —
  same layout as the first test, low-RAM `ISystemResourceProbe`,
  assert a warning is produced for the disk-discovered model.

### API-level test in `tests/RunnerLocalApiServiceTests.cs`

- `Models_Endpoint_DiskTruth_ReturnsModelsOnDisk_WhenConfigEmpty` —
  drive HTTP through the existing TestServer harness, assert
  `GET /models` returns `["llama3.2:1b"]` against an empty
  config + on-disk blob layout.

### Optional unlock-rebuild test (only if shipping that piece)

- `Runner_Unlock_OpportunisticRebuild_PersistsDiskModelsToEncryptedConfig` —
  encrypted config with empty Models + disk has model →
  unlock → reload encrypted config → assert `config.Models`
  contains the disk-discovered entry. Plaintext-config path
  unchanged.

### What NOT to test

- Don't snapshot the SwiftUI picker — no Swift unit harness for
  view bindings. Manual smoke covers it.
- Don't add new mac-runner-host smoke tests unless the Mac side
  reads `config.Models` directly somewhere we discover at kickoff.
  The LAN endpoint path is already tested by the C# API test.

## Security

MAC33 is a read-path fix; no new attack surface. Confirm during
execution:
- **MAC5 plaintext invariant.** The optional writeback only runs
  when an encrypted config was unlocked, and re-encrypts with the
  same unlock material. No plaintext file is ever produced on a
  Mac-prepped SSD.
- **Encrypted-config format.** No new encrypted-payload fields;
  `ModelEntry` schema unchanged. Cross-language fixture under
  `tests/Fixtures/MacEncryptedConfig/` stays untouched.
- **`PathGuards` for `modelsRoot`.** Use the same pattern as
  MAC29's ReadinessService swap so `models/blobs/...` paths can't
  escape via traversal. (`DiscoverModelsOnDisk` already does this;
  spot-check.)
- **No new process launches.** No `ProcessRunner.ArgumentList`
  surface added.
- **URL allowlist unchanged.**

## Out of scope

- **Mac sidecar writeback during pull.** Architectural fix
  (re-derive the key per pull, or route pulls through the host
  process where the key stays warm) is correctly noted in
  `mac_project_backlog.md` MAC33 as deferred. The Runner-side
  disk-truth fix + opportunistic unlock rebuild handle the
  user-visible symptom; the sidecar fix is a different shape and
  belongs in its own ticket if it ever becomes load-bearing.
- **MAC30 encryption-optional toggle.** Independent. MAC33 ships
  first.
- **MAC31 pull UX (cancel button, ANSI strip, partial-progress
  preservation).** Separate ticket.
- **MAC32 Finish button no-op.** Separate ticket; needs a
  product call.
- **Sizing warning's accuracy on Mac.** Out of scope to validate
  manually here; the swap means warnings start firing on Mac
  where they were silent before, which is correct.
- **Companion app UI changes.** Companion picks up the new
  `/models` shape transparently — no client-side changes.

## Do not change

- **Encrypted-config format.** No schema changes. Optional
  writeback re-uses the existing `ModelEntry` shape.
- **MAC5 plaintext invariant.** No Mac-side plaintext config
  writes added. Writeback is encrypted-only.
- **`/models` endpoint contract.** Returns `{models: string[]}`
  as today — only the data source changes.
- **`ModelInstallStatus` enum / `ModelEntry` shape.** Stays
  intact for back-compat.
- **`URL allowlist`, `ProcessRunner.ArgumentList`, `PathGuards`
  guardrails.** None of these are touched. Confirm at PR end.

## CI workflow

Branch: `kninetimmy/mac33-runner-disk-truth-models`.

Local validation (Windows-only — user's Mac doesn't have
`dotnet`):
- `dotnet build FreeAiSsd.sln -c Release`
- `dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal`
- Run the new `ModelManagementServiceTests` and
  `RunnerLocalApiServiceTests` cases specifically to confirm.

CI required jobs (must all be green before merge):
- `windows-build` — full restore / build / test / WPF
  guardrails / publish.
- `mac-runner-build` — Swift unit tests, Mac host publish, Mac
  host smoke, Runner.app bundle.
- `mac-prep-build` — unaffected by MAC33 but runs as required.
- `package-release` — skipped on PR (no release tag).

Expect CI green on first or second run. If a CI run fails,
fix-forward on the same branch; do not skip hooks; do not amend.

## Post-merge

- Update `agent_docs/project_state.md`:
  - Move PR entry into `Recently shipped`.
  - Bump `Last updated`.
  - Reorder `Next up` so MAC31 (pull UX) is #1 with MAC32 (Finish
    button product call) #2 and MAC30 (encryption-optional) #3.
- Append `agent_docs/mac_project_backlog.md` MAC33 status to
  **done** with PR reference.
- Capture the cross-OS execution decision (bundle held; if it
  split, note why) in `project_decisions.md` only if it deviated
  from the planning-phase call.
- Dispatch `gh workflow run build.yml --ref main -f
  version=1.3.11 -f include_macos=true` once the user confirms.
- **Manual smoke deferred to a real Mac + an external SSD:**
  - Prep a fresh SSD with `llama3.2:1b` (encryption ON, Mac
    PrepApp).
  - Eject, replug.
  - Open Runner.app off the SSD, enter passphrase.
  - Confirm the model picker shows `llama3.2:1b`.
  - Select it, send a chat, confirm response arrives.
  - Cross-OS roundtrip: same SSD plugged into a Windows machine,
    Windows Runner, unlock, picker shows the same model, chat
    works.
  - If the optional writeback shipped: re-unlock the SSD, confirm
    `config.Models` is now populated (read via PrepApp on a
    second unlock, or via a debug log line that fires only once
    per session).

## Open questions to resolve at execution start

1. **Mac Runner picker source.** Confirm via `mac-runner/Sources/`
   grep whether the Swift picker hits the LAN endpoint `/models`
   (most likely) or reads `config.Models` directly from a host
   JSON line. If LAN, no Swift code changes needed and the C#
   fix in `RunnerLocalApiService` covers Mac. If direct, decide
   between routing through the host vs adding a parallel
   disk-truth path on the Swift side.
2. **`ssdRoot` plumbing.** `GetInstalledModelNames` needs the
   SSD root to compute `modelsRoot`. Pass it as a parameter
   (cheapest), or capture it on the service ctor (cleaner if the
   Runner already constructs the service with SSD-root context).
   Surface at kickoff after reading the WPF runner's DI wiring.
3. **Opportunistic unlock writeback in scope?** Default: yes,
   ship it in this PR. Skip only if the unlock call site makes
   the writeback awkward (e.g., the encrypted-vs-plaintext
   branching is in a place where reaching `_configStore.SaveAsync`
   would mean a wider refactor). Surface the choice at kickoff.
4. **`DiscoverModelsOnDisk` return shape.** MAC29 introduced this
   helper; confirm at kickoff whether it returns
   `(string Name, string? Sha256, long? SizeBytes)` tuples or a
   typed record. Picker only needs `Name` — but the optional
   writeback wants `Sha256` + `SizeBytes` to populate ModelEntry.
   If the helper doesn't expose those, pair with
   `FindModelBlobForModel` which does.
5. **`GetModelSizingWarnings` cross-platform behavior change.**
   Today it's a no-op on Mac (because `config.Models` is empty);
   after MAC33 it'll start firing. Confirm `ModelSizingCatalog.Suggest`
   accepts `name:tag` strings (it should — it's already called
   with `config.Models[].Name` on Windows). If it doesn't, scope
   sizing-warning behavior on Mac to a follow-up.
