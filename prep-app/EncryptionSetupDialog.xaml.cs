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
            ThemedMessageDialog.ShowWarning("Passphrase cannot be empty.", "Encryption setup", this);
            return;
        }

        if (PasswordBox.Password.Length < 8)
        {
            ThemedMessageDialog.ShowWarning("Passphrase must be at least 8 characters.", "Encryption setup", this);
            return;
        }

        if (!string.Equals(PasswordBox.Password, ConfirmPasswordBox.Password, StringComparison.Ordinal))
        {
            ThemedMessageDialog.ShowWarning("Passphrases do not match.", "Encryption setup", this);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
