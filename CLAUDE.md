# Free-AI-SSD

Cross-platform offline AI assistant that runs entirely from an
encrypted external SSD. Windows WPF prep tool stages the drive;
Windows WPF runner (and beta macOS Swift app) provides RAG chat,
voice I/O, HOTAS PTT, DCS binding import, and a LAN API for a
lightweight companion app on a second PC.

## Session continuity

`.memhub/project.sqlite` is the source of truth. Read
`.memhub/rendered/PROJECT.md` at session start — it's the rendered
dashboard. `.memhub/rendered/PROJECT_LEDGER.md` is the rendered
append-only log. Both are generated from the sqlite store; never
hand-edit them. To change content, use the `memhub` CLI and re-run
`memhub render`.

### Recording work in memhub

Both agents (Claude, Codex) follow the same type convention so
the ledger stays clean:

- **task** — every shippable piece of work. Create with status
  `open` when starting; transition to `done` at wrap-up with the
  implementation summary in notes. This is the changelog.
- **decision** — durable rules that constrain future work
  ("don't add X to the lock path", "always use Y for Z"). A
  decision is something a future agent must not silently
  violate. Created only when the work produces such a rule.
- **fact** — current state observations (build/test status,
  active drive, etc.).

A shipped feature often produces both: a done task documenting
what landed, plus zero or more decisions documenting durable
rules that emerged. If you can't name a rule a future agent
would violate without it, it's not a decision — it's a done
task. Past entries are not retroactively migrated.

## Build / test / run

```powershell
# Build entire solution
dotnet build FreeAiSsd.sln -c Release

# Run all tests
dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal

# Run a single test class
dotnet test tests/FreeAiSsd.Tests.csproj --filter "FullyQualifiedName~DcsBindingParserTests"

# Full release build + publish (Runner, PrepApp, Companion, prereq bundle)
./build.ps1 -Configuration Release -Runtime win-x64

# Build shared only (cross-platform, no WPF)
dotnet build shared/FreeAiSsd.Shared.csproj
```

SDK version is pinned in `global.json`.

## Project-specific Claude instructions

**Security controls are non-negotiable** — don't weaken them. AES-256-GCM
for config, SHA-256 + URL allowlist for downloaded binaries,
`PathGuards` for path handling, and `ProcessRunner.ArgumentList`
for all process launches (never string concat). Full list in
`.memhub/rendered/PROJECT.md` → Architecture → "Security invariants".

**GitHub workflow:** never push directly to `main`. Create a PR,
watch CI, report results, and wait for explicit confirmation before
merging. On CI failure, investigate and push a fix to the same
branch.

**Known-supported formats:** PDF, TXT, Markdown only. DOCX is out
of scope. DCS bindings only — no IL-2 / War Thunder parsers.

## Releasing

This project uses semver with `-alpha.N` / `-beta.N` / `-rc.N` pre-release suffixes. See `RELEASING.md` for the full convention.

When the user asks to "cut a release", "ship an alpha/beta/rc/stable", "release version X", or similar:

1. **Determine the version.** If the user didn't specify, ask which stage they want:
   - alpha (early test build)
   - beta (feature-complete test build)
   - rc (release candidate)
   - stable (no suffix)

2. **Determine the version number.** Check the latest tag with `git tag --sort=-v:refname | head -10` to see what came before. Suggest the next logical version:
   - Bumping iteration within the same stage: `0.2.0-beta.1` → `0.2.0-beta.2`
   - Promoting stage: `0.2.0-beta.3` → `0.2.0-rc.1` or `0.2.0`
   - New version cycle: `0.2.0` → `0.3.0-alpha.1` (next minor) or `0.2.1` (patch)

3. **Confirm the version with the user** before doing anything destructive. Show them: "I'll cut `v0.3.0-beta.1` from the current main (`<short-sha>`). Confirm?"

4. **Cut the release.** Do NOT push tags directly. The release workflow creates the tag itself. Instead, trigger the workflow:

   ```
   gh workflow run build.yml -f version=<VERSION_WITHOUT_V_PREFIX> -f include_macos=<true|false>
   ```

   Default `include_macos` to `true` unless the user said Windows-only.

5. **Watch the run.** Use `gh run watch` or `gh run list --workflow=build.yml --limit 1` to confirm it started cleanly. Report the run URL back to the user.

6. **Do not** edit `RELEASING.md` or this section without explicit user request — it's the source of truth for the convention.

### Pre-flight checks before cutting a release

Before triggering the workflow, verify:
- Working tree is clean: `git status --porcelain` returns nothing.
- On `main`: `git rev-parse --abbrev-ref HEAD` returns `main`.
- Local main is up to date with origin: `git fetch && git status` shows no divergence.
- All PRs intended for this release are merged: ask the user to confirm if uncertain.

If any check fails, stop and report to the user. Do not auto-fix; let them decide.
