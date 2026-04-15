using System.Windows;

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
public partial class LedStatusIndicator : System.Windows.Controls.UserControl
{
    /// <summary>Identifies the <see cref="State"/> dependency property.</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State),
        typeof(LedState),
        typeof(LedStatusIndicator),
        new PropertyMetadata(LedState.Idle));

    /// <summary>
    /// Identifies the <see cref="AllowPulse"/> dependency property. Gated by
    /// a MultiDataTrigger in the XAML so storyboards only start when both
    /// State=Busy and AllowPulse=True.
    /// </summary>
    public static readonly DependencyProperty AllowPulseProperty = DependencyProperty.Register(
        nameof(AllowPulse),
        typeof(bool),
        typeof(LedStatusIndicator),
        new PropertyMetadata(true));

    /// <summary>Current lamp state. Drives fill, glow, and pulse animation.</summary>
    public LedState State
    {
        get => (LedState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>
    /// When false, the Busy state still turns the lamp cyan but skips the
    /// pulse opacity animation. Defaulted from <see cref="ReducedMotion"/>
    /// so Windows' "Show animations" toggle is honored automatically; can
    /// still be overridden per instance via XAML.
    /// </summary>
    public bool AllowPulse
    {
        get => (bool)GetValue(AllowPulseProperty);
        set => SetValue(AllowPulseProperty, value);
    }

    public LedStatusIndicator()
    {
        // Initialize BEFORE InitializeComponent so the XAML's initial
        // MultiDataTrigger evaluation reads the right value.
        AllowPulse = !ReducedMotion.IsEnabled;
        InitializeComponent();
    }
}
