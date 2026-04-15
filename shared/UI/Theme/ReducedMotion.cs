using System.Windows;
using System.Windows.Media;

namespace FreeAiSsd.Shared.UI.Theme;

/// <summary>
/// Honors the user's "reduce animations" preference by neutralizing the two
/// motion primitives the theme adds on top of default WPF: the 1×1 button
/// press translate (<c>ButtonPressTransform</c> resource) and the LED pulse
/// storyboard in <see cref="LedStatusIndicator"/>.
///
/// Call <see cref="Apply"/> from each WPF host's <c>OnStartup</c> after
/// <c>base.OnStartup(e)</c> so <see cref="Application.Resources"/> is
/// available.
///
/// <para>Detection reads <see cref="SystemParameters.ClientAreaAnimation"/>,
/// which reflects the OS "Show animations in Windows" toggle (Settings →
/// Accessibility → Visual effects). When the user turns animations off we
/// swap the resource for an identity transform and set a static flag that
/// <c>LedStatusIndicator</c> checks on construction to skip starting the
/// pulse storyboard on its Busy state.</para>
/// </summary>
public static class ReducedMotion
{
    /// <summary>
    /// True when the OS requests reduced animation. Read by
    /// <see cref="LedStatusIndicator"/> to decide whether to kick off the
    /// Busy-state pulse storyboard.
    /// </summary>
    public static bool IsEnabled { get; private set; }

    /// <summary>
    /// Applies the reduced-motion policy to <paramref name="app"/>. Safe to
    /// call from any thread, but expects <see cref="Application.Resources"/>
    /// to have been initialized (so call after <c>base.OnStartup(e)</c>).
    /// </summary>
    public static void Apply(Application app)
    {
        if (app is null) return;

        IsEnabled = !SystemParameters.ClientAreaAnimation;
        if (!IsEnabled) return;

        // Swap the button press translate for an identity transform so
        // IsPressed triggers no longer nudge content 1px down-right. Frozen
        // so WPF can share it across every button in the visual tree.
        var identity = new TranslateTransform(0, 0);
        if (identity.CanFreeze) identity.Freeze();
        app.Resources["ButtonPressTransform"] = identity;
    }
}
