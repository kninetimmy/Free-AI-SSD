# Mac UI Reconstruction — Reference for the Windows UX Parity Effort

**Audience:** the Claude agent doing the Windows WPF UX work. You may have
zero macOS context. This file is the spec; the `screenshots/` folder next to
it is the visual calibration. Read this top to bottom before touching XAML.

**Source of truth:** reconstructed directly from the SwiftUI source on
2026-05-19:
- `mac-prep-app/Sources/main.swift`, `PrepFlowStep.swift`,
  `StarterModelPickerView.swift`, `ManageModelsStepView.swift`,
  `BrandColors.swift`
- `mac-runner/Sources/main.swift`

If the Swift source has moved on since that date, treat this as drifted and
re-derive — the flow lives in `PrepFlowStep` (enum = screen list) and every
`vm.currentStep = .x` / `status = "…"` assignment (the transition edges).

---

## 0. READ THIS FIRST — the guardrail

A project decision (memhub `decision #38`, "Mac UI design language:
brand-tinted native", authored by the project owner) and an earlier
neumorphic-theme lock bear on this work. The owner has since relaxed the
theme lock (see item 2); the rest still holds:

1. **The goal is NOT pixel-porting the Mac look to Windows.** Much of why the
   Mac app feels clean is that it uses **native macOS controls** (stock
   SwiftUI buttons, `Form`, `List`, `NSAlert`, native sheets, SF Pro). WPF has
   no equivalent. Reproducing macOS chrome in WPF is explicitly the wrong
   move.
2. **The Windows neumorphic theme is NO LONGER a hard lock.** An earlier
   decision locked in the neumorphic scheme (`shared/UI/Theme/*.xaml`), but
   that was made *before* the Mac/Swift UI existed and nobody knew what it
   would look like. The project owner has since done the Mac work and
   **explicitly authorized the Windows UI moving toward the Mac look** — a
   consistent cross-platform experience now takes priority over preserving
   neumorphic. You **may** restyle the shared theme in service of that. This
   is a deliberate relaxation of the earlier lock by the owner, not a
   violation. (Still: make theme changes coherently and in reviewable chunks —
   "lenient" means you don't need a re-approval gate, not that the theme is
   unowned.)
3. **What you ARE matching:** the *flow structure* and *information density* —
   one decision per screen, progressive disclosure, calm spacing, sparse
   accent use. That is portable. Pixels are not.
4. **Do not delete Windows-only features** (voice I/O, HOTAS PTT, DCS binding
   import). They have no Mac equivalent — see §6. Mac is partly "cleaner"
   because it does less.

If a screen here looks trivially simple, that simplicity *is* the spec — it is
the thing being asked for, not a gap in this document.

---

## 1. What makes the Mac app feel clean (the portable design language)

These are the patterns to carry into WPF — none of them require native macOS
controls:

- **One-thing-per-screen state machine.** The prep app is a single window that
  renders exactly one step at a time from a 13-case enum (`PrepFlowStep`).
  Never a tab grid of everything at once. The Windows prep tool today is a
  dense two-tab window (`prep-app/MainWindow.xaml`, ~1,053 XAML lines) showing
  everything simultaneously — that is the single biggest divergence.
- **A persistent header with a step counter.** Every prep screen shows
  `Free AI SSD — Prep` (title2, bold) on the left and a progress label on the
  right (`1 / 6 — Choose drive`, `4 / 6 — Encryption`, …). Constant orientation.
- **Vertical rhythm.** Outer padding `20`. Inter-element `spacing: 12–16`.
  Captions are `.caption`/`.secondary`. There is a lot of whitespace; screens
  are never full.
- **Primary action bottom-right, secondary bottom-left.** `Continue` / `Erase`
  / `Quit` is always bottom-right and is the default (Enter) action. `Back` /
  `Start over` sits bottom-left. Consistent across every screen.
- **Progressive disclosure.** Encryption passphrase fields only appear when the
  encrypt toggle is on. "Add a model" on the manage screen is a collapsed
  `DisclosureGroup`. The model picker's HF token field only shows for the HF
  source. Complexity is hidden until chosen.
- **Sparse brand tint, native everything else.** Backgrounds are system
  surfaces, not hardcoded hex. Color is used only for *meaning*: cyan accent
  for the one focal action/banner, status colors for status. See §2.
- **Status as color + word, never color alone.** Readiness rows are a colored
  dot + name + status word. Error = red banner. RAG-without-sources = orange
  banner. The two never collapse into one signal.
- **Plain-language, non-fatal framing.** Failure copy says what happened, that
  it's recoverable, and the next move ("non-fatal; you can retry now or
  continue and pull later from Mac Runner").

---

## 2. Brand tokens (already shared with Windows)

The Mac side pulls these from the WPF `shared/UI/Theme/Colors.xaml` palette —
**the palette is already common across both platforms.** Defined in
`mac-prep-app/Sources/BrandColors.swift`. Use sparingly (accents/status only),
never as control-chrome or background fills.

| Token | Hex | Use |
|---|---|---|
| `brandAccentCyan` | `#00E5FF` | primary accent: focal action, info banner outline, count caption |
| `brandAccentMagenta` | `#FF2D92` | destructive emphasis (NOT the confirm button itself) |
| `brandAccentPurple` | `#8A2BE2` | tertiary (largely unused on Mac) |
| `brandStatusSuccess` | `#4CE0B3` | success dot/pill |
| `brandStatusWarning` | `#FFB454` | warning banner/text |
| `brandStatusDanger` | `#FF4D6D` | error dot/banner |
| `brandStatusInfo` | `#5BC0FF` | "External" badge, size-tier chip |

Windows already has the equivalents (`StatusSuccessBrush`, `AccentCyanColor`,
etc.) in the shared theme. Parity here means *using them with the same
restraint*, not introducing new colors.

---

## 3. App A — Mac Prep (`mac-prep-app`)

Single window, `minWidth 720 × minHeight 540`, `.accentColor(.brandAccentCyan)`.
Layout per screen: header + `Divider()` + the step body (`.padding(20)`).

**Flow graph (edges are real `vm.*` calls in source):**

```
welcome ──Get started──▶ driveSelection
driveSelection ──Continue──▶ eraseConfirmation
   │ (if drive already configured) ──Manage models──▶ manageModels
   │ (if drive already configured) ──Start over──▶ (fresh format path)
eraseConfirmation ──Back──▶ driveSelection
eraseConfirmation ──Erase──▶ [native NSAlert] ──▶ formatting
formatting ──success──▶ staging ──▶ encryptionSetup
formatting ──failure──▶ failed
encryptionSetup ──Continue──▶ modelPull
modelPull ──success──▶ readiness ──▶ done
modelPull ──cancel──▶ modelPullPaused ──Retry──▶ (resume) / ──Skip──▶ readiness / ──Start over──▶ welcome
modelPull ──failure──▶ modelPullFailed ──Retry──▶ (retry) / ──Continue──▶ readiness
manageModels ──Done──▶ (exits flow)
failed ──Restart──▶ (restart)
```

### A1 — Welcome — `prep-01-welcome.png`
- **Step label:** `Welcome`
- **Contents:** H1 "Prepare a drive for Free AI SSD"; one secondary paragraph
  explaining it stages Ollama + Runner + encrypted config, and warns the drive
  will be erased; spacer; bottom-right large `Get started` (default action).
- **Windows counterpart:** none today — the Windows prep tool drops straight
  into the two-tab UI with no intro/orientation screen. **Add one.**

### A2 — Drive selection — `prep-02-drive-selection.png`
- **Step label:** `1 / 6 — Choose drive`
- **Contents:** headline "Pick a drive to prepare" + `Refresh` (top-right);
  status line (secondary); selectable `List` of drives — each row: display
  name (body) over `identifier — size` (caption), and a cyan-info `External`
  badge if removable; `Form` with `Volume label` text field + `Also prepare
  for Windows (cross-platform)` toggle (disabled unless a fresh format is
  valid); bottom-right `Continue` (disabled until a drive is selected).
- **Conditional:** if the selected drive already carries our config marker, a
  **cyan-tinted banner** (`#00E5FF` @ 0.12 bg, 1px cyan stroke) appears above
  the form with `Manage models` and `Start over (formats drive)` buttons, and
  the fresh-format form is disabled.
- **Windows counterpart:** `prep-app/MainWindow.xaml` **Drives tab** — exists
  but is one half of an always-on two-tab surface. Parity = make it a discrete
  step, keep the already-configured banner pattern.

### A3 — Erase confirmation — `prep-03-erase-confirmation.png`
- **Step label:** `2 / 6 — Confirm erase`
- **Contents:** headline "Confirm destructive operation"; secondary block
  listing Drive / Size / Format (exFAT) / Mount; a sentence warning the OS
  will show a final confirmation and may ask for a password; bottom-left
  `Back` (→ drive selection), bottom-right `Erase` (default).
- **Note:** clicking `Erase` triggers a **native macOS `NSAlert`** (system red
  destructive button) before anything destructive runs. On Windows the
  equivalent is `EraseConfirmDialog.xaml` / `FixedDriveConfirmDialog.xaml` —
  keep them as distinct, unmistakable confirmation dialogs.

### A4 — Formatting — `prep-04-formatting.png`
- **Step label:** `2 / 6 — Formatting`
- **Contents:** `ProgressLogStepView` — headline "Formatting drive…" + small
  inline `ProgressView` spinner while busy; a monospaced auto-scrolling log
  pane (`textBackgroundColor` surface, rounded). No buttons; advances itself.
- ⚠️ **Look-alike:** A4/A5/A8 are the *same* view with only the title
  changed. Capture all three; keep the title text in frame.

### A5 — Staging — `prep-05-staging.png` *(optional — not captured; A4 covers the visual)*
- **Step label:** `3 / 6 — Staging` · Same view as A4, title "Staging
  artifacts…". The `prep-04-formatting.png` screenshot is the visual reference
  for this view; only the title text differs here.

### A6 — Encryption setup — `prep-06-encryption-setup.png`
- **Step label:** `4 / 6 — Encryption`
- **Contents:** headline "Set up encryption"; `Encrypt SSD config` checkbox
  toggle, **default OFF**. *If ON*: a `Form` with `Passphrase` + `Confirm
  passphrase` `SecureField`s and a caption warning there is no recovery path.
  *If OFF*: a caption explaining encryption is optional/recommended for LAN
  exposure. `Divider()`. Then the **embedded model picker** (§A-MP). A caption
  noting the pull is non-fatal. Bottom-right button whose label flips:
  `Write encryption & continue` / `Continue without encryption` (disabled if
  encryption on and passphrases empty/mismatched).
- **Windows counterpart:** `EncryptionSetupDialog.xaml` (the toggle/passphrase)
  + the **Models tab** (the picker). On Mac these are one scrollable step;
  Windows splits them across a dialog and a tab.

### A-MP — Starter model picker (embedded component) — `prep-06b-model-picker.png`
Reused in A6 and in A9's "Add a model". `StarterModelPickerView`, ~400 lines,
the single most sophisticated piece of UI in either Mac app. Rows top→bottom:
- **Action row:** "Starter models" heading; source `Picker` (Ollama /
  Hugging Face); `Most popular ✓` toggle-button; `Top N` limit picker;
  `Search models…` field; `Refresh` button (shows spinner + "Refreshing…").
- **Status captions:** catalog status (secondary) + a bold cyan visible-row
  count caption when a filter is active.
- **HF token row** (only when source = Hugging Face): `HF token (optional)`
  `SecureField` + a plaintext-storage warning if encryption is off.
- **Filter row:** `Max size` picker (All/≤7B/≤14B/≤30B/≤70B); `Capabilities`
  chips (Tools / Vision / Thinking / Audio, checkmark when active, AND
  semantics); `Sort` picker (Popular / Newest / A–Z).
- **List body:** scrolling rows — checkbox toggle, model tag (bold), a
  cyan-info size-tier chip, pull-count, per-quant size; HF repos are
  expandable (▶/▼ chevron) with indented quant children; rows surviving a chip
  filter only via missing capability data are muted to 0.55 opacity. Empty
  state explains *why* (catalog empty vs filter too tight) and the next move.
- **Windows counterpart:** the **Models tab** in `prep-app/MainWindow.xaml`.
  This is the one place the Mac UI is *more* capable than Windows (live HF
  GGUF search, capability chips, quant expansion). Parity may mean leveling
  Windows *up* here, not down.

### A7 — Model pull — `prep-07-model-pull.png`
- **Step label:** `5 / 6 — Models`
- **Contents:** headline "Pulling starter models…" + spinner; top-right
  `Cancel` (when cancellable); a single in-place monospaced progress line
  ("Preparing pull…" → live `Pulling <tag>…`); below it the auto-scrolling
  diagnostic log pane.

### A7a — Model pull paused — `prep-07a-pull-paused.png` *(optional capture)*
- **Step label:** `5 / 6 — Pull paused` · Reached only by cancelling A7.
- **Contents:** headline "Pull paused"; explanation that the partial download
  is preserved and Retry resumes; optional snapshot line; footer:
  `Start over` (left) · `Skip` · `Retry` (right, default, large).
- ⚠️ Requires inducing a cancel — capture only if convenient; spec is here.

### A7b — Model pull failed — `prep-07b-pull-failed.png` *(optional capture)*
- **Step label:** `5 / 6 — Pull failed` · Reached only on a real pull failure.
- **Contents:** headline "Model pull needs attention"; non-fatal summary
  ("N models failed to pull… retry now or continue and pull later"); list of
  failed tags (monospaced); "Recent log" pane; footer: `Continue to readiness`
  · `Retry failed pulls` (default, large, disabled if none/busy).
- ⚠️ Hard to trigger — capture only if convenient; spec is here.

### A8 — Readiness — `prep-08-readiness.png` *(optional — not captured; A4 covers the visual)*
- **Step label:** `6 / 6 — Readiness` · Same view as A4, title "Running
  readiness checks…". Transient/flash-by step; `prep-04-formatting.png` is the
  visual reference (identical layout, only the title differs). The readiness
  *results* list is on the Done screen (`prep-11-done.png`), not here.

### A9 — Manage models — `prep-09-manage-models.png`
- **Step label:** `Manage models` · Reached from the A2 already-configured
  banner, not the linear path.
- **Contents:** header "Manage models" (title2 bold) + `Drive: <name>`
  subtitle. **Banners:** if encrypted & locked → yellow banner + `Unlock…`
  button (opens A-Unlock sheet); if unlocked → green `✓ Unlocked for this
  session`; if prior pull failures → yellow banner + `Retry failed pulls`.
  **Installed section:** `Installed models (N)` + `Refresh`; either an empty
  hint or a `List` of monospaced tags each with a `Remove` button (gated on
  unlock). `Divider()`. **Add section:** collapsed `DisclosureGroup` "Add a
  model" → reveals the §A-MP picker + `Pull selected` (or, if locked, a
  caption that Add is disabled until unlock). Footer: bottom-left `Done`.
