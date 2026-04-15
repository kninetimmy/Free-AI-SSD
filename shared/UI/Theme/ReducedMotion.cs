using System.Windows.Media;

namespace FreeAiSsd.Shared.UI.Theme;

/// <summary>
/// Honors the user's "reduce animations" preference by neutralizing the two
/// motion primitives the theme adds on top of default WPF: the 1×1 button
/// press translate (<c>ButtonPressTransform</c> resource) and the LED pulse
/// storyboard in <see cref="LedStatusIndicator"/>.
///
/// Call <see cref="Apply"/> from each WPF host's <c>OnStartup</c> to swap
/// the button-press transform. <see cref="IsEnabled"/> reads the OS toggle
/// live, so controls constructed before <c>Apply</c> runs still see the
/// correct value.
///
/// <para>Detection reads <see cref="System.Windows.SystemParameters.ClientAreaAnimation"/>,
/// which reflects the OS "Show animations in Windows" toggle (Settings →
/// Accessibility → Visual effects).</para>
/// </summary>
public static class ReducedMotion
{
    /// <summary>
    /// True when the OS requests reduced animation. Read live from
    /// <see cref="System.Windows.SystemParameters.ClientAreaAnimation"/> so
    /// the value is correct even when queried before <see cref="Apply"/>
    /// has run (e.g. a <c>LedStatusIndicator</c> constructed during
    /// <c>base.OnStartup</c> via <c>StartupUri</c>).
    /// </summary>
    public static bool IsEnabled => !System.Windows.SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Applies the reduced-motion policy to <paramref name="app"/>. Must be
    /// called on the UI (STA) thread — touches <see cref="System.Windows.Application.Resources"/>
    /// and constructs a <see cref="TranslateTransform"/> (a
    /// <see cref="System.Windows.DependencyObject"/>).
    /// </summary>
    public static void Apply(System.Windows.Application app)
    {
        if (app is null) return;
        if (!IsEnabled) return;

        // Swap the button press translate for an identity transform so
        // IsPressed triggers no longer nudge content 1px down-right. Frozen
        // so WPF can share it across every button in the visual tree.
        var identity = new TranslateTransform(0, 0);
        if (identity.CanFreeze) identity.Freeze();
        app.Resources["ButtonPressTransform"] = identity;
    }
}
