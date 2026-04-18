using System.Windows;
using FreeAiSsd.Shared.Models;
using FreeAiSsd.Shared.Services;

namespace FreeAiSsd.PrepApp.Services;

public sealed class DialogService : IDialogService
{
    private readonly Func<Window> _ownerProvider;

    public DialogService(Func<Window> ownerProvider)
    {
        _ownerProvider = ownerProvider;
    }

    public void ShowInfo(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowWarning(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void ShowError(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public bool ConfirmFixedDrive(string driveRoot)
    {
        var dialog = new FixedDriveConfirmDialog(driveRoot) { Owner = _ownerProvider() };
        return dialog.ShowDialog() == true;
    }

    public bool ConfirmSizingWarnings(IReadOnlyList<string> warnings)
    {
        var message = "Some selected models may run poorly on this machine or exceed available resources."
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, warnings.Select(w => $"- {w}"))
            + Environment.NewLine + Environment.NewLine
            + "Continue anyway?";

        return MessageBox.Show(
            message,
            "Model sizing warnings",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmErase(string driveRoot, string sizeDisplay)
    {
        var dialog = new EraseConfirmDialog(driveRoot, sizeDisplay) { Owner = _ownerProvider() };
        return dialog.ShowDialog() == true && dialog.IsConfirmed;
    }

    public string? PromptForEncryptionPassword()
    {
        var dialog = new EncryptionSetupDialog { Owner = _ownerProvider() };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    public ModelRemoveChoice PromptRemoveModel(string modelName)
    {
        var dialog = new RemoveModelDialog(modelName) { Owner = _ownerProvider() };
        var result = dialog.ShowDialog();
        if (result != true) return ModelRemoveChoice.Cancel;
        return dialog.Choice;
    }

    public bool ConfirmPrereqRefresh()
    {
        return MessageBox.Show(
            "Prerequisite bundle is missing or inconsistent. Re-download prerequisites now (online) and rewrite manifest?",
            "Prerequisite bundle verification",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes) == MessageBoxResult.Yes;
    }
}
