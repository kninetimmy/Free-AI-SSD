using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.MacPrepHost.Services;

/// <summary>
/// IDialogService stub for the headless Mac prep sidecar. Mac UI
/// confirmations live in Swift (NSAlert / SwiftUI sheets) and the
/// Swift parent only invokes a sidecar command after the user has
/// already confirmed; the sidecar therefore never needs to prompt.
///
/// Mirrors the MAC6 NoOpConfigStore pattern: a stub that satisfies the
/// dependency contract without crossing the language boundary for
/// user-facing confirmations.
///
/// Behavioral choices:
///   - ShowInfo/Warning/Error → silent. Errors that need user attention
///     should bubble out as exceptions or stderr lines, not modal alerts
///     in a headless process.
///   - Confirm-style methods → return false (refuse). This is a
///     defense-in-depth posture: if a prep-core call path on Mac
///     somehow reached a confirmation prompt, refusing is safer than
///     silently approving a mutation. None of the MAC17 MVP command
///     paths actually hit these — Swift confirms before invoking.
///   - PromptForEncryptionPassword → null. Encryption is Swift-owned.
///   - PromptRemoveModel → Cancel. Same defense-in-depth.
/// </summary>
internal sealed class NoOpDialogService : IDialogService
{
    public void ShowInfo(string message, string title) { }
    public void ShowWarning(string message, string title) { }
    public void ShowError(string message, string title) { }

    public bool Confirm(string message, string title) => false;
    public bool ConfirmFixedDrive(string driveRoot) => false;
    public bool ConfirmSizingWarnings(IReadOnlyList<string> warnings) => false;
    public bool ConfirmErase(string driveRoot, string sizeDisplay, string fileSystem) => false;
    public bool ConfirmPrereqRefresh() => false;

    public string? PromptForEncryptionPassword() => null;
    public ModelRemoveChoice PromptRemoveModel(string modelName) => ModelRemoveChoice.Cancel;
}
