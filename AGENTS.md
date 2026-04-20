# Free-AI-SSD

Cross-platform offline AI assistant that runs entirely from an
encrypted external SSD. Windows WPF prep tool stages the drive;
Windows WPF runner (and beta macOS Swift app) provides RAG chat,
voice I/O, HOTAS PTT, DCS binding import, and a LAN API for a
lightweight companion app on a second PC.

## Session continuity

At the start of every session, read `agent_docs/project_state.md` --
it is the dashboard. Load on demand when the task calls for it:
- `agent_docs/project_arch.md` -- architecture, stack, layout. The
  source of truth for how the project is built.
- `agent_docs/project_decisions.md` -- locked-in decisions,
  append-only.
- `agent_docs/project_backlog.md` -- planned work.

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

## Project-specific instructions

**Security controls are non-negotiable** -- don't weaken them. See
`project_arch.md` -> "Security invariants" for the full list. In
short: AES-256-GCM for config, SHA-256 + URL allowlist for
downloaded binaries, `PathGuards` for path handling, and
`ProcessRunner.ArgumentList` for all process launches (never string
concat).

**GitHub workflow:** never push directly to `main`. Create a PR,
watch CI, report results, and wait for explicit confirmation before
merging. On CI failure, investigate and push a fix to the same
branch.

**Known-supported formats:** PDF, TXT, Markdown only. DOCX is out
of scope. DCS bindings only -- no IL-2 / War Thunder parsers.