- **Windows counterpart:** Models tab re-entered + `UnlockDialog.xaml` +
  `RemoveModelDialog.xaml`.

### A-Unlock — Unlock sheet — `prep-10-unlock-sheet.png` *(optional capture)*
- A modal sheet (`minWidth 380`): title3 "Unlock encrypted SSD"; explanatory
  callout; `Passphrase` `SecureField`; red error text on failure; bottom-right
  `Cancel` · `Unlock` (default, disabled if empty/unlocking).
- **Intentionally identical to the Runner's unlock sheet (B-Unlock)** so users
  have one mental model. Mirror that 1:1 on Windows too
  (`UnlockDialog.xaml` ↔ `UnlockDriveDialog.xaml`).

### A10 — Done — `prep-11-done.png`
- **Step label:** `Done`
- **Contents:** H1 "Drive ready"; secondary instruction to open `Runner.app`
  on the SSD; if readiness items exist, a "Readiness" list of colored-dot +
  name + status word rows; bottom-right `Quit` (default, large).

### A11 — Failed — `prep-12-failed.png` *(optional capture)*
- **Step label:** `Failed`
- **Contents:** red status dot + title2 "Something went wrong"; the error
  message (secondary); "Recent log:" pane (last 50 lines, max height 220);
  bottom-right `Restart` (default).

