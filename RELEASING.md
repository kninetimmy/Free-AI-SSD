# Releasing Free-AI-SSD

## Versioning

We use [SemVer](https://semver.org/) with standard pre-release suffixes.

| Format | Meaning | Example | GitHub Release |
|---|---|---|---|
| `MAJOR.MINOR.PATCH` | Stable release | `0.2.0` | Latest |
| `MAJOR.MINOR.PATCH-alpha.N` | Early test build, may have bugs and breaking changes | `0.2.0-alpha.1` | Pre-release |
| `MAJOR.MINOR.PATCH-beta.N` | Feature-complete, looking for bugs | `0.2.0-beta.1` | Pre-release |
| `MAJOR.MINOR.PATCH-rc.N` | Release candidate, ships as-is unless a blocker found | `0.2.0-rc.1` | Pre-release |

The `N` is an iteration counter that resets per version: `0.2.0-alpha.1`, `0.2.0-alpha.2`, `0.2.0-beta.1`, etc.

## Typical lifecycle

1. Work in feature branches → PR to `main` → CI runs → merge.
2. When ready for testing, cut an alpha: `0.2.0-alpha.1`.
3. Iterate on alphas if needed: `0.2.0-alpha.2`, `0.2.0-alpha.3`.
4. When feature-complete, cut a beta: `0.2.0-beta.1`.
5. Iterate on betas as bugs are fixed.
6. (Optional) When confident, cut an rc: `0.2.0-rc.1`.
7. When happy, cut stable: `0.2.0`.

Most cycles can skip rc and go straight from beta to stable.

## How to cut a release

1. Make sure `main` is at the commit you want to release.
2. Go to **Actions → Build and Package → Run workflow**.
3. Enter the version (no leading `v` — the workflow adds it).
4. Toggle `Include macOS artifacts` if needed.
5. Click **Run workflow**.

The workflow will:
- Build Windows (and optionally macOS) artifacts.
- Create tag `v<version>`.
- Create a GitHub Release with the artifacts attached.
- Automatically mark it as pre-release if the version contains `-alpha`, `-beta`, or `-rc`.

## Hotfixes

Hotfixes to a stable release bump the PATCH: `0.2.0` → `0.2.1`. They follow the same flow.
