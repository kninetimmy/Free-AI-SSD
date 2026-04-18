namespace FreeAiSsd.PrepApp;

public partial class FixedDriveConfirmDialog : System.Windows.Window
{
    public FixedDriveConfirmDialog(string driveRoot)
    {
        InitializeComponent();
        WarningText.Text =
            $"You selected a fixed (non-removable) drive: {driveRoot}\n\n" +
            "Proceeding may overwrite or modify existing files on this drive. " +
            "Only continue if you are certain this is the correct target.";
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
        => DialogResult = false;

    private void Proceed_Click(object sender, System.Windows.RoutedEventArgs e)
        => DialogResult = true;
}
