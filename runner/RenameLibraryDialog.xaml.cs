namespace FreeAiSsd.Runner;

public partial class RenameLibraryDialog : System.Windows.Window
{
    public RenameLibraryDialog(string currentName)
    {
        InitializeComponent();
        NameBox.Text = currentName ?? string.Empty;
    }

    public string NewName => NameBox.Text.Trim();

    private void Window_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        NameBox.SelectAll();
        NameBox.Focus();
    }

    private void Ok_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text))
        {
            System.Windows.MessageBox.Show(
                "Library name cannot be empty.",
                "Rename library",
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
