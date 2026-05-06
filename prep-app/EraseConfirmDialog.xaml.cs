namespace FreeAiSsd.PrepApp;

public partial class EraseConfirmDialog : System.Windows.Window
{
    public bool IsConfirmed { get; private set; }

    public EraseConfirmDialog(string driveRoot, string driveSizeDisplay, string fileSystem)
    {
        InitializeComponent();
        var fsLine = fileSystem switch
        {
            "exFAT" => "Format as: exFAT (Windows + macOS compatible)",
            "NTFS" => "Format as: NTFS (Windows only)",
            _ => $"Format as: {fileSystem}",
        };
        WarningText.Text =
            "WARNING: Formatting will erase all data on the selected drive.\n\n" +
            $"Drive: {driveRoot}\n" +
            $"Size: {driveSizeDisplay}\n" +
            $"{fsLine}\n\n" +
            "This action cannot be undone.";
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        IsConfirmed = false;
        DialogResult = false;
    }

    private void Proceed_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        IsConfirmed = true;
        DialogResult = true;
    }
}
