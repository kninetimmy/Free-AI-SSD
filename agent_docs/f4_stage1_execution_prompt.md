# F4 Stage 1 Execution Prompt

- Item: `F4 Stage 1`
- Status: `approved`
- Saved: `2026-04-21`
- Recommended execution model: `gpt-5.4`

Use the prompt below to resume in a fresh session.

```text
Implement F4 Stage 1 only in C:\Users\Kninetimmy\free-ai-ssd.

Goal:
Move first-run profile ownership from Runner to PrepApp FTUE, and add the two-machine architecture explainer as the first FTUE step. Do not implement F4 Stages 2-4 yet.

Repo context:
- F3 is merged via PR #165.
- PrepApp currently has a 3-step FTUE spotlight in `prep-app/MainWindow.xaml` and `prep-app/MainWindow.xaml.cs`.
- Runner currently enforces profile selection on first load in `runner/MainWindow.xaml.cs` via `ShowProfileSelectionAsync(isRequired: true)`.
- Profile is stored on `PortableConfig.ActiveProfile` and defaults are applied via `shared/Profile/ProfileDefaults.cs`.
- `shared/` is a plain `net8.0` library, not a WPF host. Do not move XAML windows into `shared/`.
- B2 LAN discovery is future work. Do not redesign companion host discovery here.
- Security invariants are non-negotiable: keep encryption, path guarding, and process-launch safety intact.
- If Stage 1 changes the first-run ownership behavior, append a superseding entry to `agent_docs/project_decisions.md`.
- Do not update README yet; F4 as a whole is not shipped.

Implement:
1. PrepApp becomes the first-run profile owner.
- Extend the PrepApp FTUE flow from 3 steps to 4 steps:
  1) two-machine architecture explainer,
  2) choose profile,
  3) pick target drive,
  4) choose/download model(s).
- Implement the profile choice inside PrepApp's FTUE flow. Prefer a PrepApp-native inline surface over reusing Runner's modal dialog.
- Reuse the current profile copy and visual language as reference, but keep the implementation local to PrepApp.
- Track the selected profile in `PrepViewModel`.
- Persist enough local FTUE/profile state in `PrepTargetPreferenceStore` so closing and reopening PrepApp does not forget the selection or restart the flow incorrectly.
- Make profile selection required before finalization. If the user tries to finalize without a selected profile, show a clear themed warning and block the action.

2. Finalize writes profile ownership to SSD config.
- In `shared/ViewModels/PrepViewModel.cs`, before the final config save/encryption path completes, set `config.ActiveProfile` to the chosen value and call `ProfileDefaults.Apply(config, selectedProfile)`.
- Preserve the rest of the finalize flow.

3. Runner stops enforcing the first-run profile dialog.
- Remove the required first-run profile prompt from `runner/MainWindow.xaml.cs`.
- Runner must still load and run if `ActiveProfile` is null on an older SSD.
- Keep `RefreshProfileVisibility()` and the in-window profile pill toggle behavior intact.
- Prefer isolating the Runner change to `MainWindow.xaml.cs`. Only change `runner/App.xaml.cs` if strictly necessary.
- Do not move `ProfileSelectionDialog` into `shared/`. If it becomes unused, either leave it in place or remove it only if that is clearly low-risk.

Likely files to touch:
- `prep-app/MainWindow.xaml`
- `prep-app/MainWindow.xaml.cs`
- `shared/ViewModels/PrepViewModel.cs`
- `prep-app/PrepTargetPreferenceStore.cs`
- `runner/MainWindow.xaml.cs`
- possibly `shared/PortableConfig.cs` comments only if they are now misleading
- `agent_docs/project_decisions.md`

Likely tests to update:
- `tests/PrepViewModelTests.cs`
- `tests/ProfileDefaultsTests.cs`
- add more test coverage only where it materially protects the new behavior

Constraints:
- Do not start Stage 2 completion-flow work.
- Do not start Stage 3 companion install target selector work.
- Do not start Stage 4 local installer logic.
- Do not add new dependencies unless absolutely necessary.
- Preserve the existing neumorphic dark theme and shared control patterns.
- Preserve backward compatibility for older SSDs where `ActiveProfile` is still null.

Acceptance criteria:
- Fresh PrepApp FTUE starts with a two-machine architecture explainer.
- PrepApp requires a profile choice before SSD finalization can complete.
- Finalized SSD config contains `ActiveProfile` and the matching profile defaults.
- Runner no longer blocks startup with a required profile dialog.
- Older configs with `ActiveProfile = null` still open Runner successfully, with flight-sim-only UI hidden by default.
- Tests cover the new finalize/profile behavior and any practical no-prompt Runner behavior.

Validation:
- Run `dotnet build FreeAiSsd.sln -c Release`
- Run relevant tests, at minimum `tests/PrepViewModelTests.cs` and `tests/ProfileDefaultsTests.cs`, or the full `dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal` suite if practical
- Call out any manual-smoke gaps, especially PrepApp FTUE visuals
```
