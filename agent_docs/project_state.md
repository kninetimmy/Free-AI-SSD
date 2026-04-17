# Project State

Last updated: 2026-04-17

## Currently building

Between tasks. README update queued for next session.

## Last session

2026-04-17 — Two PRs shipped. PR #120 (eb6f2c1) committed the prep-app
UI polish pass: implicit theme styles for DataGrid, TabControl, GroupBox,
CheckBox in Controls.xaml; Model Manager layout restructured; Drive Setup
fully themed; warning strip made collapsible. PR #121 (d9b6fd3) fixed the
drive selection bug — USB SSDs that Windows classifies as DriveType.Fixed
now appear in the default dropdown via WMI InterfaceType detection.
311/311 tests green throughout.

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

1. **README update** — discussed at end of session, details TBD
2. **Resume 1.1.0 test pass** — drive selection fix is now in main; full
   pass (model pull, voice, RAG) can proceed once Stephen tests with SSD

## Open questions for Stephen

[none currently]

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
- USB SSD drive detection uses WMI `Win32_DiskDrive WHERE InterfaceType='USB'` to determine external drives regardless of Windows DriveType. Internal drives still require the ShowFixedDrives toggle. WMI failure falls back silently to DriveType-only — fail-open is acceptable here (drive enumeration, not a security gate).
