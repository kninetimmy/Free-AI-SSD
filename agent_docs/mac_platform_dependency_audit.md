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

## MAC5 native-encryption waiver (2026-05-05)

The "Keep the Swift macOS app thin" guideline in step 6 above is **explicitly
waived** for the encrypted-config unlock/save format. MAC5 reimplements
PBKDF2-HMAC-SHA256 + AES-256-GCM and the two-file atomic commit protocol
natively in Swift (`mac-runner/Sources/SsdEncryption.swift`) using only
`CryptoKit`, `CommonCrypto`, and `Foundation`.

Rationale: hosting a .NET 8 console process on Apple Silicon to do nothing but
read and re-emit a JSON config blob would drag a cross-architecture runtime
into the Mac launch path for a small, stable, security-critical surface that
Apple already provides natively. The duplication is bounded by a cross-language
fixture under `tests/Fixtures/MacEncryptedConfig/csharp-encrypted/` that both
sides round-trip in CI; if the C# format ever changes silently, the Windows
build's `MacEncryptedConfigCrossLanguageTests` and the Mac build's Swift test
binary both fail until the fixture is regenerated alongside a dated decision.

This waiver applies to MAC5's surface only. RAG, network API hosting, and
document management (MAC6/MAC7/MAC8) keep the original "thin Swift over
shared/core" rule unless a future decision records its own waiver.

## MAC6 Mac net8.0 sidecar exception (2026-05-06)

The "Keep the Swift macOS app thin" guideline in step 6 above is **also
explicitly relaxed** for the Mac LAN API surface: a new
`mac-runner-host/FreeAiSsd.MacRunnerHost.csproj` is allowed to ship as a
plain `net8.0` console exe, self-contained for `osx-arm64`, that depends on
`runner-core/`, `shared/`, and ASP.NET Core minimal-APIs. The Swift
`mac-runner` spawns this binary as a sidecar when Network Mode is on.

This is the explicit exit ramp MAC5 left open. Rationale: the chat / RAG /
API surface (`RunnerLocalApiService`, `ChatService`, `ModelManagementService`,
multipart audio, NDJSON streaming) is large, evolving, and security-
relevant. Reimplementing it in Swift would either fork the in-flight MAC7
(RAG parity) and MAC8 (document management) work twice, or block both items
behind a Swift catch-up project. We accept the ~70 MB self-contained
osx-arm64 .NET runtime cost on the SSD payload as a rounding error against
multi-GB model files.

**Boundary preserved.** Encrypted-config IO stays Swift-authoritative
(MAC5 is not reverted). The Swift app holds the unlocked PortableConfig in
memory and hands the dictionary to the sidecar via **stdin only** — the
plaintext-config invariant from MAC5 is unchanged. The sidecar never
writes the plaintext to disk; on Lock / app background / app terminate /
explicit shutdown, the sidecar exits before the Swift app zeroes the
unlock material.

**Code-reuse, encoded.** `mac-runner-host` references `runner-core`
directly and reuses `RunnerLocalApiService` byte-for-byte. There is no
fork of the API service. `MacPlatformBoundaryTests.MacRunnerHost_RemainsPlainNet8WithoutWindowsPackages`
guards the project shape (no Windows-only packages, no
`net8.0-windows`, no `EnableWindowsTargeting`). This waiver applies to
the network-API surface only; future Mac surfaces still default to the
"thin Swift over shared/core" rule unless a separate decision records a
new waiver.
