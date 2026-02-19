# Free-AI-SSD

## Overview
Prepare a portable SSD with Ollama + LLMs once, then run them offline on Windows and/or macOS. This is a .NET 8 WPF desktop application suite.

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

## Recent Changes
- 2026-02-19: Initial Replit setup with .NET 8, build+test workflow configured