---

## 4. App B — Mac Runner (`mac-runner`)

**Single screen, no state machine** — one scrolling `VStack`, `.padding(16)`,
`minWidth 720 × minHeight 640`, plus two modal sheets. The calm comes from
linear top-to-bottom order and conditional sections, not navigation.

**Flow:** launch → if no SSD chosen, auto `pickSsdRoot()`. Selecting an
encrypted-locked SSD sets status `Encrypted SSD locked` and auto-presents the
unlock sheet. Successful unlock → status `Unlocked`, chat host auto-spawns.
`Send` drives status `Sending…` → `Generating…` → `Answered` /
`Answered with sources` / `Chat failed: …`.

### B1 — Runner main — `runner-01-main.png`
- **Top:** title2 "Free AI SSD macOS Runner"; live `status` line.
- **Action row:** `Select SSD`; then **one** conditional button —
  encrypted+unlocked → `Lock`; plaintext+API-up → `Stop`; plaintext+API-down →
  `Start`. (MAC39: the verb reflects encryption state.)
- **Model `Picker`** (populated from on-disk SSD truth, not config-pinned).
- **Prompt** `TextEditor`, fixed height 120.
- **`Send`** button — inline spinner while sending; disabled if sending, no
  model, or empty prompt.
- **Response** `TextEditor`, fixed height 200 (streamed output).
- **Red banner** (`chatError`) for failures; **orange banner** (`ragWarning`)
  for answered-without-sources — distinct, one signal per outcome.
