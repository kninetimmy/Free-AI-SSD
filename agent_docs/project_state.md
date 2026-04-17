# Project State

Last updated: 2026-04-16

## Currently building
Finishing the profile switcher UX. Core data model and first-run dialog are
done. Three remaining items before the feature is shippable:
1. Richer in-dialog profile descriptions (bullet lists of what's included)
2. Restart notification when profile is changed mid-session
3. Replace "Switch Profile" button with an inline two-segment pill toggle
   (active segment = TactilePrimaryButton, inactive = GhostSecondaryButton)

## Last session
2026-04-16 — Built the full profile switcher foundation. No commits yet —
all work is staged in the working tree across 8 modified/new files.

What got done:
- `shared/Profile/UserProfile.cs` — `FlightSim` / `GeneralAssistant` enum
- `shared/Profile/ProfileDefaults.cs` — static `Apply()` that sets PTT/TTS
  defaults per profile
- `shared/PortableConfig.cs` — `ActiveProfile` property added (nullable;
  null = first-run dialog pending)
- `runner/ProfileSelectionDialog.xaml/.cs` — neumorphic two-card dialog;
  blocks close without selection on first run; cyan glow on selected card
- `runner/MainWindow.xaml` — "Switch Profile" button added to top bar;
  `BindingsImportCard` and `PttCard` are the two flight-sim panels to gate
- `runner/MainWindow.xaml.cs` — `RefreshProfileVisibility()`,
  `ShowProfileSelectionAsync(isRequired)`, `SwitchProfile_Click`;
  `OnWindowLoaded` shows profile dialog before FTUE when `ActiveProfile` is null
- `tests/ProfileDefaultsTests.cs` — 9 tests, all green (311/311 suite)
- `global.json` + `tests/FreeAiSsd.Tests.csproj` — bumped from .NET 8 to
  .NET 10 SDK/TFM (only x64 .NET 10 runtime is installed on this machine;
  .NET 8 x64 runtime is absent)

Notable: PTT/TTS services don't re-initialize after a mid-session profile
change — they pick up new config values on next launch. This is intentional
but needs a user-facing restart message (item 2 above).

2026-04-16 — Initial project state bootstrap.

## Next up
1. Richer profile descriptions in `ProfileSelectionDialog.xaml` — bullet
   lists beneath each card (no hover required)
2. Restart notification in `ShowProfileSelectionAsync` — `MessageBox` when
   switching mid-session; suppressed on the initial first-run selection
3. Replace `SwitchProfileButton` (GhostSecondaryButton) with a two-segment
   pill toggle in `TopControlsCard` — direct switch, no dialog re-open
4. Commit all working-tree changes on a feature branch and open a PR

## Open questions for Stephen
None.

## Stable decisions (don't revisit)
- Only two profiles: **Flight Sim** and **General Assistant** — no custom/third profiles
- UI/UX must follow existing neumorphic dark theme and shared controls (Colors.xaml, Controls.xaml, etc.)
- Profile is switchable after first launch (not a one-time setup choice)
- Profile stored as `ActiveProfile` (`UserProfile` enum) on `PortableConfig` — no separate file
- First-run profile dialog is **required** — user must choose before the app proceeds; no default assumed
- .NET SDK/TFM bumped to 10.0 — x64 .NET 8 runtime not present on dev machine; shared lib stays net8.0, tests target net10.0, WPF apps stay net8.0-windows (runtime is installed x86 only for 8.0)
- `BindingsImportCard` and `PttCard` are the two XAML elements gated by profile — do not add a third without updating `RefreshProfileVisibility()`
- Mid-session profile changes save to config but don't re-init services — restart required for voice features; this is by design
