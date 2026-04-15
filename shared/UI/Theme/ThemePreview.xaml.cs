using System.Windows;

namespace FreeAiSsd.Shared.UI.Theme;

/// <summary>
/// Scratch window for visually verifying neumorphic theme tokens. Not
/// wired into either app's menu — instantiate manually from a debug
/// build (e.g. <c>new ThemePreview().Show();</c>) when tweaking
/// Colors.xaml / Shadows.xaml / Typography.xaml.
/// </summary>
public partial class ThemePreview : Window
{
    public ThemePreview()
    {
        InitializeComponent();
    }
}
