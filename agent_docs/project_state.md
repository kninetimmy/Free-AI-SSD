# Project State

Last updated: 2026-04-17

## Currently building

Prep-app UI polish pass complete (unstaged). Drive selection bug still
outstanding — next focus once changes are committed/PRed.

## Last session

2026-04-17 — No commits. Extensive prep-app UI polish pass across
`prep-app/MainWindow.xaml` and `shared/UI/Theme/Controls.xaml`. Added
implicit dark-theme styles for DataGrid, TabControl/TabItem, GroupBox,
and CheckBox (with hover/focus/pressed states). Restructured the Model
Manager layout: model tag input moved inline with tab strip, Starter/
Configured model card proportions swapped (2*/3*), button rows shrunk,
card padding tightened globally. Drive Setup tab now fully themed
(styled textboxes, checkboxes, GroupBoxes, buttons). Drive warning
strip moved to its own collapsible Row 2 (hidden when no warning);
LED removed from log header. "Browse starter models" dead button
removed. Codex adversarial review ran and both findings addressed
(CheckBox keyboard focus restored, warning strip safety regression fixed).
All changes build clean, no test regressions expected (XAML-only).

2026-04-16 — No commits. v1.1.0 confirmed shipped. First test pass identified
two UI bugs (theme mismatches and broken drive selection). Testing stalled
because the SSD couldn't be selected — drive selection bug is the blocker.

2026-04-16 — Finished and shipped the profile switcher (dab8692 / PR #119,
merged e636a60). Completed the three remaining UX items: bullet-list card
descriptions in `ProfileSelectionDialog.xaml`, a `NotifyRestartRequired()`
`MessageBox` helper called on mid-session profile change, and a two-segment
`PillRadioButton` inline toggle replacing the old `SwitchProfileButton`.
311/311 tests green. Also triggered a v1.1.0 `workflow_dispatch` release
build (Windows only, no macOS) at end of session.

## Next up

### v1.1.0 bug fix pass

1. **Drive selection broken (blocker)** — Stephen's SSD is reported as
   "Fixed" by Windows so it doesn't appear in the dropdown. Options:
   - Expose all drives and filter out true internal drives by connection
     type (SATA/NVMe via WMI `Win32_DiskDrive.InterfaceType` or
     `PnPDeviceID` prefix) rather than by Windows "fixed" flag
   - Prefer connection-type approach if reliable — the toggle was a
     workaround for a bad default, not a feature
2. **Commit/PR the UI polish changes** — `prep-app/MainWindow.xaml` and
   `shared/UI/Theme/Controls.xaml` are unstaged; need a PR before the
   next test pass

### After bugs are resolved

- Resume full 1.1.0 test pass (model pull, voice, RAG) once drive selection works
- Pick next feature

## Open questions for Stephen

- For the drive filter fix: preference on connection-type detection vs. restoring the toggle?

## Stable decisions (don't revisit)

- Only two profiles: **Flight Sim** and **General Assistant** — no custom/third profiles
- UI/UX must follow existing neumorphic dark theme and shared controls (Colors.xaml, Controls.xaml, etc.)
- Profile is switchable after first launch (not a one-time setup choice)
- Profile stored as `ActiveProfile` (`UserProfile` enum) on `PortableConfig` — no separate file
- First-run profile dialog is **required** — user must choose before the app proceeds; no default assumed
- .NET SDK/TFM bumped to 10.0 — x64 .NET 8 runtime not present on dev machine; shared lib stays net8.0, tests target net10.0, WPF apps stay net8.0-windows (runtime is installed x86 only for 8.0)
- `BindingsImportCard` and `PttCard` are the two XAML elements gated by profile — do not add a third without updating `RefreshProfileVisibility()`
- Mid-session profile changes save to config but don't re-init services — restart required for voice features; this is by design
- Pill toggle does a **direct apply** (no dialog re-open) — `ProfilePill_Checked` handler applies profile, saves config, calls `NotifyRestartRequired()` directly
- DataGrid, TabControl/TabItem, GroupBox, CheckBox all styled via implicit styles in `Controls.xaml` — do not add per-control inline styling for these in WPF hosts
- Drive warning (`SelectedDriveWarning`) lives in its own collapsible strip (Row 2 of root grid), not in the log header — keep it there for safety visibility
- Model tag input overlays the tab strip via `Panel.ZIndex=2` + `BgBaseBrush` background — intentional, not a z-order bug
