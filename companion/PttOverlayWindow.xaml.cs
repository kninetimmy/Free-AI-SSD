using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace FreeAiSsd.Companion;

/// <summary>
/// The current state shown by the Companion PTT overlay. Mirrors the
/// Runner-side <c>PttState</c> but lives in the Companion namespace so
/// the tray app doesn't take a dependency on Runner internals.
/// </summary>
public enum CompanionPttState
{
    Idle,
    Listening,
    Thinking,
    Speaking
}

/// <summary>
/// Tiny always-on-top overlay showing the current PTT pipeline state.
/// Ported from runner/PttOverlayWindow.xaml.cs so VR users running the
/// Companion tray app get the same visual cue Runner provides.
/// </summary>
public partial class PttOverlayWindow : Window
{
    private static readonly SolidColorBrush GrayBrush = new(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly SolidColorBrush RedBrush = new(System.Windows.Media.Color.FromRgb(0xE0, 0x40, 0x40));
    private static readonly SolidColorBrush YellowBrush = new(System.Windows.Media.Color.FromRgb(0xE0, 0xC0, 0x30));
    private static readonly SolidColorBrush GreenBrush = new(System.Windows.Media.Color.FromRgb(0x40, 0xC0, 0x60));

    private bool _isDragging;
    private System.Windows.Point _dragStart;

    /// <summary>Raised when the user drags the window to a new position.</summary>
    public event Action<double, double>? PositionChanged;

    public PttOverlayWindow()
    {
        InitializeComponent();

        GrayBrush.Freeze();
        RedBrush.Freeze();
        YellowBrush.Freeze();
        GreenBrush.Freeze();
    }

    /// <summary>Sets the initial window position from saved config values.</summary>
    public void SetPosition(double x, double y)
    {
        Left = x;
        Top = y;
    }

    /// <summary>
    /// Updates the overlay to reflect the current pipeline state.
    /// Safe to call from any thread — marshals to the UI thread.
    /// </summary>
    public void UpdateState(CompanionPttState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateState(state));
            return;
        }

        switch (state)
        {
            case CompanionPttState.Idle:
                StatusDot.Fill = GrayBrush;
                StatusLabel.Text = "Ready";
                break;
            case CompanionPttState.Listening:
                StatusDot.Fill = RedBrush;
                StatusLabel.Text = "Listening";
                break;
            case CompanionPttState.Thinking:
                StatusDot.Fill = YellowBrush;
                StatusLabel.Text = "Thinking";
                break;
            case CompanionPttState.Speaking:
                StatusDot.Fill = GreenBrush;
                StatusLabel.Text = "Speaking";
                break;
        }
    }

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = true;
        _dragStart = e.GetPosition(this);
        RootBorder.CaptureMouse();
    }

    private void Border_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            RootBorder.ReleaseMouseCapture();
            PositionChanged?.Invoke(Left, Top);
        }
    }

    private void Border_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging) return;

        var currentPos = e.GetPosition(this);
        Left += currentPos.X - _dragStart.X;
        Top += currentPos.Y - _dragStart.Y;
    }
}
