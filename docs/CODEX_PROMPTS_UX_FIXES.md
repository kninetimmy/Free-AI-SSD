# Codex Prompt Pack: Runner + Prep UX Fixes

Use these prompts in **separate Codex sessions** so each effort stays focused and reviewable.

---

## Prompt 1 — Runner app: fix document library creation/selection bug

You are working in the `Free-AI-SSD` repository.

### Problem report
In the Runner app document ingest area:
- Clicking **Add files** can show “Select a document library first.”
- User clicks **Create library** (default name shown as “My library”).
- App logs “Created library: My library”, but the library dropdown still shows no selectable item.
- On disk, a folder appears under `docs/libraries` with an auto-generated ID-like name (example: `lib-...`) that does not match the display name.
- Result: user is blocked and cannot ingest docs.

### Goal
Make library creation and selection work end-to-end in one obvious flow:
1. User enters or accepts a library name.
2. User clicks **Create library**.
3. Newly created library appears in the dropdown immediately.
4. Newly created library is auto-selected.
5. **Add files/Add folder** become usable immediately.

### Requirements
1. **Fix data/model mismatch** between display name and internal folder/ID so dropdown binding always uses the authoritative library list from `DocumentLibraryManager` (or equivalent service).
2. After creating a library:
   - refresh list,
   - select created library by stable key,
   - update status text with a success message that includes both display name and resolved folder path.
3. Add guardrails:
   - prevent duplicate names (case-insensitive) with clear message,
   - trim whitespace and reject empty names,
   - sanitize/validate names with a user-friendly error if invalid.
4. Improve diagnostics:
   - log create/select sequence with enough detail to debug state mismatches.
5. Add or update tests (unit/integration) for:
   - create + immediate select,
   - duplicate name rejection,
   - dropdown list refresh after create.

### UX copy updates
- Replace vague errors with actionable text, e.g.:
  - “Create or select a library to continue.”
  - “Library created and selected: <name>.”

### Deliverables
- Code changes.
- Tests.
- Short changelog summary in PR body.
- If UI changed, include before/after screenshot(s).

### Constraints
- Keep existing architecture (MVVM/service boundaries).
- Do not break existing libraries on disk.
- Preserve backward compatibility for previously created `lib-*` folders.

---

## Prompt 2 — Runner app: add explicit step-by-step onboarding for document ingest

You are working in the `Free-AI-SSD` repository.

### Problem report
New/non-technical users do not know the correct order of actions in Runner’s **Reference Documents** section. Buttons are visible but workflow is not explicit.

### Goal
Create a beginner-friendly guided flow in the UI that makes the ingest sequence unmistakable.

### Required UX behavior
Implement visible, plain-language guidance near the ingest controls:
1. **Step 1:** Create or select a library.
2. **Step 2:** Add files/folders.
3. **Step 3:** Build/Rebuild index.
4. **Step 4:** Ask a question that uses indexed docs.

### Implementation expectations
1. Add a “Getting started” panel (or equivalent inline instruction block) in Runner with numbered steps.
2. Add tooltips for all ingest controls:
   - Library dropdown
   - Create library
   - Add files
   - Add folder
   - Sweep folders now
   - Rebuild index
   - Pull embedding model
3. Add state-based enable/disable + helper text:
   - If no library selected, disable file ingest/index controls and show exact reason.
   - If library has no files, disable rebuild and explain why.
4. Add success-progress language in status/log area after each step.
5. Add automated tests for view-model states that enforce guided progression.

### Accessibility + clarity
- Use plain language for non-technical users.
- Avoid jargon like “embedding” without explanation.
- Ensure tooltip text is concise and task-oriented.

### Deliverables
- Code changes.
- UX text changes.
- Tests for workflow state transitions.
- Screenshot(s) of updated Runner ingest section.

---

## Prompt 3 — Prep app: clarify “Pull/Install” vs “Pull Selected” and enforce guided workflow

You are working in the `Free-AI-SSD` repository.

### Problem report
Prep app has confusing action buttons and unclear order of operations, especially:
- Difference between **Pull/Install** and **Pull Selected** is not obvious.
- Users cannot tell which steps should be done first.

### Goal
Redesign labels, helper text, and flow logic so users always understand what each action does and when to use it.

### Requirements
1. Audit Prep app action buttons and define clear intent for each.
2. Replace ambiguous labels with user-centered names. Example direction (adjust to actual behavior):
   - “Install recommended models” (bulk/default path)
   - “Install checked models” (manual path)
3. Add inline explanation under/near buttons describing scope and consequences.
4. Add a numbered “Prep checklist” section that clearly sequences tasks, e.g.:
   - Step 1: Choose target drive
   - Step 2: Install prerequisites
   - Step 3: Install models
   - Step 4: Verify readiness
5. Use progressive disclosure:
   - Disable later-step actions until prerequisites are complete,
   - Explain disabled state in plain language.
6. Add tooltips/help icons for core controls and any destructive actions.
7. Add or update tests for:
   - button enabled states by readiness state,
   - correct action routing for renamed buttons,
   - checklist state transitions.

### Deliverables
- Code + copy updates in Prep app.
- Tests.
- Screenshot(s) showing new labels and checklist.
- PR summary including old vs new button semantics.

### Non-goals
- Do not remove advanced functionality; only make it understandable and safer for novice users.
- Do not add hidden side effects to button actions.

---

## Optional master prompt (single larger effort)

If you prefer one combined Codex task instead of 3 smaller tasks:

> Implement a UX and workflow clarity overhaul across Runner (document ingest) and Prep app (model install flow). Fix the Runner library creation/selection bug, add explicit numbered onboarding steps, add tooltips and state-based guardrails, clarify ambiguous Prep button labels (“Pull/Install” vs “Pull Selected”), and enforce progressive workflow enablement. Add/adjust tests for state transitions and action routing. Preserve backward compatibility for existing on-disk library folders. Include screenshots and a concise PR summary describing user impact.
