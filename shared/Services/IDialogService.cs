using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.Shared.Services;

public interface IDialogService
{
    void ShowInfo(string message, string title);
    void ShowWarning(string message, string title);
    void ShowError(string message, string title);
    bool Confirm(string message, string title);
    bool ConfirmFixedDrive(string driveRoot);
    bool ConfirmSizingWarnings(IReadOnlyList<string> warnings);
    bool ConfirmErase(string driveRoot, string sizeDisplay);
    string? PromptForEncryptionPassword();
    ModelRemoveChoice PromptRemoveModel(string modelName);
    bool ConfirmPrereqRefresh();
}
