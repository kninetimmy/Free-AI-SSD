namespace FreeAiSsd.Runner;

public partial class UnlockDriveDialog : System.Windows.Window
{
    public UnlockDriveDialog()
    {
        InitializeComponent();
    }

    public string Password => PasswordBox.Password;

    private void Unlock_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PasswordBox.Password))
        {
            System.Windows.MessageBox.Show(
                "Passphrase cannot be empty.",
                "Unlock drive",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
