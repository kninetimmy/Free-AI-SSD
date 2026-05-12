namespace FreeAiSsd.PrepApp;

/// <summary>
/// C7: passphrase prompt for unlocking an encrypted SSD's Manage-Models
/// session. Mirror of <c>UnlockDriveDialog</c> in the Runner, with one
/// addition: an inline error <c>TextBlock</c> so the caller can re-show
/// the dialog with the previous "Incorrect password" feedback inline
/// instead of a MessageBox interruption.
/// </summary>
public partial class UnlockDialog : System.Windows.Window
{
    public UnlockDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordBox.Focus();
    }

    public string Password => PasswordBox.Password;

    /// <summary>
    /// Pre-populates the error <c>TextBlock</c> with feedback from a
    /// prior failed attempt. Set before <c>ShowDialog</c>; empty hides it.
    /// </summary>
    public string InitialError
    {
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                ErrorText.Visibility = System.Windows.Visibility.Collapsed;
                ErrorText.Text = string.Empty;
            }
            else
            {
                ErrorText.Text = value;
                ErrorText.Visibility = System.Windows.Visibility.Visible;
            }
        }
    }

    private void Unlock_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PasswordBox.Password))
        {
            ErrorText.Text = "Passphrase cannot be empty.";
            ErrorText.Visibility = System.Windows.Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
