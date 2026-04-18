using System.Windows;
using System.Windows.Media;

namespace FreeAiSsd.PrepApp;

public enum MessageKind { Info, Warning, Error, Question }

public partial class ThemedMessageDialog : Window
{
    private ThemedMessageDialog(string message, string title, MessageKind kind, Window? owner)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;

        string glyph;
        string brushKey;

        switch (kind)
        {
            case MessageKind.Warning:
                glyph = "⚠";
                brushKey = "StatusWarningBrush";
                break;
            case MessageKind.Error:
                glyph = "✖";
                brushKey = "StatusDangerBrush";
                break;
            case MessageKind.Question:
                glyph = "?";
                brushKey = "AccentCyanBrush";
                OkYesButton.Content = "Yes";
                NoButton.Visibility = Visibility.Visible;
                break;
            default: // Info
                glyph = "ℹ";
                brushKey = "StatusInfoBrush";
                break;
        }

        IconGlyph.Text = glyph;
        IconGlyph.Foreground = TryFindResource(brushKey) as Brush ?? Brushes.Gray;

        if (owner != null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    public static void ShowInfo(string message, string title, Window? owner = null)
        => new ThemedMessageDialog(message, title, MessageKind.Info, owner).ShowDialog();

    public static void ShowWarning(string message, string title, Window? owner = null)
        => new ThemedMessageDialog(message, title, MessageKind.Warning, owner).ShowDialog();

    public static void ShowError(string message, string title, Window? owner = null)
        => new ThemedMessageDialog(message, title, MessageKind.Error, owner).ShowDialog();

    public static bool Confirm(string message, string title, Window? owner = null)
        => new ThemedMessageDialog(message, title, MessageKind.Question, owner).ShowDialog() == true;

    private void OkYes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
