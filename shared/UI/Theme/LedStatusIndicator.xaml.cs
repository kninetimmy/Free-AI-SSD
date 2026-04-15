using System.Windows;
using System.Windows.Controls;

namespace FreeAiSsd.Shared.UI.Theme;

/// <summary>
/// Discrete states the <see cref="LedStatusIndicator"/> can render.
/// </summary>
public enum LedState
{
    /// <summary>No activity; lamp is a dim grey dot.</summary>
    Idle,

    /// <summary>Work in progress; lamp pulses cyan.</summary>
    Busy,

    /// <summary>Healthy / completed; lamp glows green.</summary>
    Ok,

    /// <summary>Failed / alert; lamp glows magenta.</summary>
    Error
}

/// <summary>
/// Small 10×10 status lamp used to give at-a-glance feedback for
/// background activities (download, drive unlock, Ollama pull, etc.).
/// Visuals are driven entirely by the <see cref="State"/> dependency
/// property; see LedStatusIndicator.xaml for the per-state triggers.
/// </summary>
public partial class LedStatusIndicator : UserControl
{
    /// <summary>Identifies the <see cref="State"/> dependency property.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(LedState),
        typeof(LedStatusIndicator),
        new PropertyMetadata(LedState.Idle));

    /// <summary>Current lamp state. Drives fill, glow, and pulse animation.</summary>
    public LedState State
    {
        get => (LedState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public LedStatusIndicator()
    {
        InitializeComponent();
    }
}
