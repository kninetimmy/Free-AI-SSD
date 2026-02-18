namespace FreeAiSsd.PrepApp;

public partial class EncryptionSetupDialog : System.Windows.Window
{
    public EncryptionSetupDialog()
    {
        InitializeComponent();
    }

    public string Password => PasswordBox.Password;

    private void Enable_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PasswordBox.Password))
        {
            System.Windows.MessageBox.Show(
                "Passphrase cannot be empty.",
                "Encryption setup",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (PasswordBox.Password.Length < 8)
        {
            System.Windows.MessageBox.Show(
                "Passphrase must be at least 8 characters.",
                "Encryption setup",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(PasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            System.Windows.MessageBox.Show(
                "Passphrases do not match.",
                "Encryption setup",
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
