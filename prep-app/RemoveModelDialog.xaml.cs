using FreeAiSsd.Shared.Models;

namespace FreeAiSsd.PrepApp;

public partial class RemoveModelDialog : System.Windows.Window
{
    public ModelRemoveChoice Choice { get; private set; } = ModelRemoveChoice.Cancel;

    public RemoveModelDialog(string modelName)
    {
        InitializeComponent();
        PromptText.Text = $"Choose how to remove '{modelName}'.";
    }

    private void RemoveConfigOnly_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Choice = ModelRemoveChoice.ConfigOnly;
        DialogResult = true;
    }

    private void DeleteFromDisk_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Choice = ModelRemoveChoice.DeleteFromDisk;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Choice = ModelRemoveChoice.Cancel;
        DialogResult = false;
    }
}
