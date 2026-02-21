using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FreeAiSsd.Runner.Services;

namespace FreeAiSsd.Runner;

/// <summary>
/// Tiny always-on-top overlay showing the current PTT pipeline state.
/// Designed to be unobtrusive over a flight sim HUD.
///
/// States:
///   Idle     → gray dot,   "Ready"
///   Listening → red dot,    "Listening"
///   Thinking  → yellow dot, "Thinking"
///   Speaking  → green dot,  "Speaking"
///
/// The window is draggable and saves its position to config.
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

        // Freeze brushes for cross-thread safety
        GrayBrush.Freeze();
        RedBrush.Freeze();
        YellowBrush.Freeze();
        GreenBrush.Freeze();
    }

    /// <summary>
    /// Sets the initial window position from saved config values.
    /// </summary>
    public void SetPosition(double x, double y)
    {
        Left = x;
        Top = y;
    }

    /// <summary>
    /// Updates the overlay to reflect the current pipeline state.
    /// Safe to call from any thread — marshals to the UI thread.
    /// </summary>
    public void UpdateState(PttState state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateState(state));
            return;
        }

        switch (state)
        {
            case PttState.Idle:
                StatusDot.Fill = GrayBrush;
                StatusLabel.Text = "Ready";
                break;
            case PttState.Listening:
                StatusDot.Fill = RedBrush;
                StatusLabel.Text = "Listening";
                break;
            case PttState.Thinking:
                StatusDot.Fill = YellowBrush;
                StatusLabel.Text = "Thinking";
                break;
            case PttState.Speaking:
                StatusDot.Fill = GreenBrush;
                StatusLabel.Text = "Speaking";
                break;
        }
    }

    // ─── Drag support ───────────────────────────────────────────────────

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