- **Sources** list ("Sources" headline + secondary lines) when RAG context
  used.
- `Divider()` → **`Expose API on LAN`** toggle + `networkApiStatus` caption.
- `Divider()` → **Documents section** (B2).
- **Windows counterpart:** `runner/MainWindow.xaml` (~945 lines, ~83KB
  code-behind). The Windows runner also carries voice/PTT/DCS UI that has no
  Mac equivalent (§6) — parity is about *calming the chat surface*, not
  removing those.

### B2 — Documents section — `runner-02-documents.png`
- "Documents" headline + `libraryStatus` caption.
- If the local sidecar isn't reachable yet (`networkApiBaseUrl == nil`): a
  single hint reflecting the real cause — "Unlock the SSD to manage documents."
  when locked, otherwise the current `networkApiStatus` (e.g. starting/crashed).
  Post-MAC34 the sidecar runs at unlock regardless of the Network Mode toggle,
  so this is not a "turn on Network Mode" prompt.
- If reachable: `Library` picker (None + libraries) + `Create`; a button row
  `Add Files` · `Add Folder` · `Sweep` · `Rebuild` · `Pull embedding model`
  (all gated on an active library except the last); then a `Files (N)` list,
  each row filename + borderless `Remove`.

### B-Create — Create library sheet — `runner-03-create-library.png` *(optional)*
- Modal (`minWidth 360`): title3 "New library"; rounded `Library name` field;
  `Cancel` · `Create` (default, disabled if blank).

