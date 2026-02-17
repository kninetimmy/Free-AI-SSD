namespace FreeAiSsd.PrepApp;

public partial class EraseConfirmDialog : System.Windows.Window
{
    public bool IsConfirmed { get; private set; }

    public EraseConfirmDialog(string driveRoot, string driveSizeDisplay)
    {
        InitializeComponent();
        WarningText.Text =
            "WARNING: Formatting will erase all data on the selected drive.\n\n" +
            $"Drive: {driveRoot}\n" +
            $"Size: {driveSizeDisplay}\n\n" +
            "This action cannot be undone.";
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
    }

    private void Proceed_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        IsConfirmed = string.Equals(ConfirmText.Text?.Trim(), "ERASE", StringComparison.Ordinal);
        DialogResult = IsConfirmed;
    }
}
