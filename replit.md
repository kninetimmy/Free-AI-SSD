# Free-AI-SSD

## Overview
Prepare a portable SSD with Ollama + LLMs once, then run them offline on Windows and/or macOS. This is a .NET 8 WPF desktop application suite consisting of a PrepApp (preparation tool) and Runner (end-user offline tool).

## Project Architecture
- **shared/**: Cross-platform shared library (`FreeAiSsd.Shared`) - builds on all platforms
- **prep-app/**: WPF GUI for preparing SSDs (`FreeAiSsd.PrepApp`) - Windows-only (WPF)
- **runner/**: WPF GUI for running models from SSD (`FreeAiSsd.Runner`) - Windows-only (WPF)
- **tests/**: xUnit test project (`FreeAiSsd.Tests`) - cross-platform
- **mac-runner/**: Swift-based macOS runner
- **docs/**: Documentation

## Build System
- .NET 8 SDK (dotnet-8.0)
- Solution file: `FreeAiSsd.sln`
- The WPF GUI projects (prep-app, runner) target `net8.0-windows` and cannot build on Linux
- The shared library and tests target `net8.0` and build cross-platform

## Development Workflow
- **Build & Test**: `dotnet build shared/FreeAiSsd.Shared.csproj && dotnet build tests/FreeAiSsd.Tests.csproj && dotnet test tests/FreeAiSsd.Tests.csproj --verbosity normal`
- 1 test (`IsPathUnderRoot_WindowsBoundaryIsRespected`) is expected to fail on Linux as it tests Windows-specific path behavior

## Key Dependencies
- xUnit 2.9.2 (testing)
- System.Management 8.0.0 (Windows system info)

## Shared Library Components (shared/)
| File | Purpose |
|------|---------|
| DependencyChecker.cs | Detects missing VC++ / .NET runtimes via registry + process checks |
| DownloadManager.cs | Resumable HTTP file downloads with progress callbacks |
| DriveInspector.cs | Enumerates candidate drives (removable + optionally fixed) |
| ModelSizing.cs | Maps model tags to RAM/VRAM/disk requirements for sizing warnings |
| NetUtils.cs | Port availability checking for Ollama port selection |
| OllamaPackageTrustPolicy.cs | URL allowlisting, SHA-256 digest verification, trust attestation |
| PathGuards.cs | Path traversal prevention (sibling boundary, case sensitivity) |
| PortableConfig.cs | JSON serialization for portable-config.json (models, port, timestamps) |
| PrepDriveWriteGuard.cs | Blocks PrepApp writes to encrypted drives (fail-closed model) |
| PrereqInstallValidator.cs | Validates bundled installer integrity (SHA-256) before execution |
| PrereqManifest.cs | Manifest of bundled prerequisite installers with hashes |
| PrereqCatalog.cs | Catalog of required Windows prerequisites (VC++, .NET) |
| MacToolCatalog.cs | macOS-specific tool URLs and paths |
| ProcessRunner.cs | Safe process spawning with argument lists (not string concatenation) |
| RunnerFirstRunState.cs | Persists first-run state (sizing warning dismissed, dependency prompt shown) |
| SsdEncryption.cs | AES-256-GCM config encryption with PBKDF2-SHA256 (210k iterations) |
| SsdLayout.cs | Canonical directory structure constants and creation |
| SsdLogger.cs | File-based logger writing to the SSD's logs directory |
| SystemCompatibility.cs | Detects GPU, CPU architecture, OS version for compatibility display |
| SystemResources.cs | WMI-based RAM and VRAM detection |

## Code Review Findings (2026-02-19)

### Architecture
- **Major**: Both MainWindow.xaml.cs files (PrepApp ~1800 lines, Runner ~600 lines) are monolithic code-behind files mixing UI state, I/O, downloads, encryption, and business logic. Recommend refactoring to MVVM pattern with a service layer for testability and maintainability.
- **Good**: Clean separation of shared library from GUI concerns. Individual shared components are focused and well-bounded.

### Security Assessment
- **Strong**: AES-256-GCM encryption with PBKDF2-SHA256 (210,000 iterations) is solid and up to industry standards.
- **Strong**: Ollama package trust policy with URL allowlisting, SHA-256 digest verification, and execution attestation prevents supply-chain attacks.
- **Strong**: PathGuards prevents path traversal with proper sibling boundary detection and platform-aware case sensitivity.
- **Strong**: PrepDriveWriteGuard uses a "fail closed" security model — any ambiguity in encryption state blocks writes.
- **Good**: ProcessRunner uses ArgumentList (not string concatenation) to prevent shell injection.
- **No critical vulnerabilities found.**

### Code Quality
- **Major**: Frequent silent exception swallowing in SystemResources.cs, PrereqManifest.Load(), and RunnerFirstRunState.Load() masks failures and makes diagnostics unreliable. Recommend logging caught exceptions before returning defaults.
- **Good**: Consistent use of records and immutable data structures throughout the shared library.
- **Good**: Async/await used correctly throughout with proper CancellationToken propagation.

### Testability
- **Major**: Tests only cover low-level shared utilities. High-risk workflows (downloads, encryption enable/disable, dependency installation, Runner start/stop) are untestable due to tight UI coupling and lack of injectable abstractions.
- **Recommendation**: Extract service interfaces (IDownloadService, IOllamaService, IDependencyChecker) and use dependency injection to enable unit testing of business logic without WPF dependencies.

### Test Coverage
- 42 tests total (41 pass on Linux, 1 Windows-specific path test expected to fail)
- Well-covered: SsdEncryption (12 tests), OllamaPackageTrustPolicy (9 tests), PrepDriveWriteGuard (7 tests)
- Covered: ModelOperations (5 tests), PathGuards (3 tests), PrereqInstallValidator (1 test)
- Not covered: DownloadManager, DriveInspector, SsdLayout, SystemCompatibility, PortableConfig, all UI workflows

### Improvement Recommendations (Priority Order)
1. **MVVM Refactoring**: Extract MainWindow logic into ViewModels + Services
2. **Error Handling**: Replace silent catch blocks with logged exceptions
3. **Service Abstractions**: Create interfaces for I/O, network, and process operations
4. **Test Expansion**: Add integration tests for download, encryption, and model workflows
5. **Configuration Validation**: Add schema validation for portable-config.json

## Code Comments
- All source files have been documented with comprehensive XML documentation comments
- Documented: 20 shared library files, 5 prep-app files, runner MainWindow, 6 test files
- Comments explain purpose, parameters, security considerations, and architectural context

## Recent Changes
- 2026-02-19: Initial Replit setup with .NET 8, build+test workflow configured
- 2026-02-19: Comprehensive code review completed with architecture, security, and quality findings
- 2026-02-19: Added XML documentation comments to all source files (shared, prep-app, runner, tests)