### B-Unlock — Unlock sheet — `runner-04-unlock-sheet.png` *(optional)*
- Modal (`minWidth 360`): title3 "Unlock encrypted SSD"; callout; `Password`
  `SecureField`; red error text; `Cancel` · `Unlock` (default, disabled if
  empty). **Same shape as A-Unlock by design.**

---

## 5. Consolidated Mac → Windows mapping

| Mac screen | Windows file(s) today | Parity action |
|---|---|---|
| Prep Welcome (A1) | — (none) | **Add** an intro/orientation step |
| Prep Drive selection (A2) | `prep-app/MainWindow.xaml` Drives tab | Make it a discrete step; keep configured-banner |
| Prep Erase confirm (A3) | `EraseConfirmDialog`, `FixedDriveConfirmDialog` | Keep as unmistakable confirm dialog(s) |
| Prep Formatting/Staging/Readiness (A4/A5/A8) | progress area in `prep-app/MainWindow.xaml` | Single step + monospaced auto-scroll log |
| Prep Encryption (A6) | `EncryptionSetupDialog` + Models tab | Merge toggle + picker into one calm step |
| Prep Model picker (A-MP) | Models tab | Likely **level Windows up** (HF search, chips, quants) |
| Prep Model pull / paused / failed (A7/a/b) | Models tab progress | Progress line + log + non-fatal recovery copy |
| Prep Manage models (A9) | Models tab re-entry, `UnlockDialog`, `RemoveModelDialog` | Banners + installed list + collapsed Add |
| Prep/Runner Unlock (A-Unlock/B-Unlock) | `UnlockDialog`, `UnlockDriveDialog` | One identical unlock dialog, both apps |
| Prep Done (A10) | prep completion state | Dedicated success step + readiness list |
| Prep Failed (A11) | `ThemedMessageDialog` / inline | Dedicated failure step + recent-log pane |
| Runner main (B1) | `runner/MainWindow.xaml` chat region | Calm linear layout; one status signal per outcome |
| Runner Documents (B2) | runner library region | Network-gated section with one hint |
| Runner Create library (B-Create) | runner dialog | Small modal |

