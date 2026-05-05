# macOS Platform Dependency Audit

Date: 2026-05-05
Backlog item: MAC2

## Summary

The current portable boundary is not clean enough to start Mac runtime parity
work directly. `shared/FreeAiSsd.Shared.csproj` targets plain `net8.0`, but it
still carries Windows-oriented package references and implementations. MAC2
does not move those implementations; it records the split plan and adds test
guardrails so the debt cannot grow while MAC3+ extracts the reusable Runner
core.

## Current blockers

- `shared/FreeAiSsd.Shared.csproj` references `System.Management`, `NAudio`,
  and `SharpDX.DirectInput`.
- `shared/Client/AudioCaptureService.cs` uses NAudio microphone capture.
- `shared/Client/PttSounds.cs` uses NAudio playback for PTT cues.
- `shared/Client/HotasInputService.cs` uses SharpDX DirectInput.
- `shared/SystemCompatibility.cs` and `shared/SystemResources.cs` use WMI via
  `System.Management`.
- `shared/Services/DriveFormatCommand.cs` builds a Windows PowerShell
  `Format-Volume` command and should stay Windows-prep-only.
- `runner/`, `prep-app/`, and `companion/` are intentionally WPF
  `net8.0-windows` hosts.
- `runner/Services/SystemTextToSpeechService.cs` uses Windows SAPI
  (`System.Speech`), and `runner/Services/PiperTextToSpeechService.cs` uses
  NAudio playback.
- `runner-cli/` is already the right shape: a portable `net8.0` HTTP client,
  not an in-process host.

## Split plan

1. Create a platform-neutral Runner core in MAC3 for chat, RAG, document
   operations, model metadata, config access, and API endpoint logic. It must
   target plain `net8.0` and avoid WPF, Windows Forms, DirectInput, WMI,
   NAudio, System.Speech, PowerShell, and Windows-only path assumptions.
2. Keep WPF UI, Windows SAPI, DirectInput, NAudio playback/capture, UAC, and
   `Format-Volume` in Windows host or adapter projects. Windows behavior should
   remain unchanged while call sites move behind interfaces.
3. Move `shared/Client/*` audio/PTT implementations behind host-provided
   adapters before MAC12/MAC13. Cross-platform interfaces can remain shared;
   concrete NAudio/DirectInput implementations should not.
4. Move WMI system probes behind an environment/system-info abstraction.
   Non-Windows adapters should return useful Mac diagnostics without depending
   on `System.Management`.
5. Treat drive formatting and elevation as PrepApp Windows adapter behavior.
   Mac shared-SSD support should focus on filesystem validation and guidance,
   not invoking Windows formatting logic.
6. Keep the Swift macOS app thin. It should call the shared/local host boundary
   proven by MAC3-MAC7 rather than reimplementing encryption, RAG, or API
   behavior in Swift.

## Guardrails added

`tests/MacPlatformBoundaryTests.cs` now checks that:

- `FreeAiSsd.Shared` remains a plain `Microsoft.NET.Sdk` `net8.0` project, not
  a Windows-targeted or WPF project.
- The current Windows-only shared package references are explicit known debt
  and no additional blocked Windows-only package is added there.
- `runner-cli` remains a portable `net8.0` HTTP client without audio, DirectX,
  SAPI, or direct Runner project coupling.

When MAC3+ pays down the known shared-package debt, update the guardrail tests
in the same PR that removes the dependencies.