---

## 6. Windows-only features with NO Mac equivalent — do not delete

These exist only on the Windows runner and have **no Mac screen to match**.
Fold them into the calmer layout; do **not** remove them to "match Mac":

- **Voice I/O** (Whisper STT + Piper/system TTS).
- **HOTAS push-to-talk** incl. the floating `PttOverlayWindow.xaml`.
- **DCS binding import** incl. `ProfileSelectionDialog.xaml` and the aircraft
  import wizard.
- **Dependency install UI** (`DependencyInstallDialog.xaml`).

Open question for the human/owner: where do these live in a Mac-style flow
(e.g. a collapsed "Advanced" / "Flight sim" disclosure vs. a separate tab)?
Flag this; don't guess.

---

## 7. Screenshot capture manifest

Save each screenshot into `screenshots/` with the **exact filename** below
(callouts in this doc already point at these names — match them and the doc
renders complete with no rename step). Keep window chrome and any title/step
label in frame. PNG preferred.

**Prep (App A):**
1. `prep-01-welcome.png`
2. `prep-02-drive-selection.png` — also grab the configured-drive banner state if you can
3. `prep-03-erase-confirmation.png`
4. `prep-04-formatting.png` ⚠ canonical capture for the progress-log view (covers A5 & A8)
5. `prep-05-staging.png` *(optional — not captured; same view as #4)*
6. `prep-06-encryption-setup.png` — toggle OFF state
7. `prep-06b-model-picker.png` — expand a Hugging Face repo so quant children show
8. `prep-07-model-pull.png`
9. `prep-07a-pull-paused.png` *(optional — needs a cancel)*
10. `prep-07b-pull-failed.png` *(optional — needs a failure)*
11. `prep-08-readiness.png` *(optional — not captured; same view as #4)*
12. `prep-09-manage-models.png` — ideally both locked and unlocked banner states
13. `prep-10-unlock-sheet.png` *(optional)*
14. `prep-11-done.png`
15. `prep-12-failed.png` *(optional — needs a failure)*

**Runner (App B):**
16. `runner-01-main.png` — after unlock, with a model selected
17. `runner-02-documents.png` — Network Mode ON, a library with files
18. `runner-03-create-library.png` *(optional)*
19. `runner-04-unlock-sheet.png` *(optional)*

Optional/⚠ shots: skip if hard to trigger — the written spec above is
sufficient for the Windows agent; screenshots only calibrate visual feel.

---

## 8. Handing off to the Windows agent

Point the Windows agent at this folder and tell it:
1. Read §0 first — this is flow/density parity, not pixel-porting; the shared
   theme is locked pending owner approval.
2. Use §3–§4 as the screen spec, §5 to locate the WPF files, the matching
   `screenshots/*.png` for visual feel.
3. Treat §6 as a hard constraint — Windows-only features stay.
4. The biggest single win is restructuring the Windows prep tool from a
   two-tab everything-at-once window into the one-step-at-a-time flow in §3.
